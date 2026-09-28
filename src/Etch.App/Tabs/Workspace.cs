using System.Collections.ObjectModel;
using System.Text;
using Etch.App.Diagnostics;
using Etch.App.Editor;
using Etch.Core.Documents;
using Etch.Persistence.Journal;
using Etch.Persistence.Model;
using Etch.Persistence.Session;
using Etch.Persistence.Storage;
using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Tabs;

/// <summary>
/// The open tabs, and everything that keeps them on disk.
/// </summary>
/// <remarks>
/// <para>
/// This is where Etch's central promise is actually kept: no save dialog, no
/// unsaved-changes prompt, no destructive close. All three follow from the same
/// three mechanisms, every edit is journaled, every close is a move to the trash,
/// and every launch restores whatever is on disk without asking. Nothing here should
/// ever raise a question the user has to answer.
/// </para>
/// <para>
/// Lives on the UI thread. Persistence calls are awaited or handed to the journal;
/// none of them block a frame. Failures are reported through
/// <see cref="Notice"/> rather than thrown, because there is no caller above this
/// one who could do anything more useful with an exception than the status bar can.
/// </para>
/// </remarks>
internal sealed class Workspace : IAsyncDisposable
{
    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for an index write already in flight.
    /// </summary>
    /// <remarks>
    /// Short on purpose. What is being waited for is the session index (tab order and
    /// titles, never text) and the cost of waiting too long is an editor that will not
    /// close.
    /// </remarks>
    private static readonly TimeSpan IndexDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly BufferStore _buffers;
    private readonly SessionStore _sessions;
    private readonly JournalWriter _journal;
    private readonly TimeProvider _time;

    /// <summary>
    /// The knobs this workspace runs with.
    /// </summary>
    /// <remarks>
    /// Not readonly, because the settings panel can change two of them while Etch is
    /// running. Both are read at the point of use rather than captured (retention on
    /// every close, the size policy on every open) so replacing this record is enough
    /// to change the behaviour, and no tab has to be reloaded for it to take effect.
    /// <para>
    /// The journal's own options are the exception and stay fixed for the life of the
    /// process: they are baked into <see cref="JournalWriter"/> at construction, and
    /// nothing in the settings panel offers to change them.
    /// </para>
    /// </remarks>
    private WorkspaceOptions _options;

    private readonly ObservableCollection<BufferTab> _tabs = [];
    private readonly List<BufferId> _reopenHistory = [];
    private readonly Dictionary<BufferId, EventHandler> _changeHandlers = [];

    private readonly Dictionary<BufferId, BufferRecord> _closedRecords = [];

    private BufferTab? _active;

    /// <summary>
    /// Whether this process may write the session index.
    /// </summary>
    /// <remarks>
    /// False until the restore has read what is on disk, not true. The window is shown
    /// before the restore runs and the dispatcher pumps input while it awaits, so a
    /// single Ctrl+N in that gap would otherwise rewrite <c>session.json</c> with one
    /// record <em>before</em> the previous session had been read: destroying tab order,
    /// titles, pins and the active tab for every tab the user had open. The text would
    /// survive as orphans, and every tab would come back called "Recovered".
    /// </remarks>
    private bool _canSaveIndex;
    private bool _sessionSaveInFlight;
    private bool _sessionSaveRequested;
    private long _activationSequence;
    private int _disposed;

    /// <summary>
    /// Serialises every write to <c>session.json</c>, from whichever path asks for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RequestSessionSave"/> already coalesces its own overlapping requests,
    /// but shutdown writes the index too and does not go through it, so a background
    /// save started a moment earlier could still be publishing when shutdown published
    /// over it. Two atomic writes to one path is a race whose loser is decided by the
    /// filesystem, and the loser was as likely to be the shutdown snapshot as the stale
    /// one.
    /// </para>
    /// <para>
    /// Held only across <c>ConfigureAwait(false)</c> awaits, and that is load-bearing
    /// rather than stylistic. On the session-end path the UI thread is blocked waiting
    /// for <see cref="CompleteShutdownAsync"/>; if a save could sit on this gate while
    /// waiting to resume on that thread, the logoff would deadlock and take the text it
    /// was saving with it.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    /// <summary>Stamped on the UI thread when an index snapshot is taken.</summary>
    private long _indexRevision;

    /// <summary>The newest revision actually on disk. Read and written under <see cref="_indexGate"/> only.</summary>
    private long _publishedIndexRevision;

    /// <summary>
    /// Set once the shutdown snapshot has been taken, after which nothing may queue a
    /// newer one: the tab list it would capture is the same, but it would land with
    /// <c>CleanShutdown: false</c> and turn an orderly exit into a reported crash.
    /// </summary>
    private bool _shutdownPrepared;

    private readonly SynchronizationContext? _uiContext;

    private Workspace(EtchPaths paths, WorkspaceOptions options, TimeProvider time)
    {
        _options = options;
        _time = time;
        _buffers = new BufferStore(paths);
        _sessions = new SessionStore(paths, time);

        // Captured on the UI thread, which is the only thread that constructs a
        // workspace. The journal reports its failures from its own thread and has no
        // business knowing about a dispatcher, so this is where the hop back happens.
        _uiContext = SynchronizationContext.Current;

        // Handing out an instance method from a constructor would be a problem if the
        // callee could invoke it before construction finished. It cannot: nothing is
        // written until Start(), which the caller invokes after the restore.
        _journal = new JournalWriter(_buffers, options.Journal, time, OnJournalFailure);

        Tabs = new ReadOnlyObservableCollection<BufferTab>(_tabs);
    }

    /// <summary>Raised when a message should be shown quietly in the status bar.</summary>
    public event Action<string>? Notice;

    /// <summary>Raised when the active tab changes.</summary>
    public event Action<BufferTab?>? ActiveChanged;

    /// <summary>The open tabs, in display order.</summary>
    public ReadOnlyObservableCollection<BufferTab> Tabs { get; }

    /// <summary>The tab in front, or null when none are open.</summary>
    public BufferTab? Active => _active;

    /// <summary>True when a closed tab is available to reopen.</summary>
    public bool CanReopenClosed => _reopenHistory.Count > 0;

    /// <summary>
    /// True when anything typed has not yet reached the disk.
    /// </summary>
    /// <remarks>
    /// The honest question for a saved indicator, and not the same as "is anything
    /// queued": a batch already handed to the writer has left the queue but has not
    /// landed.
    /// </remarks>
    public bool HasUnsavedWork => _journal.HasUnsavedWork;

