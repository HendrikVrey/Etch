using Etch.Persistence.Model;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Session;

/// <summary>
/// Reconstructs the tab list at startup from the session index and what is actually
/// on disk.
/// </summary>
/// <remarks>
/// <para>
/// There is one restore path and it is taken on every launch, whether the last
/// session ended cleanly or was killed. That is the whole design: a separate
/// "recover your files?" branch would run rarely, be tested rarely, and be broken
/// precisely when it was finally needed.
/// </para>
/// <para>
/// The index and the buffer files are written separately and can therefore disagree.
/// Both directions of disagreement are normal after a crash and both are handled
/// without asking the user anything.
/// </para>
/// </remarks>
public sealed class SessionRestorer
{
    private readonly BufferStore _buffers;
    private readonly SessionStore _sessions;

    /// <summary>Creates a restorer.</summary>
    public SessionRestorer(BufferStore buffers, SessionStore sessions)
    {
        _buffers = buffers ?? throw new ArgumentNullException(nameof(buffers));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>Loads the index, reconciles it against disk, and sweeps expired trash.</summary>
    /// <param name="retention">Retention for the trash sweep.</param>
    /// <param name="nowUtc">The current time, supplied so restore is testable.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async Task<RestoredSession> RestoreAsync(
        RetentionPolicy retention,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retention);

        var sweptTemporaries = _buffers.Initialise();
        var load = await _sessions.LoadAsync(cancellationToken).ConfigureAwait(false);

        var live = _buffers.EnumerateLive();
        var reconciled = Reconcile(load.Session, live, nowUtc);

        var prunedFromTrash = _buffers.PruneTrash(retention, nowUtc);

        return new RestoredSession(
            reconciled.Buffers,
            reconciled.ActiveBufferId,
            load.Status,
            RecoveredCount: reconciled.RecoveredCount,
            DroppedCount: reconciled.DroppedCount,
            PrunedFromTrash: prunedFromTrash,
            SweptTemporaryFiles: sweptTemporaries,
            WasUncleanShutdown: load.Status == SessionLoadStatus.Loaded && !load.Session.CleanShutdown,
            CanSaveIndex: load.CanSave,
            Notice: load.Notice);
    }

    /// <summary>
    /// Matches index entries against the buffer files that exist.
    /// </summary>
    /// <param name="session">The index as loaded, possibly empty.</param>
    /// <param name="liveBuffers">Buffer files actually present.</param>
    /// <param name="nowUtc">Timestamp given to recovered records.</param>
    /// <remarks>
    /// Pure, so every combination of agreement and disagreement between the index and
    /// the disk can be tested without touching a filesystem.
    /// </remarks>
    public static ReconciledSession Reconcile(
        SessionSnapshot session,
        IReadOnlyList<BufferId> liveBuffers,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(liveBuffers);

        var onDisk = new HashSet<BufferId>(liveBuffers);
        var indexed = new HashSet<BufferId>(session.Buffers.Count);
        var restored = new List<BufferRecord>(session.Buffers.Count);
        var dropped = 0;

        foreach (var record in session.Buffers)
        {
            // Null elements are unreachable from SessionStore, which sanitises them
            // out — but this method is public and documented as pure, so it will be
            // called with hand-built input.
            if (record is null || !indexed.Add(record.Id))
            {
                continue;
            }

            if (onDisk.Contains(record.Id))
            {
                restored.Add(record);
                continue;
            }

            // Indexed but no text on disk. The crash landed between writing the index
            // and writing the buffer, so this tab has nothing in it. Restoring it as
            // an empty tab would look like Etch lost the contents; dropping it is the
            // honest representation of what happened.
            dropped++;
        }

        // Text on disk that the index does not mention. The reverse crash window —
        // the buffer was written and the index never caught up. This is the case
        // worth getting right, because unlike the other one there is real text to
        // save, and it is recovered without a prompt.
        var recovered = 0;

        foreach (var id in liveBuffers)
        {
            if (indexed.Contains(id))
            {
                continue;
            }

            indexed.Add(id);
            recovered++;

            restored.Add(new BufferRecord(
                id,
                BufferKind.Scratch,
                RecoveredTitle(recovered),
                filePath: null,
                caretOffset: 0,
                firstVisibleLine: 1,
                isPinned: false,
                formatOverride: null,
                lastModifiedUtc: nowUtc));
        }

        var active = session.ActiveBufferId is { } requested && indexed.Contains(requested)
            ? requested
            : restored.Count > 0 ? restored[0].Id : (BufferId?)null;

        return new ReconciledSession(restored, active, recovered, dropped);
    }

    /// <summary>
    /// A recovered buffer keeps no title, because the title lived in the index that
    /// was lost. Numbered rather than blank so two of them are distinguishable.
    /// </summary>
    private static string RecoveredTitle(int ordinal) => $"Recovered {ordinal}";
}

/// <summary>The tab list after the index and the disk have been reconciled.</summary>
/// <param name="Buffers">Tabs to restore, in order.</param>
/// <param name="ActiveBufferId">The tab to render first, or null when there are none.</param>
/// <param name="RecoveredCount">Buffers found on disk that the index did not mention.</param>
/// <param name="DroppedCount">Index entries whose text was missing.</param>
public sealed record ReconciledSession(
    IReadOnlyList<BufferRecord> Buffers,
    BufferId? ActiveBufferId,
    int RecoveredCount,
    int DroppedCount);

/// <summary>Everything startup needs to know about the session it just restored.</summary>
/// <param name="Buffers">Tabs to restore, in order.</param>
/// <param name="ActiveBufferId">
/// The tab to hydrate and render before the others, which is the single biggest
/// perceived-startup win when many tabs are open.
/// </param>
/// <param name="IndexStatus">How the session index load went.</param>
/// <param name="RecoveredCount">Buffers recovered from disk without an index entry.</param>
/// <param name="DroppedCount">Index entries with no text behind them.</param>
/// <param name="PrunedFromTrash">Closed buffers deleted for outliving their retention.</param>
/// <param name="SweptTemporaryFiles">Partial writes cleaned up from a previous crash.</param>
/// <param name="WasUncleanShutdown">
/// True when the last session did not exit cleanly. Worth a quiet line in the
/// status bar and nothing more — the restore already happened, and by the time the
/// user reads it there is nothing for them to decide.
/// </param>
/// <param name="CanSaveIndex">
/// False when a newer build's index is on disk and must not be overwritten. The
/// session still works; it just does not persist its tab layout this run.
/// </param>
/// <param name="Notice">A user-facing explanation when the index was unusable.</param>
public sealed record RestoredSession(
    IReadOnlyList<BufferRecord> Buffers,
    BufferId? ActiveBufferId,
    SessionLoadStatus IndexStatus,
    int RecoveredCount,
    int DroppedCount,
    int PrunedFromTrash,
    int SweptTemporaryFiles,
    bool WasUncleanShutdown,
    bool CanSaveIndex,
    string? Notice);
