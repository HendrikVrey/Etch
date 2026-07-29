using Etch.Persistence.Journal;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Journal;

/// <summary>
/// The scheduler carries all of the journal's timing logic, so it carries most of
/// the journal's tests. Every case here is arithmetic on supplied timestamps —
/// there is not a single sleep in this file, and there should never be one.
/// </summary>
public class WriteSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly JournalOptions Options = new(
        debounceInterval: TimeSpan.FromMilliseconds(500),
        maxLatency: TimeSpan.FromSeconds(5));

    private static WriteScheduler CreateScheduler() => new(Options);

    [Fact]
    public void Nothing_is_pending_on_a_new_scheduler()
    {
        var scheduler = CreateScheduler();

        Assert.Equal(0, scheduler.PendingCount);
        Assert.Null(scheduler.TimeUntilNextDue(T0));
        Assert.Empty(scheduler.TakeDue(T0));
        Assert.Empty(scheduler.TakeAll());
    }

    [Fact]
    public void A_null_next_due_is_what_lets_the_loop_sleep_instead_of_polling()
    {
        // The 0% idle CPU budget depends on this distinction: null means "block
        // indefinitely", any TimeSpan means "wake up then". If this ever returned
        // Zero for an empty scheduler the loop would spin.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        Assert.Null(scheduler.TimeUntilNextDue(T0));

        scheduler.Record(id, "typed", T0);
        Assert.NotNull(scheduler.TimeUntilNextDue(T0));

        scheduler.TakeAll();
        Assert.Null(scheduler.TimeUntilNextDue(T0));
    }

    [Fact]
    public void A_change_is_not_due_until_the_debounce_has_elapsed()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "hello", T0);

        Assert.Equal(TimeSpan.FromMilliseconds(500), scheduler.TimeUntilNextDue(T0));
        Assert.Empty(scheduler.TakeDue(T0.AddMilliseconds(499)));

        var due = scheduler.TakeDue(T0.AddMilliseconds(500));

        Assert.Equal(id, Assert.Single(due).Id);
    }

    [Fact]
    public void Continued_typing_pushes_the_debounce_back()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "h", T0);
        scheduler.Record(id, "he", T0.AddMilliseconds(300));
        scheduler.Record(id, "hel", T0.AddMilliseconds(600));

        // 500 ms after the first keystroke, but only 200 ms after the last.
        Assert.Empty(scheduler.TakeDue(T0.AddMilliseconds(800)));

        var due = scheduler.TakeDue(T0.AddMilliseconds(1100));

        Assert.Equal("hel", Assert.Single(due).Text);
    }

    [Fact]
    public void The_latency_ceiling_fires_even_when_typing_never_stops()
    {
        // The failure this exists to prevent: someone typing steadily for minutes
        // never triggers the debounce, so without a ceiling their work is never
        // written and a crash takes all of it.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "start", T0);

        for (var elapsed = 100; elapsed < 5_000; elapsed += 100)
        {
            scheduler.Record(id, $"text at {elapsed}", T0.AddMilliseconds(elapsed));
            Assert.Empty(scheduler.TakeDue(T0.AddMilliseconds(elapsed)));
        }

        var due = scheduler.TakeDue(T0.AddSeconds(5));

        Assert.Single(due);
        Assert.Equal("text at 4900", due[0].Text);
    }

    [Fact]
    public void The_latency_ceiling_is_measured_from_the_first_unwritten_change()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "first", T0);
        scheduler.Record(id, "second", T0.AddSeconds(4));

        // 5 s after the first change, only 1 s after the second: the ceiling wins.
        var wait = scheduler.TimeUntilNextDue(T0.AddSeconds(4));

        Assert.Equal(TimeSpan.FromSeconds(1), wait);
    }

    [Fact]
    public void Repeated_edits_coalesce_into_one_write_holding_the_newest_text()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        for (var i = 0; i < 100; i++)
        {
            scheduler.Record(id, $"revision {i}", T0.AddMilliseconds(i));
        }

        Assert.Equal(1, scheduler.PendingCount);

        var due = scheduler.TakeDue(T0.AddSeconds(10));

        Assert.Equal("revision 99", Assert.Single(due).Text);
    }

    [Fact]
    public void Buffers_are_scheduled_independently()
    {
        var scheduler = CreateScheduler();
        var early = BufferId.New();
        var late = BufferId.New();

        scheduler.Record(early, "early", T0);
        scheduler.Record(late, "late", T0.AddSeconds(2));

        var first = scheduler.TakeDue(T0.AddSeconds(1));

        Assert.Equal(early, Assert.Single(first).Id);
        Assert.Equal(1, scheduler.PendingCount);

        var second = scheduler.TakeDue(T0.AddSeconds(3));

        Assert.Equal(late, Assert.Single(second).Id);
    }

    [Fact]
    public void Next_due_reports_the_soonest_deadline_across_buffers()
    {
        var scheduler = CreateScheduler();

        scheduler.Record(BufferId.New(), "a", T0);
        scheduler.Record(BufferId.New(), "b", T0.AddMilliseconds(200));

        Assert.Equal(TimeSpan.FromMilliseconds(500), scheduler.TimeUntilNextDue(T0));
    }

    [Fact]
    public void Next_due_is_zero_once_anything_is_overdue()
    {
        var scheduler = CreateScheduler();

        scheduler.Record(BufferId.New(), "a", T0);

        Assert.Equal(TimeSpan.Zero, scheduler.TimeUntilNextDue(T0.AddSeconds(30)));
    }

    [Fact]
    public void Take_all_ignores_deadlines()
    {
        // The flush path: tab switch, blur, close, exit. None of them can afford to
        // wait out a debounce.
        var scheduler = CreateScheduler();

        scheduler.Record(BufferId.New(), "a", T0);
        scheduler.Record(BufferId.New(), "b", T0);

        var all = scheduler.TakeAll();

        Assert.Equal(2, all.Count);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Taking_removes_entries_so_an_edit_during_a_write_is_not_lost()
    {
        // The race that motivates TakeDue removing rather than peeking: text changes
        // while its write is in flight. The in-flight write publishes the old text,
        // and the new text has to survive as a fresh pending entry rather than being
        // considered already written.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "before", T0);

        var due = scheduler.TakeDue(T0.AddSeconds(1));
        Assert.Equal("before", Assert.Single(due).Text);
        Assert.Equal(0, scheduler.PendingCount);

        scheduler.Record(id, "after", T0.AddSeconds(1));

        var next = scheduler.TakeDue(T0.AddSeconds(2));
        Assert.Equal("after", Assert.Single(next).Text);
    }

    [Fact]
    public void Discarding_a_closed_buffer_stops_it_being_written()
    {
        // Without this the journal recreates the buffer file moments after close
        // moved it to the trash, leaving an orphan that reopen-closed-tab would find
        // empty.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "closing", T0);

        Assert.True(scheduler.Discard(id));
        Assert.False(scheduler.Discard(id));
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Empty(scheduler.TakeDue(T0.AddSeconds(10)));
    }

    [Fact]
    public void A_requeued_write_is_retried_after_the_debounce()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();
        var write = new PendingWrite(id, "failed once", T0);

        scheduler.Requeue(write, T0);

        Assert.Equal(1, scheduler.PendingCount);
        Assert.Empty(scheduler.TakeDue(T0));

        var retried = scheduler.TakeDue(T0.AddSeconds(1));

        Assert.Equal("failed once", Assert.Single(retried).Text);
    }

    [Fact]
    public void A_requeued_write_keeps_the_deadline_it_was_already_running_against()
    {
        // Granting a retry a fresh latency ceiling is the same starvation the ceiling
        // exists to prevent, arriving by a different route: a buffer that keeps
        // failing would have its deadline pushed back indefinitely.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "first attempt", T0);
        var taken = Assert.Single(scheduler.TakeDue(T0.AddSeconds(1)));

        Assert.Equal(T0, taken.FirstChangedAt);

        scheduler.Requeue(taken, T0.AddSeconds(1));

        // The ceiling is still measured from T0, so it lands at T0 + 5 s rather than
        // five seconds after the retry.
        Assert.Equal(TimeSpan.Zero, scheduler.TimeUntilNextDue(T0.AddSeconds(5)));
    }

    [Fact]
    public void A_write_carries_the_timestamp_of_its_oldest_unwritten_edit()
    {
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "one", T0);
        scheduler.Record(id, "two", T0.AddMilliseconds(100));

        var due = Assert.Single(scheduler.TakeDue(T0.AddSeconds(1)));

        Assert.Equal(T0, due.FirstChangedAt);
        Assert.Equal("two", due.Text);
    }

    [Fact]
    public void A_discarded_buffer_stays_discarded_through_a_requeue()
    {
        // The close-during-write race: the batch was taken, the tab closed, and then
        // the write failed. Putting it back would resurrect a buffer whose file has
        // already been moved to the trash.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "closing", T0);
        var taken = Assert.Single(scheduler.TakeDue(T0.AddSeconds(1)));

        scheduler.Discard(id);
        scheduler.Requeue(taken, T0.AddSeconds(1));

        Assert.True(scheduler.IsDiscarded(id));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Recording_makes_a_discarded_buffer_live_again()
    {
        // A reopened tab. Leaving it in the discard set would silently drop every
        // subsequent write to it.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Discard(id);
        Assert.True(scheduler.IsDiscarded(id));

        scheduler.Record(id, "reopened", T0);

        Assert.False(scheduler.IsDiscarded(id));
        Assert.Equal("reopened", Assert.Single(scheduler.TakeDue(T0.AddSeconds(1))).Text);
    }

    [Fact]
    public void Discarding_everything_drops_it_without_writing()
    {
        // Backs "wipe all scratch data": wiping the files while the scheduler still
        // held their text would put the secret back on disk at the next debounce.
        var scheduler = CreateScheduler();

        scheduler.Record(BufferId.New(), "secret one", T0);
        scheduler.Record(BufferId.New(), "secret two", T0);

        Assert.Equal(2, scheduler.DiscardAll());
        Assert.Equal(0, scheduler.PendingCount);
        Assert.Empty(scheduler.TakeAll());
    }

    [Fact]
    public void A_requeued_write_never_overwrites_a_newer_edit()
    {
        // A write fails, and while it was failing the user typed. The newer text is
        // strictly better; putting the stale text back would undo their edit.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "newer", T0.AddSeconds(1));
        scheduler.Requeue(new PendingWrite(id, "stale", T0), T0.AddSeconds(1));

        var due = scheduler.TakeDue(T0.AddSeconds(10));

        Assert.Equal("newer", Assert.Single(due).Text);
    }

    [Fact]
    public void Recording_an_empty_id_is_rejected()
    {
        var scheduler = CreateScheduler();

        Assert.Throws<ArgumentException>(() => scheduler.Record(default, "text", T0));
    }

    [Fact]
    public void Null_text_is_rejected()
    {
        var scheduler = CreateScheduler();

        Assert.Throws<ArgumentNullException>(() => scheduler.Record(BufferId.New(), null!, T0));
    }

    [Fact]
    public void Empty_text_is_a_legitimate_state_and_must_be_written()
    {
        // Select-all then delete. If empty text were treated as "nothing to save",
        // the file would keep its old contents and the deletion would come back on
        // the next restore.
        var scheduler = CreateScheduler();
        var id = BufferId.New();

        scheduler.Record(id, string.Empty, T0);

        var due = scheduler.TakeDue(T0.AddSeconds(1));

        Assert.Equal(string.Empty, Assert.Single(due).Text);
    }

    [Fact]
    public void Concurrent_recording_and_draining_loses_nothing()
    {
        // The real access pattern: the UI thread records while the journal thread
        // drains. Every write must come out exactly once.
        var scheduler = CreateScheduler();
        var ids = Enumerable.Range(0, 50).Select(_ => BufferId.New()).ToArray();
        var drained = new List<PendingWrite>();
        var stop = false;

        var drainer = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                drained.AddRange(scheduler.TakeAll());
            }

            drained.AddRange(scheduler.TakeAll());
        });

        Parallel.ForEach(ids, id =>
        {
            for (var i = 0; i < 20; i++)
            {
                scheduler.Record(id, $"{id}:{i}", T0.AddMilliseconds(i));
            }
        });

        Volatile.Write(ref stop, true);
        drainer.Wait(TimeSpan.FromSeconds(30));

        // Coalescing means the count is not deterministic, but the final text for
        // every buffer must have been observed and nothing may be left behind.
        Assert.Equal(0, scheduler.PendingCount);

        foreach (var id in ids)
        {
            Assert.Contains(drained, write => write.Id == id && write.Text == $"{id}:19");
        }
    }
}
