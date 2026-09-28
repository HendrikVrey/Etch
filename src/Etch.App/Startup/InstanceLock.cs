using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Etch.Persistence.Storage;

namespace Etch.App.Startup;

/// <summary>
/// Exclusive ownership of one Etch data directory, held for the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// Two Etch processes over one data directory is silent, unbounded data loss: both
/// journal to the same buffer files and both rewrite the same session index, so the
/// loser's text is overwritten with no error anywhere. Nothing in the persistence
/// layer can detect it, each process is doing exactly what it was told, which is
/// why the guard has to sit above it, before any of that machinery starts.
/// </para>
/// <para>
/// A file handle rather than a named mutex. The invariant is about a directory, and
/// a lock file is scoped to precisely that: it holds across terminal-server sessions
/// where the <c>Local\</c> object namespace does not, and it needs none of the
/// privilege that creating a <c>Global\</c> object does. Windows drops the handle
/// when the process ends however it ends (clean exit, crash, or task-manager kill)
/// so there is no stale lock to detect or reap.
/// </para>
/// </remarks>
internal sealed class InstanceLock : IDisposable
{
    private readonly FileStream _handle;

    private InstanceLock(FileStream handle, string channelName)
    {
        _handle = handle;
        ChannelName = channelName;
    }

    /// <summary>The pipe name a second launch uses to reach this instance.</summary>
    public string ChannelName { get; }

    /// <summary>
    /// Attempts to take ownership of <paramref name="paths"/>.
    /// </summary>
    /// <param name="paths">The data directory layout to claim.</param>
    /// <param name="held">The lock, when this process won it.</param>
    /// <param name="channelName">
    /// The hand-off pipe name for this directory. Returned whether or not the lock
    /// was won, because the process that lost is exactly the one that needs it.
    /// </param>
    /// <returns>True when this process now owns the directory.</returns>
    /// <exception cref="IOException">
    /// The data directory itself could not be created. That is not a contention
    /// failure and must not be reported as one: starting a second instance because
    /// the disk is full would be the worst possible reading of it.
    /// </exception>
    public static bool TryAcquire(EtchPaths paths, out InstanceLock? held, out string channelName)
    {
        ArgumentNullException.ThrowIfNull(paths);

        channelName = ChannelNameFor(paths);
        held = null;

        Directory.CreateDirectory(paths.Root);

        FileStream handle;

        try
        {
            // FileShare.Read, not None: a process that loses the race still wants to
            // read the owner's process id so it can say something useful. Write is
            // not shared, which is what makes the open above the actual test.
            handle = new FileStream(
                paths.LockFile,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 512,
                FileOptions.None);
        }
        catch (DirectoryNotFoundException)
        {
            // Not contention. The directory was created a line ago, so this means it went
            // away underneath us, and reporting it as "already running" would send this
            // process off to hand off to a pipe that does not exist, wait out the full
            // timeout, and then refuse to start.
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by another Etch. UnauthorizedAccessException is included because a file
            // marked delete-pending, or one whose ACL was tightened, surfaces as that
            // rather than as a sharing violation, and in every one of those cases the
            // safe reading is still "do not start a second writer".
            return false;
        }

        try
        {
            // Diagnostic only. Nothing reads this to make a decision, the handle is
            // the lock, but it turns "Etch is already running" into a message the
            // user can act on when the holder has wedged.
            var stamp = string.Create(
                CultureInfo.InvariantCulture,
                $"pid={Environment.ProcessId} started={DateTimeOffset.UtcNow:O}");

            handle.Write(Encoding.UTF8.GetBytes(stamp));
            handle.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lock is held, which is the part that matters. An unwritten stamp
            // costs a slightly vaguer error message in another process, one day.
        }

        held = new InstanceLock(handle, channelName);
        return true;
    }

    /// <summary>Describes the process currently holding the lock, for an error message.</summary>
    /// <remarks>
    /// Best-effort and never throws. It is called on a path that has already failed,
    /// and a failure to read a diagnostic must not replace the real error.
    /// </remarks>
    public static string DescribeHolder(EtchPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            using var stream = new FileStream(
                paths.LockFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 512,
                FileOptions.None);

            var buffer = new byte[512];
            var read = stream.Read(buffer);

            var stamp = Encoding.UTF8.GetString(buffer, 0, read).Trim();

            if (string.IsNullOrEmpty(stamp))
            {
                return "an unidentified Etch process";
            }

            // Sanitised even though this process wrote the format: the file is on disk and
            // anything can put bytes in it, and the result goes straight into a dialog.
            var sanitised = string.Concat(stamp.Where(static character => !char.IsControl(character)));

            return sanitised.Length > 120 ? sanitised[..120] : sanitised;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "an unidentified Etch process";
        }
    }

    /// <summary>
    /// The hand-off pipe name for a data directory.
    /// </summary>
    /// <remarks>
    /// Derived from the directory rather than fixed, so that two Etch instances
    /// pointed at different data directories, a test run beside a real one, do not
    /// find each other. Hashed because a path is not a legal pipe name and because a
    /// pipe name is visible system-wide, and there is no reason to publish the user's
    /// profile path in it. Upper-cased first: Windows paths are case-insensitive, so
    /// two spellings of the same directory must hash to the same channel.
    /// </remarks>
    private static string ChannelNameFor(EtchPaths paths)
    {
        var normalised = paths.Root.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.Unicode.GetBytes(normalised));

        return string.Concat("Etch-", Convert.ToHexStringLower(digest.AsSpan(0, 16)));
    }

    /// <summary>Releases the directory.</summary>
    public void Dispose()
    {
        try
        {
            _handle.Dispose();
        }
        catch (IOException)
        {
            // The handle is going away with the process regardless. A flush failure
            // on a zero-length diagnostic file is not worth a crash on exit.
        }
    }
}