    /// <summary>
    /// Restores the previous session, or starts a new one.
    /// </summary>
    /// <remarks>
    /// One path, taken on every launch, whether the last session ended cleanly or was
    /// killed. A separate recovery branch would run rarely, be tested rarely, and be
    /// broken exactly when it was finally needed.
    /// <para>
    /// Only the active tab is hydrated here. The rest carry their metadata and read
    /// their text the first time they are shown, which is what keeps the restore
    /// budget independent of how many tabs are open.
    /// </para>
    /// </remarks>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        BufferId? activeId = null;

        try
        {
            var restored = await new SessionRestorer(_buffers, _sessions)
                .RestoreAsync(_options.Retention, _time.GetUtcNow(), cancellationToken)
                .ConfigureAwait(true);

            _canSaveIndex = restored.CanSaveIndex;
            activeId = restored.ActiveBufferId;

            foreach (var record in restored.Buffers)
            {
                Attach(BufferTab.FromRecord(record));
            }

            NormalisePinOrder();
            SeedReopenHistory();
            ReportRestore(restored);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only or full profile directory. Told plainly rather than logged
            // quietly: the user is about to start typing into an editor that cannot save,
            // and the entire premise of the application is that they do not have to think
            // about saving.
            DiagnosticLog.WriteFailure("restore", ex);
            Announce($"Etch could not read its storage, so nothing was restored and nothing will be saved: {ex.Message}");
        }
        finally
        {
            // In a finally, and after the restore has read what is on disk, the journal
            // must not publish over a buffer this process has not looked at yet, but it
            // must also start even when the restore failed, or auto-save is silently off
            // for the whole session while the status bar still says "Saving…".
            _journal.Start();
        }

        // The window is on screen while this runs and the dispatcher is pumping input, so
        // the user may already have pressed Ctrl+N and started typing. Their tab wins:
        // restoring on top of it would swap the document out mid-sentence, splitting what
        // they wrote across two buffers with no indication.
        if (_active is not null)
        {
            return;
        }

        var active = activeId is { } id
            ? _tabs.FirstOrDefault(tab => tab.Id == id) ?? (_tabs.Count > 0 ? _tabs[0] : null)
            : _tabs.Count > 0 ? _tabs[0] : null;

        if (active is null)
        {
            // First launch, everything closed last time, or a restore that failed. An
            // editor that opens with no tab at all is a broken-looking editor.
            active = NewScratch();
        }

