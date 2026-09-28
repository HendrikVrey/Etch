using Etch.Persistence.Model;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Journal;

/// <summary>
/// Writes edited buffers to disk in the background, so the user never has to.
/// </summary>
/// <remarks>
/// <para>
/// A single consumer draining a <see cref="WriteScheduler"/>. Deliberately thin:
/// every decision about *what* to write and *when* belongs to the scheduler, which
/// is synchronous and exhaustively testable, leaving this type responsible only for
/// sleeping, waking and calling the store. Concurrency bugs live in code like this,
/// so there is as little of it as possible.
/// </para>
/// <para>
/// It never polls. With nothing pending the loop blocks indefinitely on a semaphore
/// and the process sits at genuinely zero CPU, which is a stated budget rather than
/// an aspiration. When something *is* pending, the wake-up is scheduled through the
/// injected <see cref="TimeProvider"/> rather than a real timer, so a test can drive
/// the whole debounce and retry cycle with a fake clock instead of with sleeps.
/// </para>
/// </remarks>
public sealed class JournalWriter : IAsyncDisposable
{
    /// <summary>Total budget for shutdown: stopping the loop and the final flush together.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ceiling on the backoff between retries of a persistently failing write.</summary>
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    private readonly BufferStore _store;
    private readonly WriteScheduler _scheduler;
    private readonly JournalOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<JournalFailure>? _onFailure;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    private volatile Task? _loop;
    private int _started;
    private int _disposed;
    private int _inFlight;
    private int _consecutiveFailures;

    /// <summary>Creates a journal over <paramref name="store"/>.</summary>
    /// <param name="store">Where buffers are written.</param>
    /// <param name="options">Debounce and latency settings. Defaults when null.</param>
    /// <param name="timeProvider">The clock and the timer source. Defaults to the system clock.</param>
    /// <param name="onFailure">
    /// Invoked when a write fails, on the journal's own thread. Intended for a quiet
    /// status-bar indicator, never a dialog, and never on the UI thread without the
    /// caller marshalling it there itself.
    /// </param>
    public JournalWriter(
        BufferStore store,
        JournalOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<JournalFailure>? onFailure = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? JournalOptions.Default;
        _scheduler = new WriteScheduler(_options);
        _time = timeProvider ?? TimeProvider.System;
        _onFailure = onFailure;
    }

    /// <summary>How many buffers have changes that have not yet been handed to the writer.</summary>
    public int PendingCount => _scheduler.PendingCount;

    /// <summary>
    /// Whether anything is unwritten, counting batches already in flight.
    /// </summary>
    /// <remarks>
    /// The question a "all changes saved" indicator has to ask. <see cref="PendingCount"/>
    /// alone drops to zero the moment a batch is taken, which is before any of it has
    /// reached the disk.
    /// </remarks>
    public bool HasUnsavedWork => _scheduler.PendingCount > 0 || Volatile.Read(ref _inFlight) > 0;

    /// <summary>Starts the background loop. Calling it more than once does nothing.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        // Task.Run, not a LongRunning thread: RunAsync blocks on a semaphore
        // asynchronously and never occupies a thread while waiting, so a dedicated
        // thread would sit idle for the life of the process.
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Records that a buffer changed, when the caller already holds its text.</summary>
    public void Enqueue(BufferId id, string text) => Enqueue(id, BufferContent.FromText(text));

    /// <summary>
    /// Records that a buffer changed. Returns immediately; the write happens later.
    /// </summary>
    /// <remarks>
    /// Called from the UI thread on every edit, so it does no I/O and takes only an
    /// uncontended lock. <paramref name="content"/> is materialised later, on the
    /// journal's thread: see <see cref="BufferContent"/> for why the editor must
    /// hand over a snapshot rather than a string.
    /// </remarks>
    public void Enqueue(BufferId id, BufferContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (Volatile.Read(ref _disposed) == 1)
        {
            // Shutdown has already flushed. Throwing here would turn a benign race
            // between the last keystroke and window close into a crash on exit.
            return;
        }

        _scheduler.Record(id, content, _time.GetUtcNow());
        SignalWake();
    }

