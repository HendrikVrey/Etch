using Etch.Persistence.Model;

namespace Etch.Persistence.Journal;

/// <summary>
/// Decides which buffers are due to be written, and when to look again.
/// </summary>
/// <remarks>
/// <para>
/// All of the journal's actual thinking lives here, deliberately separated from the
/// loop that does the I/O. It never reads a clock, never touches the disk and never
/// awaits: every decision is a function of the timestamps it has been handed. That
/// makes the debounce, the latency ceiling and the coalescing testable with plain
/// arithmetic instead of with sleeps, which is the difference between a suite that
/// proves something and a suite that is merely slow and flaky.
/// </para>
/// <para>
/// Coalescing is the other half of the job. A buffer with ten unwritten edits is one
/// pending write holding the newest text, not ten queued writes — so a burst of
/// typing costs the disk exactly as much as a single change.
/// </para>
/// <para>
/// Safe for concurrent use: the editor records from the UI thread while the journal
/// loop drains from a background thread.
/// </para>
/// </remarks>
public sealed class WriteScheduler
{
    private readonly JournalOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<BufferId, Entry> _pending = new();

    /// <summary>
    /// Buffers that have been closed and must not be written again.
    /// </summary>
    /// <remarks>
    /// Removing the pending entry is not enough on its own. A write that is already
    /// in flight when the tab closes would publish afterwards and recreate the file
    /// the close had just moved to the trash, leaving an orphan that the next launch
    /// adopts as a recovered tab. The journal consults this set immediately before
    /// and immediately after each write, so a close that lands mid-write still wins.
    /// </remarks>
    private readonly HashSet<BufferId> _discarded = new();

    /// <summary>
    /// Buffers that must not be written to disk at all.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="_discarded"/>, and the distinction is load-bearing.
    /// A discarded buffer has been closed, and recording it again revives it —
    /// which is correct, because reopening a closed tab should start saving it
    /// again. A suppressed buffer is one that must never reach the disk while the
    /// suppression stands, no matter how many edits arrive: a tab the user marked
    /// ephemeral, a buffer read back truncated (writing it would make the
    /// truncation permanent), or one whose size puts it past the journaling
    /// threshold. <see cref="Record"/> drops those silently rather than queueing
    /// work that would then have to be filtered out later.
    /// </remarks>
    private readonly HashSet<BufferId> _suppressed = new();

    /// <summary>Creates a scheduler.</summary>
    public WriteScheduler(JournalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>How many buffers are waiting to be written.</summary>
    /// <remarks>
    /// Counts only what has not yet been handed to the writer. A batch that is
    /// mid-flight has already left the scheduler, so this reaching zero does not mean
    /// everything is on disk — <c>JournalWriter.HasUnsavedWork</c> is the question
    /// worth asking if a "saved" indicator is ever built.
    /// </remarks>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Records that <paramref name="id"/> now reads <paramref name="text"/>.</summary>
    /// <remarks>
    /// The eager overload, for callers that already hold the text — restores, tests,
    /// and anything that is not on the keystroke path. Editors should prefer the
    /// <see cref="BufferContent"/> overload and hand over a snapshot instead.
    /// </remarks>
    public void Record(BufferId id, string text, DateTimeOffset now) =>
        Record(id, BufferContent.FromText(text), now);

    /// <summary>
    /// Records that <paramref name="id"/> has changed, and how to obtain its text.
    /// </summary>
    /// <param name="id">The buffer that changed.</param>
    /// <param name="content">Its full current contents, materialised when written.</param>
    /// <param name="now">When the change happened.</param>
    /// <remarks>
    /// Replaces any earlier unwritten content for the same buffer. The deadline for
    /// the hard latency ceiling is kept from the *first* unwritten change, not reset
    /// by this one — resetting it is what would let continuous typing postpone the
    /// write indefinitely, which is the exact failure the ceiling exists to prevent.
    /// <para>
    /// A suppressed buffer is dropped here and reports nothing, because there is no
    /// outcome for the caller to act on: suppression is a standing decision the
    /// caller itself made, not a failure.
    /// </para>
    /// </remarks>
    public void Record(BufferId id, BufferContent content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (id.IsEmpty)
        {
            throw new ArgumentException("An empty BufferId cannot be scheduled.", nameof(id));
        }

        lock (_gate)
        {
            if (_suppressed.Contains(id))
            {
                return;
            }

            // Recording makes the buffer live again — a reopened tab, or a new buffer
            // that reused the id. Leaving it in the discard set would silently drop
            // every subsequent write to it.
            _discarded.Remove(id);

            _pending[id] = _pending.TryGetValue(id, out var existing)
                ? existing with { Content = content, LastChangedAt = now }
                : new Entry(content, FirstChangedAt: now, LastChangedAt: now);
        }
    }

    /// <summary>
    /// Turns writing for <paramref name="id"/> off or back on, dropping anything
    /// already queued when turning it off.
    /// </summary>
    /// <param name="id">The buffer.</param>
    /// <param name="suppressed">True to stop writing it; false to allow it again.</param>
    /// <returns>True when unwritten content was dropped by this call.</returns>
    /// <remarks>
    /// Three callers, all of which must be able to make the decision <em>before</em>
    /// the first edit arrives rather than filtering afterwards:
    /// <list type="bullet">
    /// <item>a tab the user marked ephemeral, which must never touch the disk;</item>
    /// <item>a buffer that came back from disk truncated, where writing it back would
    /// make the truncation permanent;</item>
    /// <item>a document past the journaling size threshold.</item>
    /// </list>
    /// Symmetric because the first of those is a toggle: turning ephemeral off again
    /// has to start saving, or the setting is a trap.
    /// </remarks>
    public bool SetSuppressed(BufferId id, bool suppressed)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("An empty BufferId cannot be suppressed.", nameof(id));
        }