        await ActivateAsync(active, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds an empty workspace, touching no disk.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RestoreAsync"/> so the window can be constructed and
    /// shown before anything is read. Restoring first would put a file read in front of
    /// the first frame, which is the one thing the startup budget cannot afford, and
    /// the plan already calls for exactly this order: render, then hydrate.
    /// </remarks>
    public static Workspace Create(
        EtchPaths paths,
        WorkspaceOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new Workspace(paths, options ?? WorkspaceOptions.Default, timeProvider ?? TimeProvider.System);
    }

    /// <summary>
    /// Adopts settings the user changed while Etch was running.
    /// </summary>
    /// <param name="settings">Already sanitised by <see cref="EtchSettings.Sanitised"/>.</param>
    /// <remarks>
    /// <para>
    /// Only the two settings this layer actually reads. The typeface belongs to the
    /// editor control and the associations belong to the registry; neither has any
    /// business travelling through the workspace to reach the thing that owns it.
    /// </para>
    /// <para>
    /// The size policy is rebuilt rather than mutated, and it applies to documents opened
    /// from here on. Re-evaluating the tabs already open would mean revoking journaling
    /// from a buffer the user has been typing into on the strength of a number they just
    /// changed, so a tab keeps the capabilities it was opened with until it is reopened,
    /// which is both simpler and the safer direction to be wrong in.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The thresholds are not strictly ascending. <see cref="EtchSettings.Sanitised"/>
    /// guarantees they are, so reaching this means an unsanitised value was passed and
    /// the right answer is to fail loudly rather than to construct a policy nobody chose.
    /// </exception>
    public void ApplySettings(EtchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _options = _options with
        {
            Retention = settings.TrashRetentionDays == 0
                ? RetentionPolicy.DeleteImmediately
                : new RetentionPolicy(TimeSpan.FromDays(settings.TrashRetentionDays)),
            SizePolicy = new DocumentSizePolicy(
                settings.ReducedThresholdBytes,
                settings.PlainTextThresholdBytes,
                settings.HardCeilingBytes),
        };
    }

    /// <summary>
    /// Deletes everything Etch has stored, and empties the window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "wipe all scratch data" command. <see cref="BufferStore.WipeAll"/> has existed
    /// since M1 and had no caller until now.
    /// </para>
    /// <para>
    /// <b>The order is the whole of the correctness here, and there are two writers to
    /// shut out, not one.</b> Pending journal batches are dropped first and waited for,
    /// because a batch already taken by the writer would otherwise complete <em>after</em>
    /// the delete and put the text the user asked to destroy back on disk. The session
    /// index is the second: it is written by its own fire-and-forget loop, and a snapshot
    /// taken before the wipe would republish <c>session.json</c>, which holds every
    /// buffer's id and its <em>user-authored tab title</em>, over the file that had just
    /// been deleted. Both are dealt with before a single file is unlinked.
    /// </para>
    /// <para>
    /// Then every tab is detached, without trashing it, which is the one place in Etch
    /// where closing a tab is destructive, and it is destructive because that is precisely
    /// what was asked for. Only then are the files unlinked, and a fresh empty tab created
    /// so the user is left with somewhere to type rather than an empty, disabled editor.
    /// </para>
    /// </remarks>
    public async Task<WipeResult> WipeAllAsync(CancellationToken cancellationToken = default)
    {
        // Before anything is deleted. See the remarks: this is what makes "nothing is in
        // flight" true rather than likely.
        _ = await _journal.DiscardAllAsync(cancellationToken).ConfigureAwait(true);

        foreach (var tab in _tabs.ToArray())
        {
            Unsubscribe(tab);

            // So that a tab which was suppressed does not leave a stale entry behind
            // pointing at a buffer that is about to stop existing.
            _journal.Discard(tab.Id);
        }

        SetActive(null);
        _tabs.Clear();

        // Nothing on these lists survives the files they refer to, and both hold buffer
        // identifiers and titles.
        _reopenHistory.Clear();
        _closedRecords.Clear();

        var result = await WipeStorageAsync(cancellationToken).ConfigureAwait(true);

        // Left with somewhere to type rather than an empty, disabled editor. NewScratch
        // activates the tab and requests the index save itself, so the file that lands
        // describes what the user is actually looking at rather than the empty moment
        // between the wipe and this line.
        _ = NewScratch();

        return result;
    }

    /// <summary>
    /// Deletes the files, with the index writer shut out for the duration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate is the same one every index write takes, so holding it here means no
    /// snapshot can publish while the delete is running. Advancing
    /// <see cref="_publishedIndexRevision"/> under it then disposes of the queued ones:
    /// <see cref="WriteIndexAsync"/> already discards any snapshot whose revision is not
    /// newer than the published mark, so bumping the mark past every revision handed out
    /// so far invalidates all of them at once. That mechanism was built for shutdown; a
    /// wipe wants exactly the same guarantee.
    /// </para>
    /// <para>
    /// <c>++_indexRevision</c> rather than reading it: a snapshot captured on this same
    /// turn, before the wipe began, holds the current value, and the mark has to be
    /// strictly above it for the <c>&lt;=</c> test to reject it.
    /// </para>
    /// </remarks>
    private async Task<WipeResult> WipeStorageAsync(CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            _publishedIndexRevision = ++_indexRevision;

            return _buffers.WipeAll();
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>Creates and activates a new empty scratch tab.</summary>
    public BufferTab NewScratch()
    {
        var tab = BufferTab.NewScratch(
            ScratchTitles.NextAvailable(_tabs.Select(existing => existing.Title)),
            _time.GetUtcNow());

        Attach(tab);
        SetActive(tab);

        // An empty buffer file is written immediately rather than waiting for the first
        // keystroke. Without it a tab that is created and renamed but never typed into has
        // no text on disk, so the restore drops it as an index entry with nothing behind
        // it, and the rename goes with it.
        //
        // Only when the index is writable, though. When it is not, a newer build's
        // session file is on disk and this one must not overwrite it, every Ctrl+N would
        // otherwise leave an empty file that no index will ever mention, and the next
        // launch would adopt each one as a bogus "Recovered" tab.
        if (_canSaveIndex)
        {
            Journal(tab);
        }

        RequestSessionSave();

        return tab;
    }

    /// <summary>
    /// Opens a file in a new tab, or activates the tab that already has it open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reusing the existing tab is not a nicety. Two tabs over one file would journal
    /// to two shadow copies and then race each other on <c>Ctrl+S</c>, and the user
    /// would have no way to tell which one won.
    /// </para>
    /// <para>
    /// Which makes comparing path strings the wrong test, because Windows hands the
    /// same file back under several spellings: an 8.3 short name, a junction, a mapped
    /// drive that is really a UNC share, a hard link. <see cref="FileIdentity"/> asks
    /// the filesystem instead. The path comparison is kept as a fallback for tabs whose
    /// identity was never established, which is what a session restore leaves behind.
    /// </para>
    /// </remarks>
    public async Task<BufferTab?> OpenFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Probed before the load rather than after, so a 300 MB file that is already
        // open is not read a second time only to be thrown away. The file is opened
        // again a moment later for the read itself; if it is swapped in between, the
        // identity stored on the tab is the one from the read, which is the truth.
        var identity = FileIdentity.TryRead(path, out var probed) ? probed : default;

        var existing = _tabs.FirstOrDefault(tab => IsSameFile(tab, path, identity));

        if (existing is not null)
        {
            await ActivateAsync(existing, cancellationToken).ConfigureAwait(true);
            return existing;
        }

        DocumentLoadResult result;

        // Captured, not read at each use. The settings panel can change the thresholds
        // mid-load (Ctrl+, works while a large file is being read) and a document
        // loaded under a 100 MB ceiling that was then evaluated against a 10 MB one would
        // be refused after it was already in memory. A tab keeps the policy it was opened
        // with, which is what ApplySettings promises.
        var sizePolicy = _options.SizePolicy;

        try
        {
            result = await DocumentLoader.LoadAsync(path, sizePolicy, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OutOfMemoryException)
        {
            Announce($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }

        if (result is DocumentLoadResult.Refused refused)
        {
            Announce(refused.Reason);
            return null;
        }

        var loaded = ((DocumentLoadResult.Loaded)result).Document;

        var record = new BufferRecord(
            BufferId.New(),
            BufferKind.File,
            Path.GetFileName(loaded.Path) is { Length: > 0 } name ? name : loaded.Path,
            loaded.Path,
            caretOffset: 0,
            firstVisibleLine: 1,
            isPinned: false,
            formatOverride: null,
            lastModifiedUtc: _time.GetUtcNow());

        var tab = BufferTab.FromRecord(record);
        tab.Hydrate(loaded.Text, loaded.Capabilities, loaded.WasTruncated, recoveredFromBackup: false);
        tab.AdoptFileMetadata(loaded.Encoding, loaded.LineEnding, loaded.Capabilities);
        tab.AdoptFileState(loaded.Identity, loaded.Witness);

        Attach(tab);
        SetActive(tab);

        // The shadow copy is seeded immediately rather than waiting for the first
        // edit: until it exists, a crash loses the fact that this file was open at
        // all, and the tab comes back empty.
        Journal(tab);

        RequestSessionSave();

        if (loaded.WasTruncated)
        {
            Announce($"{tab.Title} was truncated at the size ceiling - auto-save is off so the rest of the file is not at risk.");
        }
        else if (loaded.Capabilities.Notice is { } notice)
        {
            Announce(notice);
        }

        return tab;
    }

    /// <summary>
    /// Makes <paramref name="tab"/> the front tab, hydrating it if necessary.
    /// </summary>
    public async Task ActivateAsync(BufferTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!_tabs.Contains(tab))
        {
            return;
        }

        // Holding Ctrl+Tab starts an activation per repeat, each awaiting a flush and a
        // possible file read. Without a sequence number whichever finishes last wins, so
        // the editor can settle on a tab other than the one the user stopped on.
        var request = ++_activationSequence;

        // Leaving a tab is one of the plan's immediate-flush moments: the user's
        // attention has moved on, which is exactly when an unwritten edit is most likely
        // to be forgotten and least likely to be retyped.
        if (!ReferenceEquals(_active, tab))
        {
            await FlushAsync(cancellationToken).ConfigureAwait(true);
        }

        await HydrateAsync(tab, cancellationToken).ConfigureAwait(true);

        // Hydration still counts even when a newer request has overtaken this one: the
        // text is read and cached either way. Only the decision about what is in front
        // is stale.
        if (request != _activationSequence || !_tabs.Contains(tab))
        {
            return;
        }

        SetActive(tab);
        RequestSessionSave();
    }

    /// <summary>
    /// Reads a tab's text from disk if it has not been read yet.
    /// </summary>
    /// <remarks>
    /// A tab whose text has gone missing is not an error worth a dialog. The index
    /// and the buffer files are written separately, so a crash between them leaves
    /// exactly this state; hydrating it as empty and saying so is the honest
    /// outcome.
    /// </remarks>
    public async Task HydrateAsync(BufferTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.IsHydrated)
        {
            return;
        }

        StoredBuffer? stored = null;

        // Captured before the await, for the reason given in OpenFileAsync: the settings
        // panel can change the thresholds while this read is in flight.
        var sizePolicy = _options.SizePolicy;

        try
        {
            stored = await _buffers.ReadAsync(tab.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            DiagnosticLog.WriteFailure("hydrate", ex);
            Announce($"Could not read the text for {tab.Title}: {ex.Message}");
        }

        tab.Hydrate(
            stored?.Text ?? string.Empty,
            sizePolicy.Evaluate(stored?.SizeInBytes ?? 0),
            stored?.WasTruncated ?? false,
            stored?.RecoveredFromBackup ?? false);

        // Decided at hydration and enforced in the layer that does the writing, so
        // that a later Enqueue from anywhere cannot undo it.
        _journal.SetSuppressed(tab.Id, !tab.IsJournaled);

        Subscribe(tab);

        if (tab.RecoveredFromBackup)
        {
            Announce($"{tab.Title} was recovered from its previous revision - the last few seconds of edits may be missing.");
        }
        else if (tab.WasTruncated)
        {
            Announce($"{tab.Title} was too large to load in full. Auto-save is off for it, so nothing on disk will be overwritten.");
        }
    }

    /// <summary>
    /// Closes a tab without asking anything.
    /// </summary>
    /// <remarks>
    /// The text moves to the trash, where it stays for the retention window, and the
    /// tab goes onto the reopen stack. This is the mechanism that makes "no
    /// confirmation dialogs" safe rather than reckless: close is not destructive, so
    /// there is nothing to confirm.
    /// </remarks>
    public async Task CloseAsync(BufferTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var index = _tabs.IndexOf(tab);

        if (index < 0)
        {
            return;
        }

        // Captured before the flush, for the reason given in OpenFileAsync. The close the
        // user asked for is the one they were looking at when they pressed Ctrl+W, and
        // retention changing underneath it would decide whether their text goes to the
        // trash or straight to deletion.
        var retention = _options.Retention;

        // Written before it is moved, or the last few seconds of typing go to the
        // trash without ever having reached the file being trashed.
        await FlushAsync(cancellationToken).ConfigureAwait(true);

        Unsubscribe(tab);
        _journal.Discard(tab.Id);

        // An ephemeral tab was never written and must not start being written now.
        // Trashing it would put the one thing it promised to keep off disk into the one
        // place that keeps things for a week. Deleted rather than merely skipped: if the
        // suppression or the earlier delete failed for any reason, a file left in the
        // buffers directory is adopted as a recovered tab on the next launch, and the
        // content resurrects across a restart.
        bool recoverable;

        if (tab.IsEphemeral)
        {
            _buffers.DeleteLive(tab.Id);
            recoverable = false;
        }
        else
        {
            recoverable = _buffers.Trash(tab.Id, retention, _time.GetUtcNow());
        }

        _tabs.RemoveAt(index);

        if (recoverable)
        {
            // Kept so that reopening restores what the tab actually was. Without it a
            // closed file tab comes back as an untitled scratch buffer holding the file's
            // text, and Ctrl+S no longer writes through to the file it came from.
            _closedRecords[tab.Id] = tab.ToRecord();
            PushReopen(tab.Id);
        }

        if (ReferenceEquals(_active, tab))
        {
            var next = _tabs.Count == 0
                ? NewScratch()
                : _tabs[Math.Min(index, _tabs.Count - 1)];

            await ActivateAsync(next, cancellationToken).ConfigureAwait(true);
        }

        RequestSessionSave();
    }

    /// <summary>Reopens the most recently closed tab.</summary>
    /// <returns>The reopened tab, or null when there was nothing to reopen.</returns>
    public async Task<BufferTab?> ReopenLastClosedAsync(CancellationToken cancellationToken = default)
    {
        while (_reopenHistory.Count > 0)
        {
            var id = _reopenHistory[^1];
            _reopenHistory.RemoveAt(_reopenHistory.Count - 1);

            // Already open: the same buffer can reach the stack twice if it was
            // closed, reopened by hand, and closed again.
            if (_tabs.Any(tab => tab.Id == id))
            {
                continue;
            }

            // Before the restore, not after. Closing put the id in the journal's discard
            // set, and a write taken before the close that completes after this would see
            // it there and delete the file that was just moved back out of the trash,
            // where the only copy now lives, because Restore moves rather than copies.
            _journal.Revive(id);

            if (!_buffers.Restore(id))
            {
                // Swept by retention, or removed from under us. Try the next one down
                // rather than reporting a failure the user cannot act on, but put the
                // discard back first, or a write taken before the close could still
                // publish and leave an orphan for the next launch to adopt.
                _journal.Discard(id);
                _closedRecords.Remove(id);
                continue;
            }

            // The record from this session's close when there is one, so a reopened file
            // tab is still a file tab, with its path and title. Across a restart there is
            // no record to recover, and it comes back as scratch holding the same text.
            var record = _closedRecords.Remove(id, out var closed)
                ? closed
                : new BufferRecord(
                    id,
                    BufferKind.Scratch,
                    ScratchTitles.NextAvailable(_tabs.Select(existing => existing.Title)),
                    filePath: null,
                    caretOffset: 0,
                    firstVisibleLine: 1,
                    isPinned: false,
                    formatOverride: null,
                    lastModifiedUtc: _time.GetUtcNow());

            var tab = BufferTab.FromRecord(record);

            Attach(tab);
            await ActivateAsync(tab, cancellationToken).ConfigureAwait(true);

            return tab;
        }

        Announce("There is no recently closed tab left to reopen.");
        return null;
    }

    /// <summary>
    /// Moves a tab to a new position in the strip.
    /// </summary>
    /// <param name="tab">The tab being dragged.</param>
    /// <param name="targetIndex">Where it should land, as an index into <see cref="Tabs"/>.</param>
    /// <remarks>
    /// <para>
    /// A drag never crosses the pin boundary. Pinned tabs are a group that stays to the
    /// left of the rest, so the target is clamped to the dragged tab's own group: letting
    /// the drag pin or unpin as a side effect would make one gesture do two things, and
    /// the one the user did not intend is the one that survives into the next session.
    /// </para>
    /// <para>
    /// <see cref="ObservableCollection{T}.Move"/> removes and reinserts, so passing the
    /// index of the tab currently under the pointer produces the expected feel without any
    /// arithmetic here: dragging rightwards past a tab lands after it, leftwards lands
    /// before it.
    /// </para>
    /// </remarks>
    public void Move(BufferTab tab, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(tab);

        var current = _tabs.IndexOf(tab);

        if (current < 0)
        {
            return;
        }

        var (first, last) = GroupRange(tab.IsPinned);
        var target = Math.Clamp(targetIndex, first, last);

        if (target == current)
        {
            return;
        }

        _tabs.Move(current, target);
        RequestSessionSave();
    }

    /// <summary>
    /// Pins a tab to the front of the strip, or releases it.
    /// </summary>
    /// <remarks>
    /// The tab moves to join its new group rather than staying where it was. Leaving it in
    /// place would break the invariant every other part of this relies on, that pinned
    /// tabs occupy a contiguous run at the front, and the first drag afterwards would
    /// clamp against a boundary that does not match what is on screen.
    /// </remarks>
    public void SetPinned(BufferTab tab, bool pinned)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.IsPinned == pinned || !_tabs.Contains(tab))
        {
            return;
        }

        tab.IsPinned = pinned;

        // Counted after the flag is set, so it already includes this tab when pinning and
        // already excludes it when unpinning. Pinning lands it last among the pinned;
        // unpinning lands it first among the rest. Either way it ends up beside the tabs
        // it now belongs with, and its relative order within that group is the newest.
        var pinnedCount = _tabs.Count(static existing => existing.IsPinned);
        var destination = pinned ? pinnedCount - 1 : pinnedCount;

        var current = _tabs.IndexOf(tab);

        if (current != destination)
        {
            _tabs.Move(current, destination);
        }

        RequestSessionSave();
    }

