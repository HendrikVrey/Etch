using Etch.Persistence.Model;
using Etch.Persistence.Session;
using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Session;

/// <summary>
/// Restore is the crash-recovery guarantee, and it runs on every launch rather than
/// only after a crash. These tests are mostly about the two ways the index and the
/// disk can disagree, both of which are normal after a kill.
/// </summary>
public class SessionRestorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static BufferRecord Record(BufferId id, string title = "Untitled") =>
        new(id, BufferKind.Scratch, title, null, 0, 1, false, null, Now);

    private static SessionSnapshot Index(BufferId? active, params BufferRecord[] records) =>
        new(SessionSnapshot.CurrentVersion, true, active, records);

    [Fact]
    public void An_index_that_matches_the_disk_restores_verbatim()
    {
        var first = BufferId.New();
        var second = BufferId.New();

        var result = SessionRestorer.Reconcile(
            Index(second, Record(first, "One"), Record(second, "Two")),
            [first, second],
            Now);

        Assert.Equal(2, result.Buffers.Count);
        Assert.Equal("One", result.Buffers[0].Title);
        Assert.Equal(second, result.ActiveBufferId);
        Assert.Equal(0, result.RecoveredCount);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void Tab_order_is_preserved()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => BufferId.New()).ToArray();
        var records = ids.Select((id, i) => Record(id, $"Tab {i}")).ToArray();

        var result = SessionRestorer.Reconcile(Index(null, records), ids, Now);

        Assert.Equal(ids, result.Buffers.Select(buffer => buffer.Id));
    }

    [Fact]
    public void Text_on_disk_that_the_index_never_recorded_is_recovered()
    {
        // The crash window that matters: the buffer was written and the index never
        // caught up. There is real text here and it is recovered without a prompt.
        var known = BufferId.New();
        var orphan = BufferId.New();

        var result = SessionRestorer.Reconcile(Index(known, Record(known)), [known, orphan], Now);

        Assert.Equal(2, result.Buffers.Count);
        Assert.Equal(1, result.RecoveredCount);
        Assert.Contains(result.Buffers, buffer => buffer.Id == orphan);
    }

    [Fact]
    public void A_recovered_buffer_is_a_scratch_buffer_with_a_distinguishable_title()
    {
        var orphans = new[] { BufferId.New(), BufferId.New() };

        var result = SessionRestorer.Reconcile(SessionSnapshot.Empty, orphans, Now);

        Assert.Equal(2, result.RecoveredCount);
        Assert.All(result.Buffers, buffer =>
        {
            // Scratch, never File: the index that would have said which file it came
            // from is exactly what was lost, and guessing would risk writing text
            // into the wrong place.
            Assert.Equal(BufferKind.Scratch, buffer.Kind);
            Assert.Null(buffer.FilePath);
            Assert.False(string.IsNullOrWhiteSpace(buffer.Title));
        });
        Assert.Equal(2, result.Buffers.Select(buffer => buffer.Title).Distinct().Count());
    }

    [Fact]
    public void An_index_entry_with_no_text_behind_it_is_dropped()
    {
        // The other crash window: the index was written and the buffer never was.
        // Restoring it as an empty tab would look like Etch lost the contents.
        var present = BufferId.New();
        var missing = BufferId.New();

        var result = SessionRestorer.Reconcile(
            Index(present, Record(present), Record(missing)),
            [present],
            Now);

        Assert.Single(result.Buffers);
        Assert.Equal(1, result.DroppedCount);
        Assert.DoesNotContain(result.Buffers, buffer => buffer.Id == missing);
    }

    [Fact]
    public void The_active_tab_falls_back_when_its_text_is_gone()
    {
        var survivor = BufferId.New();
        var lost = BufferId.New();

        var result = SessionRestorer.Reconcile(
            Index(lost, Record(survivor), Record(lost)),
            [survivor],
            Now);

        Assert.Equal(survivor, result.ActiveBufferId);
    }

    [Fact]
    public void The_active_tab_always_names_a_tab_that_came_back()
    {
        // The invariant behind the case above, stated on its own because it is the one
        // the caller relies on: an id that is not in Buffers gives the app an active tab
        // it cannot select, and every route into the editor has to cope with that or
        // open blank.
        var lost = BufferId.New();

        var result = SessionRestorer.Reconcile(Index(lost, Record(lost)), [], Now);

        Assert.Empty(result.Buffers);
        Assert.Equal(1, result.DroppedCount);
        Assert.Null(result.ActiveBufferId);
    }

    [Fact]
    public void An_empty_session_restores_to_nothing()
    {
        var result = SessionRestorer.Reconcile(SessionSnapshot.Empty, [], Now);

        Assert.Empty(result.Buffers);
        Assert.Null(result.ActiveBufferId);
    }

    [Fact]
    public void Duplicate_index_entries_are_collapsed()
    {
        var id = BufferId.New();

        var result = SessionRestorer.Reconcile(
            Index(id, Record(id, "First"), Record(id, "Duplicate")),
            [id],
            Now);

        Assert.Equal("First", Assert.Single(result.Buffers).Title);
    }

    [Fact]
    public void A_recovered_buffer_is_not_also_counted_as_dropped()
    {
        var orphan = BufferId.New();

        var result = SessionRestorer.Reconcile(SessionSnapshot.Empty, [orphan], Now);

        Assert.Equal(1, result.RecoveredCount);
        Assert.Equal(0, result.DroppedCount);
        Assert.Equal(orphan, result.ActiveBufferId);
    }

    [Fact]
    public async Task Restoring_from_disk_reports_everything_startup_needs()
    {
        using var workspace = TemporaryWorkspace.Create();
        var kept = BufferId.New();
        var orphan = BufferId.New();

        await workspace.Buffers.WriteAsync(kept, "indexed text", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(orphan, "text the index never heard about", TestContext.Current.CancellationToken);
        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(SessionSnapshot.CurrentVersion, CleanShutdown: false, kept, [Record(kept, "Kept")]),
            TestContext.Current.CancellationToken);

        var restorer = new SessionRestorer(workspace.Buffers, workspace.Sessions);
        var restored = await restorer.RestoreAsync(RetentionPolicy.Default, Now, TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Loaded, restored.IndexStatus);
        Assert.True(restored.WasUncleanShutdown);
        Assert.Equal(2, restored.Buffers.Count);
        Assert.Equal(1, restored.RecoveredCount);
        Assert.Equal(kept, restored.ActiveBufferId);
    }

    [Fact]
    public async Task A_destroyed_index_still_gets_the_text_back()
    {
        // The scenario the whole design is for. session.json is gone or unreadable;
        // the text is not, and the text is the part that matters.
        using var workspace = TemporaryWorkspace.Create();
        var first = BufferId.New();
        var second = BufferId.New();

        await workspace.Buffers.WriteAsync(first, "important note", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(second, "another one", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(workspace.Paths.SessionFile, "}}} not json {{{", TestContext.Current.CancellationToken);

        var restorer = new SessionRestorer(workspace.Buffers, workspace.Sessions);
        var restored = await restorer.RestoreAsync(RetentionPolicy.Default, Now, TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Unreadable, restored.IndexStatus);
        Assert.Equal(2, restored.Buffers.Count);
        Assert.Equal(2, restored.RecoveredCount);
        Assert.NotNull(restored.Notice);

        foreach (var buffer in restored.Buffers)
        {
            Assert.NotNull(await workspace.Buffers.ReadAsync(buffer.Id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Restore_sweeps_expired_trash_and_partial_writes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var expired = BufferId.New();

        await workspace.Buffers.WriteAsync(expired, "long gone", TestContext.Current.CancellationToken);
        workspace.Buffers.Trash(expired, RetentionPolicy.Default, Now.AddDays(-30));

        await File.WriteAllTextAsync(
            Path.Combine(workspace.Paths.BuffersDirectory, "abcd.txt.11112222.tmp"),
            "half a write",
            TestContext.Current.CancellationToken);

        var restorer = new SessionRestorer(workspace.Buffers, workspace.Sessions);
        var restored = await restorer.RestoreAsync(RetentionPolicy.Default, Now, TestContext.Current.CancellationToken);

        Assert.Equal(1, restored.PrunedFromTrash);
        Assert.Equal(1, restored.SweptTemporaryFiles);
        Assert.Empty(restored.Buffers);
    }

    [Fact]
    public async Task A_first_launch_restores_cleanly_from_nothing()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        var restorer = new SessionRestorer(workspace.Buffers, workspace.Sessions);
        var restored = await restorer.RestoreAsync(RetentionPolicy.Default, Now, TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Missing, restored.IndexStatus);
        Assert.Empty(restored.Buffers);
        Assert.Null(restored.ActiveBufferId);
        Assert.False(restored.WasUncleanShutdown);
        Assert.Null(restored.Notice);
    }
}
