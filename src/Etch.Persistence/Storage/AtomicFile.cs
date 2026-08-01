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
    /// <param name="backupPath">
    /// Where to move the current contents before they are replaced, or null to
    /// discard them. Must be in the same directory as <paramref name="path"/>, so
    /// that rotating a generation is a rename rather than a copy.
    /// </param>
    /// <param name="cancellationToken">Cancels the write before the rename.</param>
    /// <remarks>
    /// <para>
    /// Cancellation is honoured only up to the rename. Once the rename has happened
    /// the new contents are live and there is nothing sensible to undo, so the token
    /// is not checked afterwards.
    /// </para>
    /// <para>
    /// A <paramref name="backupPath"/> narrows the atomicity guarantee slightly and
    /// it is worth being precise about how. The destination is moved aside and then
    /// replaced, so there is a window — two metadata operations wide — in which no
    /// file exists at <paramref name="path"/>. A crash inside that window leaves the
    /// previous contents intact at the backup path rather than at the destination,
    /// which callers recover from by reading the backup when the live file is
    /// missing. What cannot happen, with or without a backup, is a half-written or
    /// zero-length file at the destination: nothing is ever opened there for writing.
    /// The net effect is strictly more recoverable than the no-backup path, never
    /// less.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The file could not be written or renamed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static async Task WriteAllTextAsync(
        string path,
        string contents,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        // Enforced rather than documented. A backup path in another directory would turn
        // the rotation from a rename into a cross-volume copy of the whole buffer, on
        // the journal's path, silently.
        if (backupPath is not null
            && !string.Equals(Path.GetDirectoryName(backupPath), Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The backup path must be in the same directory as the destination.",
                nameof(backupPath));
        }

        // A unique temporary name, not "<destination>.tmp". Two Etch processes
        // writing the same buffer would otherwise share one scratch file and
        // interleave their bytes into it, and the rename would publish the mixture.
        // Orphans left by a crash are swept by SweepTemporaryFiles.
        var temporaryPath = string.Concat(path, ".", Path.GetRandomFileName().AsSpan(0, 8), TemporaryExtension);

        try
        {
            await WriteTemporaryAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (backupPath is not null)
            {
                RotateGeneration(path, backupPath);
            }

            await PublishAsync(temporaryPath, path).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Moves the current contents of <paramref name="path"/> aside so that the write
    /// about to replace them is recoverable.
    /// </summary>
    /// <remarks>
    /// Best-effort, and that is a deliberate choice rather than an oversight. The
    /// backup is a safety net for a logic bug in the layer above — an empty text
    /// change raised before a tab has hydrated, say — not part of the durability
    /// contract. Failing the write because the previous generation could not be
    /// preserved would turn a missing safety net into the very data loss it exists to
    /// prevent.
    /// </remarks>
    private static void RotateGeneration(string path, string backupPath)
    {
        try
        {
            var current = new FileInfo(path);

            if (!current.Exists)
            {
                return;
            }

            // Never rotate an empty file over the generation. This is the whole reason
            // the generation exists: the failure being defended against is a bad write
            // of nothing, and rotating on the write *after* that one would replace the
            // good revision with the empty one and complete the loss the first write
            // started. Keeping the last non-empty revision means the good text survives
            // however many empty writes follow it.
            if (current.Length == 0)
            {
                return;
            }

            File.Move(path, backupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // The live file stays where it is and is replaced without a backup, which is
            // exactly the behaviour of the no-backup path.
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
    private static async Task PublishAsync(string temporaryPath, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < PublishRetryDelaysMs.Length)
            {
                // Awaited rather than slept. This runs on a thread-pool thread from an
                // async call path, and blocking one for up to 170 ms per write is a
                // thread the rest of the process could have used.
                await Task.Delay(PublishRetryDelaysMs[attempt]).ConfigureAwait(false);
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