    /// <summary>The first and last index a tab of the given pin state may occupy.</summary>
    private (int First, int Last) GroupRange(bool pinned)
    {
        var pinnedCount = _tabs.Count(static tab => tab.IsPinned);

        return pinned
            ? (0, Math.Max(pinnedCount - 1, 0))
            : (pinnedCount, Math.Max(_tabs.Count - 1, pinnedCount));
    }

    /// <summary>
    /// Brings pinned tabs to the front, preserving their relative order.
    /// </summary>
    /// <remarks>
    /// Normally a no-op: the index is written in display order and pinned tabs are already
    /// at the front of it. It is not decoration, though. A session file written by an
    /// older build, or edited by hand, can interleave them, and every subsequent drag
    /// would then clamp against a boundary that does not exist on screen, which looks like
    /// tabs refusing to move for no reason.
    /// </remarks>
    private void NormalisePinOrder()
    {
        var insertAt = 0;

        for (var i = 0; i < _tabs.Count; i++)
        {
            if (!_tabs[i].IsPinned)
            {
                continue;
            }

            if (i != insertAt)
            {
                _tabs.Move(i, insertAt);
            }

            insertAt++;
        }
    }

    /// <summary>
    /// Marks a tab as ephemeral, or lifts the mark.
    /// </summary>
    /// <remarks>
    /// Turning it on has to do three things, and doing two of them would be worse
    /// than doing none: stop the journal, delete what has already been written, and
    /// keep the tab out of the session index, the index carries the title, and a tab
    /// called <c>prod-db-password</c> surviving a restart would defeat the point on
    /// its own.
    /// </remarks>
    public void SetEphemeral(BufferTab tab, bool ephemeral)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.IsEphemeral == ephemeral)
        {
            return;
        }

        tab.IsEphemeral = ephemeral;
        _journal.SetSuppressed(tab.Id, !tab.IsJournaled);

        if (ephemeral)
        {
            _buffers.DeleteLive(tab.Id);
            Announce($"{tab.Title} is ephemeral - nothing in it will be written to disk, including on exit.");
        }
        else
        {
            Journal(tab);
            Announce($"{tab.Title} is being saved again.");
        }

        RequestSessionSave();
    }

    /// <summary>
    /// Writes a file tab through to the user's own file.
    /// </summary>
    /// <remarks>
    /// The only place Etch writes outside its own data directory, and the only write
    /// the user explicitly asks for. Everything else journals to a shadow copy:
    /// silently overwriting someone's <c>appsettings.json</c> because they scrolled
    /// through it is the one way continuous auto-save becomes a liability.
    /// </remarks>
    /// <returns>True when the file was written.</returns>
    public async Task<bool> SaveThroughAsync(BufferTab tab, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (tab.Kind != BufferKind.File || tab.FilePath is not { } path)
        {
            return false;
        }

        if (tab.WasTruncated)
        {
            // The buffer holds a prefix of the file. Writing it back is not a save, it
            // is a deletion of everything past the cap.
            Announce($"{tab.Title} was only partly loaded, so it cannot be written back over the original.");
            return false;
        }

        if (tab.Document is not { } document)
        {
            return false;
        }

        if (!await ConfirmOverwriteAsync(tab, path, cancellationToken).ConfigureAwait(true))
        {
            return false;
        }

        try
        {
            await DocumentWriter.WriteThroughAsync(path, document.CreateSnapshot(), tab.Encoding, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.WriteFailure("save-through", ex);
            Announce($"Could not save {tab.Title}: {ex.Message}");
            return false;
        }

        // Re-read after the write, so the tab's idea of the file is Etch's own output.
        // Skipping this would make the next Ctrl+S report this save as somebody else's
        // change, and a warning that cries wolf is worse than none, because the user
        // learns to press through it.
        await RefreshFileStateAsync(tab, path, cancellationToken).ConfigureAwait(true);

        Announce($"Saved {tab.Title}.");
        return true;
    }

    /// <summary>
    /// Checks whether anything else has written to the file since Etch last read it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A refusal and a status-bar line rather than a modal dialog, per the UI rules, and
    /// pressing <c>Ctrl+S</c> again goes through. That is a real confirmation and not a
    /// speed bump: the second press is armed against the exact on-disk state the message
    /// described, so if the file changes again in between, the warning returns.
    /// </para>
    /// <para>
    /// The probe runs off the UI thread for the same reason
    /// <see cref="DocumentWriter.WriteThroughAsync"/> does: opening a file is synchronous
    /// however the handle is configured, and a share that has just gone away makes the
    /// open alone last for the SMB timeout. A save guard that freezes the window is not
    /// an improvement on the bug it guards against.
    /// </para>
    /// <para>
    /// <b>Known limit, stated rather than hidden.</b> A tab restored from a previous
    /// session has no witness: the session index records a path and not a timestamp, and
    /// the text comes from the journal so the file is never read. A change made while
    /// Etch was <i>closed</i> is therefore not detected, and the first save after a
    /// restore merely establishes the witness, which is a shame, because "changed while
    /// the editor was shut" is the likelier case of the two. Closing it means persisting
    /// the witness in the session index, which is a schema change and is not attempted
    /// here rather than being approximated with the buffer's own edit time.
    /// </para>
    /// </remarks>
    /// <returns>True when the write may proceed.</returns>
    private async Task<bool> ConfirmOverwriteAsync(BufferTab tab, string path, CancellationToken cancellationToken)
    {
        if (tab.Witness is not { } expected)
        {
            return true;
        }

        var actual = await Task.Run(() => DiskState.Read(path), cancellationToken).ConfigureAwait(true);

        if (actual.Matches(expected))
        {
            tab.ArmOverwrite(null);
            return true;
        }

        // Armed against this exact state by a previous refusal, so this is the user
        // pressing Ctrl+S a second time having read the message. A null arm never
        // matches, because DiskState is a value even when the file is missing.
        if (tab.OverwriteArmedFor == actual)
        {
            tab.ArmOverwrite(null);
            return true;
        }

        tab.ArmOverwrite(actual);

        Announce(actual.Presence switch
        {
            DiskPresence.Present => $"{tab.Title} has changed on disk since Etch opened it. Ctrl+S again to overwrite it.",
            DiskPresence.Missing => $"{tab.Title} no longer exists on disk. Ctrl+S again to write it out again.",
            _ => $"{tab.Title} could not be read - it may be open in another program. Ctrl+S again to write over it.",
        });

        return false;
    }

    /// <summary>Re-reads the file's identity and witness after Etch has written to it.</summary>
    /// <remarks>
    /// A failure here clears the state rather than leaving the old one. The stale witness
    /// describes the file as it was <i>before</i> Etch's own save, so keeping it would
    /// make the very next Ctrl+S report this write as somebody else's change: the
    /// failure this method exists to prevent, reached down its error path.
    /// </remarks>
    private static async Task RefreshFileStateAsync(BufferTab tab, string path, CancellationToken cancellationToken)
    {
        var refreshed = await Task.Run(() => ReadFileState(path), cancellationToken).ConfigureAwait(true);

        if (refreshed is { } state)
        {
            tab.AdoptFileState(state.Identity, state.Witness);
            tab.ArmOverwrite(null);
            return;
        }

        tab.ForgetFileState();
    }

    /// <summary>Identity and witness together, or null when either could not be read.</summary>
    private static (FileIdentity Identity, FileWitness Witness)? ReadFileState(string path) =>
        FileIdentity.TryRead(path, out var identity) && FileWitness.TryRead(path, out var witness)
            ? (identity, witness.Value)
            : null;

    /// <summary>Promotes a scratch tab to a file the user has chosen, and writes it.</summary>
    public async Task<bool> SaveAsAsync(BufferTab tab, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            tab.PromoteToFile(path, tab.Encoding);
        }
        catch (ArgumentException ex)
        {
            Announce($"Etch will not save to that path: {ex.Message}");
            return false;
        }

        RequestSessionSave();

        return await SaveThroughAsync(tab, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Whether <paramref name="tab"/> is already over the file at <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Identity when both sides have one, path otherwise. The fallback is reached by
    /// tabs restored from a session index, which records a path and not an identity,
    /// and by files on a filesystem that will not supply an id.
    /// </remarks>
    private static bool IsSameFile(BufferTab tab, string path, FileIdentity identity)
    {
        if (tab.Kind != BufferKind.File)
        {
            return false;
        }

        // Either test is enough, and the path is not merely a fallback for when the
        // identity is unknown. A file id does not survive delete-and-recreate, and
        // rename-over-temp is how almost everything saves: git checkout, VS Code,
        // Notepad, most build tools. Stopping at "both ids known and different" would
        // therefore open a second tab over a path already open the moment another tool
        // wrote to it, which is the two-tabs-one-file race this method exists to stop,
        // arriving by a new route.
        return (identity.IsKnown && tab.Identity.IsKnown && tab.Identity == identity)
            || string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the current text of <paramref name="tab"/> for writing.</summary>
    public void Journal(BufferTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        if (!tab.IsJournaled || tab.TakeSnapshot() is not { } snapshot)
        {
            return;
        }

        tab.LastModifiedUtc = _time.GetUtcNow();
        _journal.Enqueue(tab.Id, BufferContent.FromSnapshot(snapshot));
    }

    /// <summary>Writes everything pending, now.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _journal.FlushAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("flush", ex);
            Announce($"Auto-save is failing: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the session index, coalescing overlapping requests.
    /// </summary>
    /// <remarks>
    /// Structural changes (new tab, close, activate, rename, pin) are user-paced,
    /// so this does not need a debounce timer. It does need to not overlap with
    /// itself: holding Ctrl+Tab would otherwise start a write per frame, and two
    /// concurrent atomic writes to one path is a race with no winner worth having.
    /// </remarks>
    public void RequestSessionSave()
    {
        if (!_canSaveIndex || _shutdownPrepared || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        if (_sessionSaveInFlight)
        {
            _sessionSaveRequested = true;
            return;
        }

        _sessionSaveInFlight = true;
        _ = RunSessionSaveAsync();
    }

    private async Task RunSessionSaveAsync()
    {
        try
        {
            do
            {
                _sessionSaveRequested = false;
                await SaveSessionAsync(CancellationToken.None).ConfigureAwait(true);
            }
            while (_sessionSaveRequested && !_shutdownPrepared && Volatile.Read(ref _disposed) == 0);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception would surface as an
            // unobserved one at some arbitrary later garbage collection: attributed
            // to nothing, and long after the thing that caused it.
            DiagnosticLog.WriteFailure("session-save-loop", ex);
        }
        finally
        {
            _sessionSaveInFlight = false;
        }
    }

    /// <summary>
    /// Reports a failed auto-save, quietly the first time and plainly if it persists.
    /// </summary>
    /// <remarks>
    /// Called on the journal's thread. The escalation matters: a single failed write
    /// is usually an antivirus scanner holding a handle and is fixed by the retry, and
    /// interrupting someone's typing for it would be worse than useless. A run of them
    /// means auto-save is genuinely not working, and the entire premise of the app is
    /// that the user does not have to think about saving, so at that point they have
    /// to be told.
    /// </remarks>
    private void OnJournalFailure(JournalFailure failure)
    {
        const int PersistentFailureThreshold = 3;

        if (failure.ConsecutiveFailures < PersistentFailureThreshold)
        {
            DiagnosticLog.WriteFailure("journal", failure.Exception);
            return;
        }

        // Only on crossing the threshold, not on every failure past it. A disk that is
        // not coming back would otherwise repaint the status bar until the user closes
        // the window.
        if (failure.ConsecutiveFailures != PersistentFailureThreshold)
        {
            return;
        }

        DiagnosticLog.WriteFailure("journal", failure.Exception);

        var message = $"Auto-save is not working: {failure.Exception.Message} Copy anything you need elsewhere.";

        if (_uiContext is { } context)
        {
            context.Post(state => Announce((string)state!), message);
        }
        else
        {
            Announce(message);
        }
    }

    /// <summary>Writes the session index once.</summary>
    public async Task SaveSessionAsync(CancellationToken cancellationToken = default)
    {
        // _shutdownPrepared is checked here and not only in RequestSessionSave because
        // this method is public: a snapshot taken after the shutdown one would carry a
        // *higher* revision, so the guard below could not discard it, and it would
        // legitimately replace the clean-shutdown record with cleanShutdown false.
        if (!_canSaveIndex || _shutdownPrepared || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        var (revision, snapshot) = CaptureIndex(cleanShutdown: false);

        await WriteIndexAsync(revision, snapshot, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the tab list into a snapshot and stamps it with a revision.
    /// </summary>
    /// <remarks>
    /// Synchronous and UI-thread-only, because it reads UI state. Separated from the
    /// write so that the write itself needs nothing from this thread: see
    /// <see cref="_indexGate"/>.
    /// </remarks>
    private (long Revision, SessionSnapshot Snapshot) CaptureIndex(bool cleanShutdown)
    {
        // Ephemeral tabs are excluded outright. The index holds titles and, for file
        // tabs, full paths: exactly the kind of thing someone marks a tab ephemeral
        // to keep off the disk.
        var records = _tabs
            .Where(static tab => !tab.IsEphemeral)
            .Select(static tab => tab.ToRecord())
            .ToArray();

        var active = _active is { IsEphemeral: false } current ? current.Id : (BufferId?)null;

        return (
            ++_indexRevision,
            new SessionSnapshot(
                SessionSnapshot.CurrentVersion,
                cleanShutdown,
                ActiveBufferId: active,
                Buffers: records));
    }

    /// <summary>
    /// Publishes an index snapshot, unless a newer one has already landed.
    /// </summary>
    /// <remarks>
    /// The revision check is what makes ordering a property of the code rather than of
    /// the semaphore's release order, which is not documented to be fair. Without it a
    /// snapshot taken before shutdown could still acquire the gate afterwards and
    /// overwrite the clean-shutdown record with a stale tab list.
    /// </remarks>
    private async Task WriteIndexAsync(long revision, SessionSnapshot snapshot, CancellationToken cancellationToken)
    {
        // Before the wait, not only after it. DisposeAsync gives up on a wedged write
        // after a bounded interval and leaves it running, still holding the gate, so a
        // caller arriving afterwards would queue behind exactly the write that was
        // already judged not worth waiting for.
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Queued before the workspace was disposed, arriving after. Publishing now
            // would rename a file over session.json behind a caller that has already
            // been told this workspace is finished with the directory.
            if (revision <= _publishedIndexRevision || Volatile.Read(ref _disposed) == 1)
            {
                return;
            }

            // Handed to the pool rather than awaited from here, and that is structural
            // rather than stylistic. An uncontended WaitAsync completes synchronously,
            // so without this the write would begin on the UI thread with the
            // dispatcher context installed, holding the gate, and the only thing
            // stopping a continuation from posting back to a thread that is blocked
            // waiting for this gate would be that every await two assemblies away
            // happens to say ConfigureAwait(false). Task.Run makes that a property of
            // the code instead of a convention. The cost is one dispatch per index
            // write, which is user-paced: a tab created, closed, renamed or switched.
            // The disposal check is re-taken inside the delegate because the one above is
            // taken when the work is *queued*. A starved thread pool can leave it sitting
            // for longer than DisposeAsync is willing to wait, and it would then start
            // touching the disk after dispose had returned.
            await Task.Run(
                    () => Volatile.Read(ref _disposed) == 1
                        ? Task.CompletedTask
                        : _sessions.SaveAsync(snapshot, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            _publishedIndexRevision = revision;
        }
        catch (OperationCanceledException)
        {
            // Advanced here for the same reason as in the I/O handler below: a cancelled
            // write is a failed *newer* write, and on the session-end path cancellation
            // is the expected failure rather than an exotic one. Leaving the revision
            // behind would let an older queued snapshot publish cleanShutdown false over
            // an orderly exit.
            _publishedIndexRevision = revision;
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is the cheapest thing here to lose: tab order and titles, not
            // text. Failing loudly would be out of proportion to the harm.
            //
            // The revision still advances. It means "nothing older than this may land",
            // not "this reached the disk", and if a failed shutdown write left it
            // behind, the older snapshot queued underneath would then be free to publish
            // cleanShutdown false over an orderly exit, which is the outcome this whole
            // mechanism exists to prevent, arriving down the error path.
            _publishedIndexRevision = revision;
            DiagnosticLog.WriteFailure("session-save", ex);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    /// <summary>
    /// Flushes everything and records that this shutdown was orderly.
    /// </summary>
    /// <remarks>
    /// The clean-shutdown flag changes nothing about what the next launch does, it
    /// restores either way, so it is a diagnostic, not a branch. That is deliberate:
    /// a recovery path taken only after a crash is a recovery path that has never
    /// been tested when it matters.
    /// </remarks>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var plan = PrepareShutdown();

        await CompleteShutdownAsync(plan, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Does the half of shutdown that needs the UI thread, and returns the rest as data.
    /// </summary>
    /// <remarks>
    /// The split exists for the session-end path. Taking a document snapshot has thread
    /// affinity and reading the tab list is UI state, but everything after that is file
    /// I/O, and when Windows is logging the user off, the caller has to block the
    /// dispatcher while that I/O finishes. Blocking on a task whose continuations post
    /// back to the dispatcher deadlocks, so the two halves have to be separable: this one
    /// is synchronous and touches only UI state, and
    /// <see cref="CompleteShutdownAsync"/> touches no UI state at all and never resumes
    /// on the dispatcher.
    /// </remarks>
    public ShutdownPlan PrepareShutdown()
    {
        foreach (var tab in _tabs)
        {
            Journal(tab);
        }

        var (revision, snapshot) = CaptureIndex(cleanShutdown: true);

        // After the snapshot, not before: a save requested between the two would capture
        // the same tabs but carry a lower revision, so it could only ever be discarded.
        _shutdownPrepared = true;

        return new ShutdownPlan(_canSaveIndex, revision, snapshot);
    }

    /// <summary>
    /// Writes everything <see cref="PrepareShutdown"/> gathered.
    /// </summary>
    /// <remarks>
    /// Every await here is <c>ConfigureAwait(false)</c>, deliberately and load-bearingly:
    /// the session-end path runs this from a thread-pool thread while the UI thread is
    /// blocked waiting for it, and a single continuation posted back to the dispatcher
    /// would hang the user's logoff and lose the very text this is writing.
    /// </remarks>
    public async Task CompleteShutdownAsync(ShutdownPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            await _journal.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write("Etch - the final flush ran out of time; some recent edits may not have been written.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("shutdown-flush", ex);
        }

        if (!plan.CanSaveIndex)
        {
            return;
        }

        try
        {
            // Through the same gate as every other index write, so a background save
            // still in flight finishes before this one replaces it rather than racing
            // it. The gate is never held across a resumption onto the UI thread, which
            // is what makes this safe to await while that thread is blocked on it.
            await WriteIndexAsync(plan.IndexRevision, plan.Index, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // The only kind that reaches here: WriteIndexAsync swallows the I/O failures
            // itself, and a cancelled wait on the gate is what a session-end deadline
            // running out looks like.
            DiagnosticLog.WriteFailure("session-shutdown", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        foreach (var tab in _tabs)
        {
            Unsubscribe(tab);
        }

        // Disposing the journal runs its own final flush, which is the last chance
        // anything unwritten has.
        await _journal.DisposeAsync().ConfigureAwait(false);

        // Then wait out any index write still in flight. RequestSessionSave is
        // fire-and-forget, so without this a disposed workspace can still be renaming a
        // file over session.json, which a caller that goes on to read that file, or to
        // delete the directory, has no way to have anticipated.
        //
        // Bounded, and the bound is not decoration. Background index writes carry
        // CancellationToken.None, and the atomic write ends in a synchronous
        // FlushFileBuffers; against a redirected profile directory on a share that has
        // gone away, that blocks for as long as the network stack takes to give up. An
        // unbounded wait here would be the last thing on the close path, so Etch would
        // simply never quit, and the write it is waiting for is the session index, the
        // cheapest thing it stores.
        //
        // Acquired and released rather than disposed: a SemaphoreSlim that never handed
        // out a wait handle has nothing to dispose, and disposing it would only turn a
        // queued write into an ObjectDisposedException on a thread with nobody to report
        // it to. A write that has not started yet is stopped by the _disposed checks in
        // WriteIndexAsync; one that is already writing when the timeout expires is not
        // stopped at all, it is accepted and left to finish, which is what the
        // diagnostic below records.
        if (await _indexGate.WaitAsync(IndexDrainTimeout).ConfigureAwait(false))
        {
            _indexGate.Release();
        }
        else
        {
            DiagnosticLog.Write("Etch - a session index write was still running at shutdown and was left to finish on its own.");
        }
    }

    private void Attach(BufferTab tab)
    {
        _tabs.Add(tab);

        if (tab.IsHydrated)
        {
            _journal.SetSuppressed(tab.Id, !tab.IsJournaled);
            Subscribe(tab);
        }
    }

    /// <summary>
    /// Starts journaling a tab's edits.
    /// </summary>
    /// <remarks>
    /// Only ever called once the tab holds real text. Subscribing before hydration is
    /// the bug the retained backup generation exists to survive: the editor raises a
    /// change event for the empty document it starts with, and the journal
    /// faithfully writes emptiness over the file it was about to read.
    /// </remarks>
    private void Subscribe(BufferTab tab)
    {
        if (tab.Document is not { } document || _changeHandlers.ContainsKey(tab.Id))
        {
            return;
        }

        // Held in a local and stored, rather than attached from a method group twice:
        // unsubscribing has to hand back a delegate equal to the one that went on, and
        // "equal" is a thing to be sure of rather than to reason about at a leak site.
        EventHandler handler = (_, _) => Journal(tab);

        _changeHandlers[tab.Id] = handler;
        document.TextChanged += handler;
    }

    private void Unsubscribe(BufferTab tab)
    {
        if (!_changeHandlers.Remove(tab.Id, out var handler))
        {
            return;
        }

        if (tab.Document is { } document)
        {
            document.TextChanged -= handler;
        }
    }

    private void SetActive(BufferTab? tab)
    {
        // Bumped here rather than only in ActivateAsync, so that a *direct* activation
        // (a new tab, an opened file, the tab that follows a close) also invalidates any
        // slower activation still in flight. Otherwise pressing Ctrl+N while an awaited
        // tab switch is in progress lets the older request win and swap the document out
        // from under someone who has already started typing.
        _activationSequence++;

        if (ReferenceEquals(_active, tab))
        {
            return;
        }

        if (_active is { } previous)
        {
            previous.IsActive = false;
        }

        _active = tab;

        if (tab is not null)
        {
            tab.IsActive = true;
        }

        ActiveChanged?.Invoke(tab);
    }

    private void PushReopen(BufferId id)
    {
        _reopenHistory.Remove(id);
        _reopenHistory.Add(id);

        if (_reopenHistory.Count <= _options.ReopenHistoryDepth)
        {
            return;
        }

        // Records for buffers that have fallen off the end go with them, or the map grows
        // for the life of the process holding titles and full file paths.
        foreach (var dropped in _reopenHistory.Take(_reopenHistory.Count - _options.ReopenHistoryDepth))
        {
            _closedRecords.Remove(dropped);
        }

        _reopenHistory.RemoveRange(0, _reopenHistory.Count - _options.ReopenHistoryDepth);
    }

    /// <summary>
    /// Fills the reopen stack from the trash so that Ctrl+Shift+T works across a
    /// restart.
    /// </summary>
    /// <remarks>
    /// The files are on disk for the whole retention window either way. Not seeding
    /// the stack would mean the reopen command silently does nothing on the first
    /// press after every launch, which reads as a broken shortcut rather than as an
    /// in-memory history.
    /// </remarks>
    private void SeedReopenHistory()
    {
        // Newest first from the store; reversed so the newest ends up on top of a
        // stack that is popped from the end.
        var trashed = _buffers.EnumerateTrash();

        for (var i = Math.Min(trashed.Count, _options.ReopenHistoryDepth) - 1; i >= 0; i--)
        {
            _reopenHistory.Add(trashed[i].Id);
        }
    }

    private void ReportRestore(RestoredSession restored)
    {
        if (restored.Notice is { } notice)
        {
            Announce(notice);
            return;
        }

        if (restored.RecoveredCount > 0)
        {
            Announce(restored.RecoveredCount == 1
                ? "Recovered 1 tab that was not in the session index."
                : $"Recovered {restored.RecoveredCount} tabs that were not in the session index.");
            return;
        }

        if (restored.WasUncleanShutdown)
        {
            Announce("Etch did not shut down cleanly last time. Everything was restored.");
        }
    }

    private void Announce(string message) => Notice?.Invoke(message);
}

/// <summary>
/// Everything the asynchronous half of shutdown needs, gathered on the UI thread.
/// </summary>
/// <param name="CanSaveIndex">False when a newer build's session index is on disk.</param>
/// <param name="IndexRevision">
/// Orders this write against any save still in flight, so the clean-shutdown record
/// cannot be overwritten by a snapshot taken before it.
/// </param>
/// <param name="Index">
/// The session as it stood, ephemeral tabs already excluded and the clean-shutdown flag
/// already set. A finished snapshot rather than its parts, because assembling it needs
/// the UI thread and this record is what crosses to a thread-pool one.
/// </param>
internal sealed record ShutdownPlan(
    bool CanSaveIndex,
    long IndexRevision,
    SessionSnapshot Index);