    /// <summary>
    /// Forgets any unwritten changes for a buffer that has been closed, and refuses
    /// to write it again.
    /// </summary>
    public void Discard(BufferId id) => _scheduler.Discard(id);

    /// <summary>
    /// Stops or resumes writing a buffer entirely: an ephemeral tab, a buffer read
    /// back truncated, or one past the journaling size threshold.
    /// </summary>
    /// <returns>True when unwritten content was dropped by this call.</returns>
    public bool SetSuppressed(BufferId id, bool suppressed) => _scheduler.SetSuppressed(id, suppressed);

    /// <summary>Whether writes for <paramref name="id"/> are currently suppressed.</summary>
    public bool IsSuppressed(BufferId id) => _scheduler.IsSuppressed(id);

    /// <summary>Lifts a discard, for a buffer that has been reopened.</summary>
    public bool Revive(BufferId id) => _scheduler.Revive(id);

    /// <summary>
    /// Drops every unwritten change without writing it, and waits for anything already
    /// in flight to finish first.
    /// </summary>
    /// <remarks>
    /// Backs "wipe all scratch data", and the wait is the whole point. Clearing the
    /// queue alone is not enough: a batch already taken by the writer is no longer in
    /// the queue, so it would complete and recreate the file <em>after</em> the wipe
    /// deleted it, putting the secret the user asked to destroy straight back on disk.
    /// Taking the same gate the writer holds is what makes "nothing is in flight" true
    /// rather than likely.
    /// </remarks>
    /// <returns>The number of buffers whose unwritten text was dropped.</returns>
    public async Task<int> DiscardAllAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _scheduler.DiscardAll();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes everything pending, now, and waits for it.
    /// </summary>
    /// <remarks>
    /// The immediate-flush path from the plan: tab switch, window blur, tab close and
    /// application exit. Safe to call concurrently with the background loop, both go
    /// through the same gate, so a buffer is never written from two places at once.
    /// If <paramref name="cancellationToken"/> fires mid-batch, everything not yet
    /// written goes back on the queue rather than being dropped.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteBatchAsync(_scheduler.TakeAll(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await SleepUntilDueAsync(token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                var failuresBefore = Volatile.Read(ref _consecutiveFailures);

                await _writeGate.WaitAsync(token).ConfigureAwait(false);

