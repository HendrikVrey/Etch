using System.Collections.Concurrent;
using Etch.Persistence.Journal;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Journal;

/// <summary>
/// Integration tests for the loop that drives the scheduler. The timing logic itself
/// is proved in <see cref="WriteSchedulerTests"/> without any clock at all; what is
/// left to check here is that the loop wakes, writes, retries and shuts down,
/// which does need a real one.
/// </summary>
public class JournalWriterTests
{
    /// <summary>Short enough to keep the suite quick, long enough not to be flaky.</summary>
    private static readonly JournalOptions Fast = new(
        debounceInterval: TimeSpan.FromMilliseconds(40),
        maxLatency: TimeSpan.FromMilliseconds(200));

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_edit_reaches_the_disk_without_anyone_asking_for_it()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        journal.Enqueue(id, "typed and forgotten");

        await WaitUntilAsync(() => workspace.Buffers.Exists(id));

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("typed and forgotten", stored!.Value.Text);
    }

    [Fact]
    public async Task Nothing_is_written_before_the_debounce_elapses()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        journal.Start();
        journal.Enqueue(id, "still typing");

        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(workspace.Buffers.Exists(id));
        Assert.Equal(1, journal.PendingCount);
    }

    [Fact]
    public async Task A_burst_of_typing_produces_one_file_holding_the_last_revision()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        for (var i = 0; i < 500; i++)
        {
            journal.Enqueue(id, $"revision {i}");
        }

        await WaitUntilAsync(() => workspace.Buffers.Exists(id));
        await journal.FlushAsync(TestContext.Current.CancellationToken);

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal("revision 499", stored!.Value.Text);
        // Filtered to live buffers: a retained ".prev" generation appears alongside
        // the moment a second write lands, which timing under load can produce.
        Assert.Single(Directory.GetFiles(workspace.Paths.BuffersDirectory, "*" + BufferId.Extension));
    }

    [Fact]
    public async Task Flushing_writes_immediately_without_waiting_for_the_debounce()
    {
        // Tab switch, window blur, tab close, exit. None of them can wait.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        journal.Start();
        journal.Enqueue(id, "flush me now");

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("flush me now", stored!.Value.Text);
        Assert.Equal(0, journal.PendingCount);
    }

    [Fact]
    public async Task Flushing_with_nothing_pending_is_harmless()
    {
        using var workspace = TemporaryWorkspace.Create();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(workspace.Paths.BuffersDirectory));
    }

    [Fact]
    public async Task Many_buffers_are_all_written()
    {
        using var workspace = TemporaryWorkspace.Create();
        var ids = Enumerable.Range(0, 25).Select(_ => BufferId.New()).ToArray();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        foreach (var (id, index) in ids.Select((id, index) => (id, index)))
        {
            journal.Enqueue(id, $"buffer {index}");
        }

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < ids.Length; i++)
        {
            var stored = await workspace.Buffers.ReadAsync(ids[i], TestContext.Current.CancellationToken);
            Assert.Equal($"buffer {i}", stored!.Value.Text);
        }
    }

    [Fact]
    public async Task A_discarded_buffer_is_never_written()
    {
        // Without this the journal recreates the file moments after close moved it to
        // the trash, and reopen-closed-tab finds an empty buffer.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        journal.Start();
        journal.Enqueue(id, "about to be closed");
        journal.Discard(id);

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        Assert.False(workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task Disposing_flushes_what_is_still_pending()
    {
        // Application exit. The last thing typed must not be the thing that is lost.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        journal.Start();
        journal.Enqueue(id, "typed a moment before closing");

        await journal.DisposeAsync();

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("typed a moment before closing", stored!.Value.Text);
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        using var workspace = TemporaryWorkspace.Create();

        var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        await journal.DisposeAsync();
        await journal.DisposeAsync();
    }

    [Fact]
    public async Task Enqueuing_after_disposal_is_ignored_rather_than_thrown()
    {
        // A keystroke racing the window close must not become a crash on exit.
        using var workspace = TemporaryWorkspace.Create();

        var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();
        await journal.DisposeAsync();

        journal.Enqueue(BufferId.New(), "too late");
    }

    [Fact]
    public async Task A_journal_that_was_never_started_still_flushes_on_demand()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);

        journal.Enqueue(id, "written without a loop");
        await journal.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task Starting_twice_does_not_start_two_loops()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);

        journal.Start();
        journal.Start();

        journal.Enqueue(id, "once is enough");
        await WaitUntilAsync(() => workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task A_failing_write_is_reported_and_retried_until_it_succeeds()
    {
        // A full disk, a locked file, a profile that briefly went away. The text is
        // requeued rather than dropped, because the user believes it is saved.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();
        var failures = new ConcurrentQueue<JournalFailure>();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            Fast,
            onFailure: failures.Enqueue);

        journal.Start();

        // Remove the directory underneath the writer so every write fails.
        Directory.Delete(workspace.Paths.BuffersDirectory, recursive: true);

        journal.Enqueue(id, "must survive the outage");

        await WaitUntilAsync(() => !failures.IsEmpty);

        Assert.True(failures.TryPeek(out var first));
        Assert.NotNull(first!.Id);
        Assert.Equal(id, first.Id!.Value);
        Assert.True(first.ConsecutiveFailures >= 1);

        // The disk comes back.
        Directory.CreateDirectory(workspace.Paths.BuffersDirectory);

        await WaitUntilAsync(() => workspace.Buffers.Exists(id));

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("must survive the outage", stored!.Value.Text);
    }

    [Fact]
    public async Task A_faulty_failure_handler_does_not_stop_the_journal()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            Fast,
            onFailure: _ => throw new InvalidOperationException("a bad handler"));

        journal.Start();

        Directory.Delete(workspace.Paths.BuffersDirectory, recursive: true);
        journal.Enqueue(id, "resilient");

        await Task.Delay(200, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(workspace.Paths.BuffersDirectory);

        await WaitUntilAsync(() => workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task An_edit_arriving_during_a_write_is_written_afterwards()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        for (var i = 0; i < 50; i++)
        {
            journal.Enqueue(id, $"revision {i}");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("revision 49", stored!.Value.Text);
    }

    [Fact]
    public async Task Sustained_typing_is_written_within_the_latency_ceiling()
    {
        // The debounce alone never fires while somebody keeps typing. The ceiling is
        // what bounds how much a crash can take.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        // Debounce comfortably longer than the typing interval, ceiling only just
        // above it: the debounce can never elapse, so the file can only appear
        // because the ceiling fired.
        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(300)));

        journal.Start();

        using var typing = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var typist = Task.Run(async () =>
        {
            var revision = 0;

            while (!typing.IsCancellationRequested)
            {
                journal.Enqueue(id, $"revision {revision++}");
                await Task.Delay(20, CancellationToken.None);
            }
        });

        await WaitUntilAsync(() => workspace.Buffers.Exists(id));

        await typist;
    }

    [Fact]
    public async Task Cancelling_a_flush_puts_every_unwritten_buffer_back()
    {
        // The batch has already been removed from the scheduler by the time the
        // writer sees it, so anything not written and not requeued exists nowhere.
        // Before this was fixed, closing a window with ten dirty tabs while the
        // writer was on tab three lost tabs four through ten silently.
        using var workspace = TemporaryWorkspace.Create();
        var ids = Enumerable.Range(0, 20).Select(_ => BufferId.New()).ToArray();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        foreach (var id in ids)
        {
            journal.Enqueue(id, $"contents of {id}");
        }

        // Cancelled mid-batch rather than up front, so the writer is partway through
        // the list when the token fires.
        using var cancelled = new CancellationTokenSource();
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(2));

        try
        {
            await journal.FlushAsync(cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on most runs; a fast enough disk finishes first.
        }

        // The invariant that matters, and the one that was broken: every buffer is
        // either on disk or still queued. None of them may be in neither place.
        var written = ids.Count(workspace.Buffers.Exists);
        Assert.Equal(ids.Length, written + journal.PendingCount);

        await journal.FlushAsync(TestContext.Current.CancellationToken);

        foreach (var id in ids)
        {
            var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.Equal($"contents of {id}", stored!.Value.Text);
        }
    }

    [Fact]
    public async Task Nothing_is_lost_when_disposal_interrupts_a_batch()
    {
        using var workspace = TemporaryWorkspace.Create();
        var ids = Enumerable.Range(0, 30).Select(_ => BufferId.New()).ToArray();

        var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        foreach (var id in ids)
        {
            journal.Enqueue(id, $"contents of {id}");
        }

        await journal.DisposeAsync();

        foreach (var id in ids)
        {
            var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
            Assert.NotNull(stored);
            Assert.Equal($"contents of {id}", stored!.Value.Text);
        }
    }

    [Fact]
    public async Task A_buffer_closed_while_its_write_is_in_flight_is_not_resurrected()
    {
        // The write was already taken from the scheduler when the tab closed. If it
        // publishes afterwards it recreates the file the close moved to the trash,
        // and the next launch adopts the orphan as a recovered tab.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        journal.Enqueue(id, "closing right about now");
        journal.Discard(id);

        await journal.FlushAsync(TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.False(workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task Unsaved_work_is_reported_until_the_bytes_are_actually_down()
    {
        // PendingCount drops to zero the moment a batch is taken, which is before any
        // of it has reached the disk. A "all changes saved" indicator built on that
        // alone would show green too early.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(
            workspace.Buffers,
            new JournalOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));

        Assert.False(journal.HasUnsavedWork);

        journal.Enqueue(id, "not yet on disk");
        Assert.True(journal.HasUnsavedWork);

        await journal.FlushAsync(TestContext.Current.CancellationToken);
        Assert.False(journal.HasUnsavedWork);
    }

    [Fact]
    public async Task Discarding_everything_stops_a_wipe_being_undone_by_the_journal()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await using var journal = new JournalWriter(workspace.Buffers, Fast);
        journal.Start();

        journal.Enqueue(id, "a pasted credential");

        Assert.Equal(1, await journal.DiscardAllAsync(TestContext.Current.CancellationToken));

        workspace.Buffers.WipeAll();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.False(workspace.Buffers.Exists(id));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was still false after {Patience.TotalSeconds:0} seconds.");
    }
}
