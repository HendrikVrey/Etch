using System.Collections.Concurrent;

namespace Etch.App.Tests;

/// <summary>
/// Runs a test body entirely on one thread, the way the real application does.
/// </summary>
/// <remarks>
/// <para>
/// This exists because AvalonEdit's <c>TextDocument</c> has thread affinity: it records
/// the thread that constructed it and throws from any other. xUnit is free to resume an
/// <c>async</c> test after each <c>await</c> on whichever thread-pool thread is handy,
/// so a workspace test that creates a tab and then awaits anything would be creating
/// the document on one thread and snapshotting it on another. That fails intermittently,
/// which is worse than failing always.
/// </para>
/// <para>
/// The fix is not to work around the affinity but to reproduce the environment that
/// makes it correct. Installing a synchronization context that posts every continuation
/// back to a single pumping thread is exactly what a WPF dispatcher does, so
/// <c>ConfigureAwait(true)</c> in <c>Workspace</c> behaves here as it does in the app,
/// and a test that would deadlock or fault under the real dispatcher does so here too.
/// </para>
/// <para>
/// The parts of the system that genuinely are off-thread stay off-thread: the journal's
/// background loop, and anything using <c>ConfigureAwait(false)</c>, still run on the
/// thread pool. So this narrows the test to the real threading model rather than
/// flattening it.
/// </para>
/// </remarks>
internal static class UiThread
{
    /// <summary>
    /// Runs <paramref name="body"/> to completion on a single dedicated thread.
    /// </summary>
    /// <param name="body">The test body.</param>
    /// <param name="timeout">
    /// How long to wait before declaring the test hung. A deadlock here is a real finding,
    /// it means the code under test would deadlock the dispatcher, so it fails rather
    /// than hanging the suite.
    /// </param>
    /// <exception cref="TimeoutException">The body did not finish in time.</exception>
    public static void Run(Func<Task> body, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        var previous = SynchronizationContext.Current;
        using var context = new PumpingSynchronizationContext();

        SynchronizationContext.SetSynchronizationContext(context);

        try
        {
            var task = body();

            // Completing the pump is what lets it stop; without this the loop would block
            // on an empty queue forever once the body finished.
            _ = task.ContinueWith(
                static (_, state) => ((PumpingSynchronizationContext)state!).Complete(),
                context,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            context.Pump(timeout ?? DefaultTimeout);

            // Unwrapped rather than left on the task: GetAwaiter().GetResult() rethrows the
            // original exception with its stack intact, where .Wait() would bury it in an
            // AggregateException and make every assertion failure read like a threading bug.
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Long enough for real file I/O, short enough that a deadlock is reported.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        /// <inheritdoc />
        public override void Post(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            try
            {
                _queue.Add((d, state));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The pump has already been completed: a continuation arriving after the
                // body finished, which is normal for fire-and-forget work the workspace
                // starts. Dropping it matches what a dispatcher does once it shuts down.
            }
        }

        /// <inheritdoc />
        /// <remarks>
        /// Send is synchronous by contract. Queueing it would deadlock when the caller is
        /// already the pump thread, which is the only caller that exists here.
        /// </remarks>
        public override void Send(SendOrPostCallback d, object? state)
        {
            ArgumentNullException.ThrowIfNull(d);

            d(state);
        }

        /// <summary>Runs queued callbacks until the body completes.</summary>
        public void Pump(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (!_queue.IsCompleted)
            {
                var remaining = deadline - DateTime.UtcNow;

                if (remaining <= TimeSpan.Zero)
                {
                    throw Hung();
                }

                if (!_queue.TryTake(out var work, remaining))
                {
                    // TryTake also reports false the instant the queue is completed and
                    // empty, which is the ordinary way a test finishes: the body can
                    // complete between the check above and this call. Only a genuinely
                    // empty wait is a timeout.
                    if (_queue.IsCompleted)
                    {
                        return;
                    }

                    throw Hung();
                }

                work.Callback(work.State);
            }
        }

        private static TimeoutException Hung() => new(
            "The test body did not complete. Under a real dispatcher this would be a hung window.");

        /// <summary>Signals that nothing further will be posted.</summary>
        public void Complete()
        {
            try
            {
                _queue.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
                // Raced with disposal at the end of a test. Nothing left to complete.
            }
        }

        public void Dispose() => _queue.Dispose();
    }
}
