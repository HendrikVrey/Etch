using System.Text;

namespace Etch.Persistence.Storage;

/// <summary>
/// Writes a file so that a crash mid-write cannot leave it half-written.
/// </summary>
/// <remarks>
/// <para>
/// Etch has no save dialog, which means the user never confirms that their text
/// reached the disk. That removes the moment where a human would notice a failure,
/// so the write itself has to be the thing that cannot fail halfway. The recipe is
/// the standard one and every step of it is load-bearing:
/// </para>
/// <list type="number">
/// <item>write the new contents to a uniquely-named temporary file;</item>
/// <item>flush that file all the way to the disk;</item>
/// <item>rename it over the destination in a single filesystem operation.</item>
/// </list>
/// <para>
/// Opening the destination and truncating it is the thing this exists to avoid: for
/// the whole duration of that write the file on disk is neither the old contents nor
/// the new ones, and a power cut in that window leaves a zero-byte file where
/// someone's notes used to be.
/// </para>
/// </remarks>
public static class AtomicFile
{
    private const string TemporaryExtension = ".tmp";
    private const int BufferSize = 1 << 16;

    /// <summary>Backoff between rename attempts, in milliseconds. Short — this is an AV window, not an outage.</summary>
    private static readonly int[] PublishRetryDelaysMs = [10, 40, 120];

    /// <summary>UTF-8 without a byte-order mark — what every tool expects of a text file.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="contents"/>, atomically.
    /// </summary>
    /// <param name="path">The destination. Its directory must already exist.</param>
    /// <param name="contents">The text to write, encoded as UTF-8 with no BOM.</param>
    /// <param name="cancellationToken">Cancels the write before the rename.</param>
    /// <remarks>
    /// Cancellation is honoured only up to the rename. Once the rename has happened
    /// the new contents are live and there is nothing sensible to undo, so the token
    /// is not checked afterwards.
    /// </remarks>
    /// <exception cref="IOException">The file could not be written or renamed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        // A unique temporary name, not "<destination>.tmp". Two Etch processes
        // writing the same buffer would otherwise share one scratch file and
        // interleave their bytes into it, and the rename would publish the mixture.
        // Orphans left by a crash are swept by SweepTemporaryFiles.
        var temporaryPath = string.Concat(path, ".", Path.GetRandomFileName().AsSpan(0, 8), TemporaryExtension);

        try
        {
            await WriteTemporaryAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            Publish(temporaryPath, path);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static async Task WriteTemporaryAsync(
        string temporaryPath,
        string contents,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, Utf8NoBom, BufferSize, leaveOpen: true);

            await using (writer.ConfigureAwait(false))
            {
                await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Pushes the bytes past the operating system's write cache and onto the
            // device. Without this the rename can be journaled while the contents are
            // still only in memory, which after a power cut produces exactly the
            // zero-byte file the temp-and-rename dance was meant to prevent.
            //
            // It costs a real disk round trip. That is affordable here because the
            // journal debounces and coalesces, so this runs at most a couple of times
            // a second per buffer rather than once per keystroke.
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Renames the temporary file over the destination, retrying briefly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>File.Move(overwrite: true)</c> rather than <c>File.Replace</c>, deliberately.
    /// <c>ReplaceFile</c> has documented failure states — <c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c>,
    /// <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c> — in which the destination has already
    /// been destroyed, which is a data-loss window this code exists to close.
    /// <c>MoveFileEx</c> with <c>MOVEFILE_REPLACE_EXISTING</c> has no such state and is
    /// atomic within a volume; both paths are in the same directory by construction,
    /// so the cross-volume copy fallback never applies. The usual argument for
    /// <c>Replace</c> — that it preserves the destination's ACLs — does not apply
    /// either: everything written here is a file Etch itself created under its own
    /// data directory. A user's own file is never written through.
    /// </para>
    /// <para>
    /// The retry is for antivirus. Real-time scanners routinely hold a handle on a
    /// freshly written file for a few milliseconds, which surfaces as a sharing
    /// violation on the rename. Without a retry that becomes a spurious write failure
    /// on an ordinary Windows desktop — and on the shutdown flush there is no second
    /// chance to get it right.
    /// </para>
    /// </remarks>
    private static void Publish(string temporaryPath, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < PublishRetryDelaysMs.Length)
            {
                Thread.Sleep(PublishRetryDelaysMs[attempt]);
            }
            catch (UnauthorizedAccessException) when (attempt < PublishRetryDelaysMs.Length)
            {
                Thread.Sleep(PublishRetryDelaysMs[attempt]);
            }
        }
    }

    /// <summary>
    /// Deletes temporary files abandoned by a process that died mid-write.
    /// </summary>
    /// <param name="directory">The directory to sweep. Missing directories are ignored.</param>
    /// <returns>The number of files deleted.</returns>
    /// <remarks>
    /// Best-effort by design: a leftover scratch file wastes a few kilobytes and
    /// nothing else, so a failure to remove one must never propagate into startup.
    /// A file another process is actively writing is skipped, because it is still
    /// open and the delete fails — which is the correct outcome.
    /// </remarks>
    public static int SweepTemporaryFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var swept = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*" + TemporaryExtension, SearchOption.TopDirectoryOnly))
            {
                // Windows wildcard matching on a three-character extension also
                // matches longer ones that begin with it, so "*.tmp" comes back
                // holding "notes.tmpl". This is the only unconditional delete in the
                // layer; it does not get to run on a name it did not actually match.
                if (!Path.GetExtension(file.AsSpan()).Equals(TemporaryExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryDelete(file))
                {
                    swept++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The directory became unreadable mid-enumeration. Whatever was swept
            // before that stays swept.
        }

        return swept;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
