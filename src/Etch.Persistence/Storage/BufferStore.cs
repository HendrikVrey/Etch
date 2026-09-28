using System.Buffers;
using System.Text;
using Etch.Persistence.Model;

namespace Etch.Persistence.Storage;

/// <summary>
/// Stores buffer text on disk, in its two lifecycle states: live and trashed.
/// </summary>
/// <remarks>
/// Both states are held by one type because closing a tab is a rename between them
/// and splitting that across two objects would leave the atomic step owned by
/// neither. Everything here is safe to call from a background thread; nothing here
/// knows what a tab is.
/// </remarks>
public sealed class BufferStore
{
    /// <summary>
    /// Upper bound on a single buffer read, in characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Above the editor's default hard ceiling of 100 MiB, deliberately. A scratch tab is
    /// journaled whatever its size, because the journal is the only copy it has, so a
    /// buffer at the ceiling is an ordinary thing to find here, and the editor refuses to let
    /// a journaled tab grow past this number so that what it writes always comes back whole.
    /// The previous 64 Mi sat below the ceiling, so a 90 MB paste was saved and then restored
    /// cut short.
    /// </para>
    /// <para>
    /// The cap still exists for the file that gets past it anyway (hand-edited, restored
    /// from a backup, or written by a build with a raised ceiling) so that a startup restore
    /// cannot be turned into an out-of-memory failure by something on disk. A read that hits
    /// it reports <see cref="StoredBuffer.WasTruncated"/>, and a truncated buffer must never
    /// be journaled: the write-back would make the truncation permanent.
    /// </para>
    /// </remarks>
    public const int MaxBufferChars = 128 * 1024 * 1024;

    private const int BufferSize = 1 << 16;

    /// <summary>
    /// Ceiling on the buffer pre-allocated for a read.
    /// </summary>
    /// <remarks>
    /// Sizing a <see cref="StringBuilder"/> from the file length would allocate a
    /// single array of that many chars up front: 128 MB for a file at the cap, on
    /// the pre-first-frame startup path, to guard against an out-of-memory failure.
    /// Growing is slower and is the right trade here.
    /// </remarks>
    private const int MaxPreallocatedChars = 1 << 20;

    /// <summary>UTF-8 with no preamble: see the read path for why the instance matters.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly EtchPaths _paths;