        lock (_gate)
        {
            if (!suppressed)
            {
                _suppressed.Remove(id);
                return false;
            }

            _suppressed.Add(id);
            return _pending.Remove(id);
        }
    }

    /// <summary>Whether writes for <paramref name="id"/> are currently suppressed.</summary>
    public bool IsSuppressed(BufferId id)
    {
        lock (_gate)
        {
            return _suppressed.Contains(id);
        }
    }

    /// <summary>Discards any unwritten text for <paramref name="id"/> and blocks future writes to it.</summary>
    /// <returns>True when there was unwritten text to drop.</returns>
    public bool Discard(BufferId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("An empty BufferId cannot be discarded.", nameof(id));
        }

        lock (_gate)
        {
            _discarded.Add(id);
            return _pending.Remove(id);
        }
    }

    /// <summary>
    /// Lifts a discard, for a buffer that has been reopened.
    /// </summary>
    /// <remarks>
    /// Reopening a closed tab moves its text out of the trash and back to live storage
    /// without recording anything — nothing has been typed yet. Until something is, the
    /// buffer is still in the discard set, and a write taken before the close that
    /// completes in that window would see it as discarded and delete the file that was
    /// just restored. The trash copy has already been moved, not copied, so that loss is
    /// total. Reviving before the restore closes it.
    /// </remarks>
    /// <returns>True when the buffer was in the discard set.</returns>
    public bool Revive(BufferId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("An empty BufferId cannot be revived.", nameof(id));
        }

        lock (_gate)
        {
            return _discarded.Remove(id);
        }
    }

    /// <summary>Whether <paramref name="id"/> has been closed and must not be written.</summary>
    public bool IsDiscarded(BufferId id)
    {
        lock (_gate)
        {
            return _discarded.Contains(id);
        }
    }

    /// <summary>
    /// How long until the next buffer is due, or null when nothing is pending.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeSpan.Zero"/> means something is due now. A null return is what
    /// lets the journal loop sleep indefinitely rather than tick — which is how the
    /// plan's 0% idle CPU target is actually met, rather than approximated.
    /// </remarks>
    public TimeSpan? TimeUntilNextDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            var soonest = TimeSpan.MaxValue;

            foreach (var entry in _pending.Values)
            {
                var wait = WaitFor(entry, now);

                if (wait <= TimeSpan.Zero)
                {
                    return TimeSpan.Zero;
                }

                if (wait < soonest)
                {
                    soonest = wait;
                }
            }

            return soonest;
        }
    }

    /// <summary>
    /// Removes and returns every buffer whose deadline has passed.
    /// </summary>
    /// <remarks>
    /// Taken rather than peeked: the entries leave the scheduler in the same locked
    /// step that reports them, so a change arriving mid-write is recorded as a fresh
    /// pending entry and written again afterwards, rather than being folded into a
    /// write that had already started and silently lost. The corollary is that the
    /// caller now owns those writes and must put back anything it does not complete.
    /// </remarks>
    public IReadOnlyList<PendingWrite> TakeDue(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            List<PendingWrite>? due = null;

            foreach (var (id, entry) in _pending)
            {
                if (WaitFor(entry, now) <= TimeSpan.Zero)
                {
                    (due ??= []).Add(new PendingWrite(id, entry.Content, entry.FirstChangedAt));
                }
            }

            if (due is null)
            {
                return [];
            }

            foreach (var write in due)
            {
                _pending.Remove(write.Id);
            }

            return due;
        }
    }

    /// <summary>
    /// Removes and returns everything pending, regardless of deadline.
    /// </summary>
    /// <remarks>
    /// The immediate-flush path: tab switch, window blur, tab close, application
    /// exit. Those are the moments where the user's attention has left the buffer,
    /// which is exactly when an unwritten edit is most likely to be forgotten about
    /// and least likely to be retyped.
    /// </remarks>
    public IReadOnlyList<PendingWrite> TakeAll()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            var all = new List<PendingWrite>(_pending.Count);

            foreach (var (id, entry) in _pending)
            {
                all.Add(new PendingWrite(id, entry.Content, entry.FirstChangedAt));
            }

            _pending.Clear();
            return all;
        }
    }

    /// <summary>
    /// Puts a write back after it could not be completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The original first-changed timestamp travels on <see cref="PendingWrite"/> and
    /// is restored here, so a retry stays inside the latency ceiling it was already
    /// running against instead of being granted a fresh one. Without that, a buffer
    /// that fails repeatedly would have its ceiling pushed back indefinitely — the
    /// same starvation the ceiling exists to prevent, arriving by a different route.
    /// </para>
    /// <para>
    /// <paramref name="retryAt"/> sets the debounce clock, which is what the caller
    /// uses to back a failing write off. A newer edit always wins: it is strictly
    /// better than the text being returned, and overwriting it would undo the user's
    /// most recent keystrokes. A buffer closed since the write began is dropped.
    /// </para>
    /// </remarks>
    public void Requeue(PendingWrite write, DateTimeOffset retryAt)
    {
        ArgumentNullException.ThrowIfNull(write.Content, nameof(write));

        lock (_gate)
        {
            if (_discarded.Contains(write.Id) || _suppressed.Contains(write.Id) || _pending.ContainsKey(write.Id))
            {
                return;
            }

            _pending[write.Id] = new Entry(
                write.Content,
                FirstChangedAt: write.FirstChangedAt,
                LastChangedAt: retryAt);
        }
    }

    /// <summary>Drops everything pending without writing it.</summary>
    /// <remarks>
    /// Backs "wipe all scratch data". Wiping the files while the scheduler still held
    /// their text would put the secret straight back on disk at the next debounce.
    /// </remarks>
    public int DiscardAll()
    {
        lock (_gate)
        {
            var dropped = _pending.Count;

            foreach (var id in _pending.Keys)
            {
                _discarded.Add(id);
            }

            _pending.Clear();
            return dropped;
        }
    }

    private TimeSpan WaitFor(Entry entry, DateTimeOffset now)
    {
        // Quiet for long enough, or unwritten for too long — whichever comes first.
        var debounceDue = entry.LastChangedAt + _options.DebounceInterval;
        var latencyDue = entry.FirstChangedAt + _options.MaxLatency;
        var due = debounceDue < latencyDue ? debounceDue : latencyDue;

        var wait = due - now;
        return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    private readonly record struct Entry(BufferContent Content, DateTimeOffset FirstChangedAt, DateTimeOffset LastChangedAt);
}

/// <summary>A buffer's contents, waiting to be written.</summary>
/// <param name="Id">The buffer.</param>
/// <param name="Content">
/// Its full contents as of the last edit, materialised by the writer rather than by
/// the editor — see <see cref="BufferContent"/> for why that indirection exists.
/// </param>
/// <param name="FirstChangedAt">
/// When the oldest unwritten edit in this content was made. Carried through the write
/// so that a retry can be put back on the deadline it was already running against.
/// </param>
public readonly record struct PendingWrite(BufferId Id, BufferContent Content, DateTimeOffset FirstChangedAt)
{
    /// <summary>
    /// Materialises the text to be written.
    /// </summary>
    /// <remarks>
    /// A method, not a property, because for a deferred snapshot this walks the whole
    /// document and allocates it. Call it once and keep the result — a call site that
    /// reads like a field access is exactly how that ends up inside a loop.
    /// </remarks>
    public string ReadText() => Content.ReadText();
}
