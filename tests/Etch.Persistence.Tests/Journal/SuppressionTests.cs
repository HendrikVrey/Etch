using Etch.Persistence.Journal;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Journal;

/// <summary>
/// Buffers that must never reach the disk.
/// </summary>
/// <remarks>
/// Three callers need this and all three are correctness rather than preference: a tab
/// the user marked ephemeral, a buffer read back truncated (writing it would make the
/// truncation permanent), and a document past the journaling size threshold.
/// Suppression is deliberately distinct from discarding — a discarded buffer revives
/// the moment it is recorded again, which is what makes reopening a closed tab work,
/// and would silently defeat all three of these.
/// </remarks>
public class SuppressionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static WriteScheduler NewScheduler() =>
        new(new JournalOptions(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5)));

    [Fact]
    public void A_suppressed_buffer_is_never_scheduled()
    {
        var scheduler = NewScheduler();
        var id = BufferId.New();

        Assert.False(scheduler.SetSuppressed(id, suppressed: true));

        scheduler.Record(id, "secret", T0);

        Assert.Equal(0, scheduler.PendingCount);
        Assert.Empty(scheduler.TakeDue(T0.AddSeconds(10)));
        Assert.Empty(scheduler.TakeAll());
    }

    [Fact]
    public void Suppressing_drops_what_was_already_queued()
    {
        // Marking a tab ephemeral has to take effect on text typed a moment ago, not
        // only on text typed next.
        var scheduler = NewScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "typed before the toggle", T0);

        Assert.True(scheduler.SetSuppressed(id, suppressed: true));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Lifting_suppression_starts_saving_again()
    {
        // Ephemeral is a toggle. A setting that cannot be turned back off is a trap.
        var scheduler = NewScheduler();
        var id = BufferId.New();

        scheduler.SetSuppressed(id, suppressed: true);
        scheduler.Record(id, "ignored", T0);

        scheduler.SetSuppressed(id, suppressed: false);
        scheduler.Record(id, "kept", T0);

        Assert.False(scheduler.IsSuppressed(id));
        Assert.Equal("kept", Assert.Single(scheduler.TakeDue(T0.AddSeconds(1))).ReadText());
    }

    [Fact]
    public void A_suppressed_buffer_is_not_revived_by_a_requeue()
    {
        // The failure this closes: a write is taken, the tab is marked ephemeral, the
        // write fails, and the retry puts the secret straight back on the queue.
        var scheduler = NewScheduler();
        var id = BufferId.New();

        scheduler.Record(id, "in flight", T0);
        var taken = Assert.Single(scheduler.TakeDue(T0.AddSeconds(1)));

        scheduler.SetSuppressed(id, suppressed: true);
        scheduler.Requeue(taken, T0.AddSeconds(1));

        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void Suppression_and_discarding_are_independent()
    {
        var scheduler = NewScheduler();
        var id = BufferId.New();

        scheduler.SetSuppressed(id, suppressed: true);
        scheduler.Discard(id);

        Assert.True(scheduler.IsSuppressed(id));
        Assert.True(scheduler.IsDiscarded(id));

        // Recording clears the discard — a reopened tab must start saving again — but
        // must not clear the suppression.
        scheduler.Record(id, "reopened", T0);

        Assert.True(scheduler.IsSuppressed(id));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void A_snapshot_is_not_read_until_the_write_is_due()
    {
        // The whole reason BufferContent exists: materialising on the editor's thread,
        // per keystroke, is what the deferral avoids.
        var scheduler = NewScheduler();
        var id = BufferId.New();
        var reads = 0;

        scheduler.Record(
            id,
            BufferContent.FromSnapshot(() =>
            {
                reads++;
                return "materialised";
            }),
            T0);

        Assert.Equal(0, reads);

        var due = Assert.Single(scheduler.TakeDue(T0.AddSeconds(1)));

        Assert.Equal(0, reads);
        Assert.Equal("materialised", due.ReadText());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Coalescing_drops_the_superseded_snapshot_without_reading_it()
    {
        // A burst of typing must cost one materialisation, not one per keystroke.
        var scheduler = NewScheduler();
        var id = BufferId.New();
        var reads = 0;

        for (var i = 0; i < 50; i++)
        {
            var revision = i;

            scheduler.Record(
                id,
                BufferContent.FromSnapshot(() =>
                {
                    reads++;
                    return $"revision {revision}";
                }),
                T0.AddMilliseconds(i * 10));
        }

        var due = Assert.Single(scheduler.TakeDue(T0.AddSeconds(5)));

        Assert.Equal("revision 49", due.ReadText());
        Assert.Equal(1, reads);
    }
}