    /// <summary>Creates a store over <paramref name="paths"/>.</summary>
    public BufferStore(EtchPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <summary>Creates the directory tree and clears any temporary files a crash left behind.</summary>
    /// <returns>The number of orphaned temporary files removed.</returns>
    public int Initialise()
    {
        _paths.EnsureCreated();

        return AtomicFile.SweepTemporaryFiles(_paths.BuffersDirectory)
            + AtomicFile.SweepTemporaryFiles(_paths.TrashDirectory)
            + AtomicFile.SweepTemporaryFiles(_paths.Root);
    }

    /// <summary>
    /// Writes <paramref name="text"/> as the contents of <paramref name="id"/>,
    /// keeping the previous revision alongside it.
    /// </summary>
    /// <remarks>
    /// The retained generation exists because this method runs unattended, on a
    /// debounce, with no human ever confirming a save. A single bad call from the
    /// editor layer, an empty text change raised before a tab has finished
    /// hydrating is the realistic one, would otherwise replace someone's notes with
    /// nothing, permanently and silently. One extra rename per write buys the ability
    /// to get it back.
    /// </remarks>
    /// <exception cref="IOException">The write failed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public Task WriteAsync(BufferId id, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        return AtomicFile.WriteAllTextAsync(
            _paths.BufferFile(id),
            text,
            _paths.BufferBackupFile(id),
            cancellationToken);
    }

    /// <summary>Reads the contents of <paramref name="id"/>, or null when it is not there.</summary>
    /// <remarks>
    /// <para>
    /// A missing file is a normal outcome, not an error: the session index and the
    /// buffers directory are written separately, so a crash between the two leaves an
    /// index entry with nothing behind it. The caller drops that tab and carries on.
    /// </para>
    /// <para>
    /// The retained generation is read only when the live file is absent, which is the
    /// signature of a crash inside the rename window. Preferring it any earlier would
    /// hand back stale text over good text.
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">The file exists but could not be read.</exception>
    public async Task<StoredBuffer?> ReadAsync(BufferId id, CancellationToken cancellationToken = default)
    {
        var live = await ReadFileAsync(_paths.BufferFile(id), cancellationToken).ConfigureAwait(false);

        // A live file with content always wins. The generation is consulted when the live
        // file is missing, a process that died inside the rename window, and also when
        // it is present but empty, which is the signature of the bad write the generation
        // exists to survive. Without the second case the good revision sits on disk
        // permanently unreachable behind an empty tab.
        if (live is { Text.Length: > 0 })
        {
            return live;
        }

        var backup = await ReadFileAsync(_paths.BufferBackupFile(id), cancellationToken).ConfigureAwait(false);

        if (backup is { Text.Length: > 0 } recovered)
        {
            return recovered with { RecoveredFromBackup = true };
        }

        // Genuinely empty, or gone. An empty buffer is a legitimate state, a new tab,
        // so the live result is returned as-is rather than treated as a failure.
        return live;
    }

    /// <summary>Reads a trashed buffer, or null when it is not there.</summary>
    public Task<StoredBuffer?> ReadTrashedAsync(BufferId id, CancellationToken cancellationToken = default) =>
        ReadFileAsync(_paths.TrashFile(id), cancellationToken);

    /// <summary>True when text exists for <paramref name="id"/>, live or recoverable.</summary>
    public bool Exists(BufferId id) =>
        File.Exists(_paths.BufferFile(id)) || File.Exists(_paths.BufferBackupFile(id));

    /// <summary>
    /// Deletes live text outright, without trashing it.
    /// </summary>
    /// <remarks>
    /// Not the close path: this exists to undo a write that lost a race with a close
    /// or a suppression, where the file was recreated after the trash move. The
    /// retained generation goes with it: leaving it behind would resurrect the buffer
    /// on the next launch through the very fallback that makes a crash recoverable,
    /// and for an ephemeral tab it would leave on disk the one thing that must never
    /// be written there. Returns false rather than throwing, because the caller is
    /// already handling one problem.
    /// </remarks>
    public bool DeleteLive(BufferId id)
    {
        var deletedLive = TryDelete(_paths.BufferFile(id));
        var deletedBackup = TryDelete(_paths.BufferBackupFile(id));

        return deletedLive || deletedBackup;
    }

    /// <summary>
    /// Every buffer with text on disk, whether or not the session index mentions it.
    /// </summary>
    /// <remarks>
    /// This is what makes a lost or corrupt <c>session.json</c> survivable. The index
    /// records tab order and titles; the text is the part that actually matters, and
    /// it can always be recovered by looking at what is really there, including a
    /// buffer that exists only as a retained generation because the process died
    /// between the rotate and the publish.
    /// </remarks>
    public IReadOnlyList<BufferId> EnumerateLive()
    {
        var live = Enumerate(_paths.BuffersDirectory, BufferId.Extension, BufferId.TryParseFileName);

        var orphanedBackups = Enumerate(
            _paths.BuffersDirectory,
            BufferId.BackupExtension,
            BufferId.TryParseBackupFileName);

        if (orphanedBackups.Count == 0)
        {
            return live;
        }

        var all = new List<BufferId>(live);
        var seen = new HashSet<BufferId>(live);

        foreach (var id in orphanedBackups)
        {
            if (seen.Add(id))
            {
                all.Add(id);
            }
        }

        return all;
    }

    /// <summary>Every trashed buffer, with the time it was trashed.</summary>
    public IReadOnlyList<TrashedBuffer> EnumerateTrash()
    {
        var directory = new DirectoryInfo(_paths.TrashDirectory);

        if (!directory.Exists)
        {
            return [];
        }

        var results = new List<TrashedBuffer>();

        try
        {
            // DirectoryInfo rather than Directory.EnumerateFiles: the timestamp comes
            // back in the same find data as the name, so this is one syscall per entry
            // instead of two, on the startup path.
            foreach (var file in directory.EnumerateFiles("*" + BufferId.Extension, SearchOption.TopDirectoryOnly))
            {
                if (!BufferId.TryParseFileName(file.Name, out var id))
                {
                    continue;
                }

                var trashedAt = file.LastWriteTimeUtc;

                // The 1601 sentinel means the timestamp could not be read, usually a
                // file that vanished mid-enumeration. Treating it as maximally old
                // would hand it straight to the next prune.
                if (trashedAt.Year <= 1601)
                {
                    continue;
                }

                results.Add(new TrashedBuffer(id, new DateTimeOffset(trashedAt, TimeSpan.Zero)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Enumeration is best-effort; whatever was found before the failure is
            // still a valid, if partial, answer.
        }

        results.Sort(static (left, right) => right.TrashedAtUtc.CompareTo(left.TrashedAtUtc));
        return results;
    }

    /// <summary>
    /// Moves a closed buffer to the trash, or deletes it when the policy says so.
    /// </summary>
    /// <param name="id">The buffer being closed.</param>
    /// <param name="policy">Retention. A zero window deletes instead of trashing.</param>
    /// <param name="nowUtc">The moment of the close, which is when retention starts.</param>
    /// <returns>True when the buffer was trashed and can be reopened; false when it was deleted or absent.</returns>
    /// <remarks>
    /// Closing must never fail loudly: it is bound to <c>Ctrl+W</c> and the user has
    /// already moved on. A buffer that cannot be moved is left where it is, which
    /// wastes disk but loses nothing.
    /// </remarks>
    public bool Trash(BufferId id, RetentionPolicy policy, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var backup = _paths.BufferBackupFile(id);

        // Normally the live file. When it is missing but a retained generation is not,
        // the process died inside the rename window and that generation is the only
        // copy of the text: trashing it is what keeps Ctrl+Shift+T honest in exactly
        // the case where the user is most likely to need it.
        var source = File.Exists(_paths.BufferFile(id)) ? _paths.BufferFile(id)
            : File.Exists(backup) ? backup
            : null;

        if (source is null)
        {
            return false;
        }

        if (policy.DeletesOnClose)
        {
            // Deleted outright, so it is not reopenable. Reporting false here is what
            // stops the UI offering a "reopen closed tab" that would silently do
            // nothing: the user chose zero retention and the interface has to agree.
            TryDelete(source);
            TryDelete(backup);
            return false;
        }

        var destination = _paths.TrashFile(id);

        try
        {
            Directory.CreateDirectory(_paths.TrashDirectory);

            // Overwrite: an id can only repeat if the same buffer is trashed twice,
            // and in that case the newer text is the one worth keeping.
            File.Move(source, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }

        // The trashed copy is now the canonical one. A generation left behind in the
        // buffers directory would be adopted as a recovered tab on the next launch,
        // resurrecting a tab the user closed with text one revision out of date.
        TryDelete(backup);

        // Retention has to run from the close, not from the last edit. File.Move
        // preserves the last-write time, so without this stamp a note last touched
        // nine days ago would already be expired the instant it was closed, and the
        // next launch would delete it. The buffers with the most time invested in
        // them would get the least protection, which is precisely backwards for the
        // mechanism that makes closing a tab safe to do without confirmation.
        try
        {
            File.SetLastWriteTimeUtc(destination, nowUtc.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move succeeded, which is the part that matters. A stale timestamp
            // means this buffer expires early or late, not that it is lost.
        }

        return true;
    }

    /// <summary>Moves a trashed buffer back to live storage.</summary>
    /// <returns>True when the buffer was restored.</returns>
    public bool Restore(BufferId id)
    {
        var source = _paths.TrashFile(id);

        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(_paths.BuffersDirectory);

            // Not overwrite: a live file with this id means the buffer is already
            // open, and replacing it would discard whatever the user has typed since.
            File.Move(source, _paths.BufferFile(id), overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Deletes every trashed buffer that has outlived <paramref name="policy"/>.</summary>
    /// <param name="policy">The retention window.</param>
    /// <param name="nowUtc">The current time, supplied so the sweep is testable.</param>
    /// <returns>The number of buffers deleted.</returns>
    public int PruneTrash(RetentionPolicy policy, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var pruned = 0;

        foreach (var entry in EnumerateTrash())
        {
            if (!policy.IsExpired(entry.TrashedAtUtc, nowUtc))
            {
                continue;
            }

            if (TryDelete(_paths.TrashFile(entry.Id)))
            {
                pruned++;
            }
        }

        return pruned;
    }

    /// <summary>
    /// Deletes every trace of the user's buffers: live text, trashed text, the session
    /// index, quarantined indexes, and any partial writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "wipe all scratch data" affordance. Etch stores plaintext in the user's
    /// profile and people paste credentials into scratchpads, so there has to be a way
    /// to make that go away that does not involve explaining where the folder is.
    /// </para>
    /// <para>
    /// The session index is included because it holds tab titles and, for file
    /// buffers, full paths: a tab called <c>prod-db-password</c> surviving a wipe
    /// would defeat the point. Quarantined indexes are included for the same reason:
    /// nothing else ever deletes them.
    /// </para>
    /// <para>
    /// <strong>This is not a secure erase.</strong> It unlinks files. The bytes remain
    /// until their blocks are reused, small files may live on inside the MFT, and
    /// shadow copies keep whole prior versions. Say so plainly wherever this is
    /// offered, and note that the atomic write-and-rename strategy means every
    /// revision was written to fresh blocks, so there are more recoverable copies
    /// rather than fewer. The only real answer for a secret is not to write it at all.
    /// </para>
    /// <para>
    /// Callers must stop the journal first. Deleting the files while the writer still
    /// holds their text in memory puts the secret straight back on disk at the next
    /// debounce.
    /// </para>
    /// </remarks>
    public WipeResult WipeAll()
    {
        var deleted = 0;
        var failed = 0;

        void Attempt(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (TryDelete(path))
            {
                deleted++;
            }
            else
            {
                failed++;
            }
        }

        foreach (var id in EnumerateLive())
        {
            Attempt(_paths.BufferFile(id));

            // The retained generation holds a full prior revision of the same text.
            // Wiping the live file and leaving that behind would defeat the point
            // entirely, and it is precisely the copy a user would not think to look
            // for.
            Attempt(_paths.BufferBackupFile(id));
        }

        foreach (var entry in EnumerateTrash())
        {
            Attempt(_paths.TrashFile(entry.Id));
        }

        Attempt(_paths.SessionFile);

        foreach (var quarantined in _paths.EnumerateQuarantinedSessions())
        {
            Attempt(quarantined);
        }

        deleted += AtomicFile.SweepTemporaryFiles(_paths.BuffersDirectory);
        deleted += AtomicFile.SweepTemporaryFiles(_paths.TrashDirectory);
        deleted += AtomicFile.SweepTemporaryFiles(_paths.Root);

        return new WipeResult(deleted, failed);
    }

    private delegate bool FileNameParser(ReadOnlySpan<char> fileName, out BufferId id);

    // Returns the concrete List rather than IReadOnlyList: it is private, both callers
    // only ever enumerate it, and the interface bought nothing but a layer of dispatch.
    private static List<BufferId> Enumerate(string directory, string extension, FileNameParser parse)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var results = new List<BufferId>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + extension, SearchOption.TopDirectoryOnly))
            {
                if (parse(Path.GetFileName(path.AsSpan()), out var id))
                {
                    results.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Partial answers beat exceptions during startup restore.
        }

        return results;
    }

    private static async Task<StoredBuffer?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        FileStream stream;

        try
        {
            // FileShare.Delete as well as ReadWrite: without it, a read in progress
            // blocks the journal's rename-over-the-destination and manufactures a
            // spurious write failure out of a completely ordinary interleaving.
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            // A byte-order-mark-free encoding instance, and that is what does the work
            // here, not the detect flag. StreamReader sets its internal preamble check
            // from the encoding's own preamble, independently of
            // detectEncodingFromByteOrderMarks, so passing Encoding.UTF8 would strip a
            // leading EF BB BF whatever the flag said. Etch writes these files without a
            // mark, so any such bytes are the user's own text, a pasted U+FEFF, and the
            // journal would write the stripped version straight back, losing the
            // character permanently.
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, BufferSize);

            var length = stream.Length;

            var (text, wasTruncated) = await ReadCappedAsync(reader, length, cancellationToken)
                .ConfigureAwait(false);

            return new StoredBuffer(text, wasTruncated, length);
        }
    }

    private static async Task<(string Text, bool WasTruncated)> ReadCappedAsync(
        StreamReader reader,
        long lengthHint,
        CancellationToken cancellationToken)
    {
        // Halved because the hint is a byte count and this is a char count; clamped
        // because pre-sizing is an optimisation and an optimisation must never be the
        // thing that exhausts memory.
        var capacity = (int)Math.Clamp(lengthHint / 2, BufferSize, MaxPreallocatedChars);

        var builder = new StringBuilder(capacity);
        var buffer = ArrayPool<char>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return (builder.ToString(), false);
                }

                var remaining = MaxBufferChars - builder.Length;

                if (read > remaining)
                {
                    // Never split a surrogate pair: a lone surrogate becomes U+FFFD on
                    // the next write and corrupts the character silently.
                    if (remaining > 0 && char.IsHighSurrogate(buffer[remaining - 1]))
                    {
                        remaining--;
                    }

                    builder.Append(buffer, 0, remaining);
                    return (builder.ToString(), true);
                }

                builder.Append(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
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

/// <summary>Buffer text read back from disk.</summary>
/// <param name="Text">The contents.</param>
/// <param name="WasTruncated">
/// True when the file was larger than the read cap and the tail was dropped. The
/// user has to be told, and the buffer must not be journaled: auto-save would write
/// the truncated text back over the original and make the loss permanent.
/// </param>
/// <param name="SizeInBytes">
/// The length of the file it was read from. Reported rather than recomputed from
/// <paramref name="Text"/>: the caller needs it to decide which editor features this
/// document may switch on, and re-encoding the whole buffer to count its bytes would
/// be a full extra pass over it on the startup path.
/// </param>
/// <param name="RecoveredFromBackup">
/// True when the live file was gone and the retained previous generation was read
/// instead: the signature of a process that died inside the rename window. The text
/// is one revision behind, which is worth a quiet line in the status bar and nothing
/// more.
/// </param>
public readonly record struct StoredBuffer(
    string Text,
    bool WasTruncated,
    long SizeInBytes,
    bool RecoveredFromBackup = false);

/// <summary>A closed buffer waiting out its retention period.</summary>
/// <param name="Id">Identifies the buffer.</param>
/// <param name="TrashedAtUtc">When it was closed, which is when retention starts.</param>
public readonly record struct TrashedBuffer(BufferId Id, DateTimeOffset TrashedAtUtc);

/// <summary>The outcome of wiping stored data.</summary>
/// <param name="Deleted">Files removed.</param>
/// <param name="Failed">
/// Files that could not be removed: locked by another process, or permission denied.
/// Non-zero means data the user asked to destroy is still on disk, and saying so is
/// the whole reason this is not just a count.
/// </param>
public readonly record struct WipeResult(int Deleted, int Failed)
{
    /// <summary>True when everything the user asked to remove is gone.</summary>
    public bool IsComplete => Failed == 0;
}
