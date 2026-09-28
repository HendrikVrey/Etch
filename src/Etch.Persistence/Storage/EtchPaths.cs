using System.Globalization;
using Etch.Persistence.Model;

namespace Etch.Persistence.Storage;

/// <summary>
/// Resolves every path Etch writes to.
/// </summary>
/// <remarks>
/// One type owns the layout so that no other code concatenates a path under the
/// data directory. The root is injectable purely so tests can point at a temporary
/// folder, nothing may write to a real profile during a test run.
/// </remarks>
public sealed class EtchPaths
{
    private const string BuffersFolder = "buffers";
    private const string TrashFolder = "trash";
    private const string SessionFileName = "session.json";
    private const string SettingsFileName = "settings.json";
    private const string LockFileName = ".lock";
    private const string QuarantinePrefix = "session.quarantined-";

    /// <summary>Creates a layout rooted at <paramref name="root"/>.</summary>
    /// <param name="root">An absolute path to Etch's data directory.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is blank or relative.</exception>
    public EtchPaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Path.IsPathFullyQualified(root))
        {
            // A relative root would resolve against the working directory, which for
            // a shell-launched editor is wherever the user happened to be standing.
            throw new ArgumentException("Etch's data root must be an absolute path.", nameof(root));
        }

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        BuffersDirectory = Path.Combine(Root, BuffersFolder);
        TrashDirectory = Path.Combine(Root, TrashFolder);
        SessionFile = Path.Combine(Root, SessionFileName);
        SettingsFile = Path.Combine(Root, SettingsFileName);
        LockFile = Path.Combine(Root, LockFileName);
    }

    /// <summary>The data directory, <c>%LOCALAPPDATA%\Etch</c> by default.</summary>
    public string Root { get; }

    /// <summary>Where live buffer text is stored, one file per buffer.</summary>
    public string BuffersDirectory { get; }

    /// <summary>Where closed buffers wait out their retention period.</summary>
    public string TrashDirectory { get; }

    /// <summary>The session index.</summary>
    public string SessionFile { get; }

    /// <summary>
    /// The user's settings.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> removed by <see cref="BufferStore.WipeAll"/>. Everything
    /// else under this root is buffer text or a description of it, the things somebody
    /// wiping their scratch data is trying to get rid of, whereas this file holds only
    /// preferences, contains no path and no fragment of any buffer, and losing it would
    /// be a surprise rather than a relief. It is also the file that records a retention
    /// window of zero, which is a choice a privacy-minded user would have to make twice
    /// if a wipe reset it.
    /// </remarks>
    public string SettingsFile { get; }

    /// <summary>
    /// The file whose exclusive handle marks this data directory as owned by a
    /// running Etch.
    /// </summary>
    /// <remarks>
    /// A file rather than a named mutex, deliberately. The invariant being protected
    /// is "one process writes this directory", and a lock file is scoped to exactly
    /// that: it works across terminal-server sessions, where the <c>Local\</c> object
    /// namespace does not and <c>Global\</c> needs a privilege a locked-down user may
    /// not have. Windows releases the handle when the process dies however it dies,
    /// so there is no stale lock to reap.
    /// </remarks>
    public string LockFile { get; }

    /// <summary>The default layout, under the local application data folder.</summary>
    /// <exception cref="InvalidOperationException">
    /// The local application data folder could not be resolved. Windows returns an
    /// empty string rather than throwing when the profile is not loaded (a service
    /// account, or a badly broken profile) and silently rooting Etch's storage at
    /// a relative "Etch" in the working directory would be far worse than failing.
    /// </exception>
    public static EtchPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Could not resolve the local application data folder, so Etch has nowhere to store buffers.");
        }

        return new EtchPaths(Path.Combine(localAppData, "Etch"));
    }

    /// <summary>The live file for <paramref name="id"/>.</summary>
    public string BufferFile(BufferId id) => Path.Combine(BuffersDirectory, id.FileName);

    /// <summary>
    /// The retained previous generation of <paramref name="id"/>, alongside the live file.
    /// </summary>
    /// <remarks>
    /// Same directory as the live file, deliberately, so that rotating a generation is
    /// a rename within one volume rather than a copy, and so that a wipe or a trash
    /// move only ever has one place to look.
    /// </remarks>
    public string BufferBackupFile(BufferId id) => Path.Combine(BuffersDirectory, id.BackupFileName);

    /// <summary>The trash file for <paramref name="id"/>.</summary>
    public string TrashFile(BufferId id) => Path.Combine(TrashDirectory, id.FileName);

    /// <summary>
    /// Names a file to move an unusable session index aside to.
    /// </summary>
    /// <param name="stampUtc">When the quarantine happened.</param>
    /// <param name="discriminator">
    /// A short token making the name unique. Two quarantines inside the same
    /// millisecond would otherwise collide, and the move would fail.
    /// </param>
    /// <remarks>
    /// Built from <see cref="Root"/> rather than by re-deriving the directory from
    /// the session file's path: a null directory there would silently produce a
    /// relative name, and a relative name resolves against the process working
    /// directory, which is how a copy of the user's index ends up outside the data
    /// folder entirely.
    /// </remarks>
    public string QuarantinedSessionFile(DateTimeOffset stampUtc, string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);

        // Validated, not trusted. This type exists so that nothing else concatenates a
        // path under the data root, and a caller-supplied fragment interpolated straight
        // into one would defeat that with a single `..` segment. Alphanumeric only, and
        // short.
        if (discriminator.Length > 8 || !discriminator.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                "A quarantine discriminator must be one to eight ASCII letters or digits.",
                nameof(discriminator));
        }

        var stamp = stampUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);

        return Path.Combine(Root, $"{QuarantinePrefix}{stamp}-{discriminator}.json");
    }

    /// <summary>Every quarantined session index currently on disk.</summary>
    /// <remarks>
    /// Nothing else deletes these, and each one holds a complete prior index,
    /// including tab titles and file paths, so a wipe has to be able to find them.
    /// </remarks>
    public IReadOnlyList<string> EnumerateQuarantinedSessions()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        try
        {
            return Directory.GetFiles(Root, QuarantinePrefix + "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    /// <summary>Creates the directory tree if it is not already there.</summary>
    /// <exception cref="IOException">The directories could not be created.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BuffersDirectory);
        Directory.CreateDirectory(TrashDirectory);
    }
}