                try
                {
                    await WriteBatchAsync(_scheduler.TakeDue(_time.GetUtcNow()), token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                // Only back off if this pass actually failed. Without the check a
                // single historical failure would slow the journal down for as long
                // as the process lived.
                if (Volatile.Read(ref _consecutiveFailures) > failuresBefore)
                {
                    await DelayAsync(BackoffFor(Volatile.Read(ref _consecutiveFailures)), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must outlive any single failure. A journal that stopped on
                // the first unexpected exception would keep the window open and
                // responsive while quietly saving nothing, which is the worst
                // available outcome.
                Report(new JournalFailure(null, ex, Interlocked.Increment(ref _consecutiveFailures)));

                try
                {
                    await DelayAsync(BackoffFor(Volatile.Read(ref _consecutiveFailures)), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Blocks until a buffer is due or an edit arrives.
    /// </summary>
    /// <remarks>
    /// The wait itself is an untimed semaphore; the deadline is delivered by a
    /// <see cref="TimeProvider"/> timer that releases it. Doing it this way rather
    /// than with a semaphore timeout is what makes the loop drivable by a fake clock:
    /// a timeout would always be measured against the real one.
    /// </remarks>
    private async Task SleepUntilDueAsync(CancellationToken token)
    {
        var wait = _scheduler.TimeUntilNextDue(_time.GetUtcNow());

        if (wait == TimeSpan.Zero)
        {
            DrainWakeSignals();
            return;
        }

        if (wait is null)
        {
            await PruneDiscardsAsync(token).ConfigureAwait(false);

            // Nothing pending: sleep until an edit arrives. No timer, no tick, no CPU.
            await _wake.WaitAsync(token).ConfigureAwait(false);
        }
        else
        {
            using var timer = _time.CreateTimer(
                static state => ((JournalWriter)state!).SignalWake(),
                this,
                wait.Value,
                Timeout.InfiniteTimeSpan);

            await _wake.WaitAsync(token).ConfigureAwait(false);
        }

        DrainWakeSignals();
    }

    /// <summary>
    /// Drops discards that nothing can ask about any more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The discard set exists to beat a write that was already in flight when a tab was
    /// closed, so an entry is needed exactly as long as a batch containing that id might
    /// still be running. Left alone the set grew by one <c>BufferId</c> per tab closed,
    /// for the life of the process.
    /// </para>
    /// <para>
    /// Taken under <c>_writeGate</c>, and deliberately not under a test of
    /// <c>_inFlight</c>. Every batch leaves the scheduler <i>inside</i> the gate
    /// (<c>WriteBatchAsync(_scheduler.TakeAll(), …)</c> evaluates its argument once the
    /// gate is held) but <c>_inFlight</c> is not incremented until the method body
    /// begins. Between those two points the scheduler is empty, <c>_inFlight</c> is still
    /// zero, and a batch is nonetheless in somebody's hand: a prune there clears the
    /// discard for an id in that batch, the closed tab's buffer file is republished, and
    /// the next launch adopts it as a recovered tab holding text the user closed. The
    /// gate is what the batch is actually taken under, so the gate is the only thing that
    /// covers the window.
    /// </para>
    /// </remarks>
    private async Task PruneDiscardsAsync(CancellationToken token)
    {
        await _writeGate.WaitAsync(token).ConfigureAwait(false);

        try
        {
            _scheduler.PruneDiscards();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Collapses the backlog of wake signals a burst of typing left behind.</summary>
    private void DrainWakeSignals()
    {
        while (_wake.Wait(0))
        {
        }
    }

    private void SignalWake()
    {
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed underneath a timer callback or a racing keystroke. The process
            // is going away; there is nothing left to wake.
        }
        catch (SemaphoreFullException)
        {
            // Already signalled more times than the loop has consumed. Harmless.
        }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, token).ConfigureAwait(false);
        }
    }

    private TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        // Exponential from the debounce interval, capped. A disk that is not coming
        // back must not be retried twice a second forever, each attempt is a real
        // disk round trip and a callback the UI has to marshal.
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var scaled = _options.DebounceInterval * Math.Pow(2, exponent);

        return scaled >= MaxRetryBackoff ? MaxRetryBackoff : scaled;
    }

    /// <summary>
    /// Writes a batch, guaranteeing that anything not written goes back on the queue.
    /// </summary>
    /// <remarks>
    /// The batch has already been removed from the scheduler by the time it gets
    /// here, so this method owns the only reference to that text. Every exit path
    /// (success, failure, cancellation) has to account for all of it. Returning
    /// early without requeuing the remainder is silent data loss, and it would happen
    /// at application exit, which is exactly when there is the most unwritten text
    /// and the least chance of anyone noticing.
    /// </remarks>
    private async Task WriteBatchAsync(IReadOnlyList<PendingWrite> writes, CancellationToken cancellationToken)
    {
        if (writes.Count == 0)
        {
            return;
        }

        Interlocked.Increment(ref _inFlight);

        try
        {
            for (var index = 0; index < writes.Count; index++)
            {
                var write = writes[index];

                // Discarded: the tab was closed after this batch was taken, and writing
                // now would recreate the file the close just moved to the trash.
                // Suppressed: the buffer was marked ephemeral or found truncated in the
                // same window, and its text must not reach the disk at all.
                if (_scheduler.IsDiscarded(write.Id) || _scheduler.IsSuppressed(write.Id))
                {
                    continue;
                }

                // Materialised once, here, on the journal's thread rather than the
                // editor's. The result is held in a local so that a failure requeues an
                // already-realised string instead of a snapshot that would be walked a
                // second time on every subsequent retry.
                string text;

                try
                {
                    text = write.Content.ReadText();
                }
                catch (OperationCanceledException)
                {
                    RequeueFrom(writes, index);
                    throw;
                }
                catch (Exception ex)
                {
                    _scheduler.Requeue(write, _time.GetUtcNow());
                    Report(new JournalFailure(write.Id, ex, Interlocked.Increment(ref _consecutiveFailures)));
                    continue;
                }

                try
                {
                    await _store.WriteAsync(write.Id, text, cancellationToken).ConfigureAwait(false);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);

                    // Closed or suppressed while this write was in flight. The publish
                    // won the race, so undo it rather than leave an orphan for the next
                    // launch to adopt as a recovered tab, or, for an ephemeral buffer,
                    // leave the one thing it promised never to write sitting on disk.
                    if (_scheduler.IsDiscarded(write.Id) || _scheduler.IsSuppressed(write.Id))
                    {
                        _store.DeleteLive(write.Id);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The already-materialised string goes back for this one; the rest of
                    // the batch has not been read yet and goes back deferred.
                    _scheduler.Requeue(write with { Content = BufferContent.FromText(text) }, _time.GetUtcNow());
                    RequeueFrom(writes, index + 1);
                    throw;
                }
                catch (Exception ex)
                {
                    // Every exception type, not just IOException. Requeued rather than
                    // dropped: a full disk, a locked file or a profile that briefly
                    // went away all come back, and the alternative is discarding text
                    // the user believes is saved. The backoff in the loop is what keeps
                    // the retry from becoming a spin. The already-materialised string
                    // goes back, not the snapshot.
                    _scheduler.Requeue(write with { Content = BufferContent.FromText(text) }, _time.GetUtcNow());
                    Report(new JournalFailure(write.Id, ex, Interlocked.Increment(ref _consecutiveFailures)));
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void RequeueFrom(IReadOnlyList<PendingWrite> writes, int index)
    {
        var now = _time.GetUtcNow();

        for (var i = index; i < writes.Count; i++)
        {
            _scheduler.Requeue(writes[i], now);
        }
    }

    private void Report(JournalFailure failure)
    {
        try
        {
            _onFailure?.Invoke(failure);
        }
        catch
        {
            // A faulty error handler must not take down the writer that called it.
        }
    }

    /// <summary>
    /// Stops the loop and writes everything still pending.
    /// </summary>
    /// <remarks>
    /// The final flush runs with its own token rather than the shutdown one: the
    /// whole point of this method is to write the edits that shutdown would otherwise
    /// discard, so cancelling it with the shutdown signal would defeat it. Both waits
    /// share a single <see cref="ShutdownTimeout"/> budget so a wedged disk delays
    /// exit by that much in total rather than twice over.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var deadline = _time.GetUtcNow() + ShutdownTimeout;

        await _stopping.CancelAsync().ConfigureAwait(false);

        var settled = true;

        if (_loop is { } loop)
        {
            try
            {
                settled &= await SettleAsync(loop, Remaining(deadline)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // RunAsync swallows its own failures, so a faulted loop task means
                // something escaped that design. Report it rather than let it take
                // the final flush down with it.
                Report(new JournalFailure(null, ex, Interlocked.Increment(ref _consecutiveFailures)));
            }
        }

        try
        {
            settled &= await SettleAsync(FlushAsync(CancellationToken.None), Remaining(deadline)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(new JournalFailure(null, ex, Interlocked.Increment(ref _consecutiveFailures)));
        }

        // Only when nothing is still running. Disposing these underneath an abandoned
        // wait produces ObjectDisposedException on a task nobody observes, and the
        // leak of three handles for the remaining life of a closing process is the
        // cheaper of the two.
        if (settled)
        {
            _stopping.Dispose();
            _wake.Dispose();
            _writeGate.Dispose();
        }
    }

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - _time.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task<bool> SettleAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }
}

/// <summary>A write that did not succeed.</summary>
/// <param name="Id">The buffer being written, or null when the loop itself faulted.</param>
/// <param name="Exception">What went wrong.</param>
/// <param name="ConsecutiveFailures">
/// How many failures have happened in a row without an intervening success. The
/// first is worth noting quietly; a sustained run means auto-save is genuinely not
/// working and the user needs to be told plainly.
/// </param>
public sealed record JournalFailure(BufferId? Id, Exception Exception, int ConsecutiveFailures);
