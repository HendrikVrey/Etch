using Etch.App.Tabs;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.App.Tests.Tabs;

/// <summary>
/// <see cref="Workspace.WipeAllAsync"/> and <see cref="Workspace.ApplySettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// The wipe is the only irreversible operation in Etch and the only place where closing a
/// tab destroys its text on purpose, so its ordering is the one thing here worth pinning:
/// pending journal writes dropped before the delete, the session index shut out for the
/// duration, and nothing left behind that could adopt a deleted buffer on the next
/// launch.
/// </para>
/// <para>
/// In <see cref="UiThread.Run"/> like the rest of the workspace suite, because AvalonEdit's
/// document has thread affinity and these tests type into one.
/// </para>
/// </remarks>
public class WorkspaceSettingsTests
{
    private static async Task<Workspace> OpenAsync(TemporaryDataDirectory directory)
    {
        var workspace = Workspace.Create(directory.Paths);
        await workspace.RestoreAsync(TestContext.Current.CancellationToken);

        return workspace;
    }

    [Fact]
    public void A_wipe_leaves_one_empty_tab_and_nothing_on_disk() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var first = workspace.Active!;
        first.Document!.Text = "a secret pasted into a scratchpad";

        var second = workspace.NewScratch();
        second.Document!.Text = "and another";

        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        var firstId = first.Id;
        var secondId = second.Id;

        var result = await workspace.WipeAllAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Deleted > 0);

        // Read straight off the disk rather than through BufferStore: the point of the
        // assertion is what is actually there.
        Assert.Null(directory.ReadBuffer(firstId));
        Assert.Null(directory.ReadBuffer(secondId));
        Assert.Null(directory.ReadTrashed(firstId));
        Assert.Null(directory.ReadTrashed(secondId));

        // Somewhere to type, not an empty disabled editor.
        var remaining = Assert.Single(workspace.Tabs);

        Assert.Same(remaining, workspace.Active);
        Assert.Equal(string.Empty, remaining.Document!.Text);
        Assert.NotEqual(firstId, remaining.Id);
        Assert.NotEqual(secondId, remaining.Id);
    });

    [Fact]
    public void A_wipe_drops_text_that_had_not_reached_the_disk_yet() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        var id = tab.Id;

        // Deliberately NOT flushed. This is the resurrection case the ordering exists to
        // prevent: a batch already handed to the writer completing after the delete and
        // putting the secret back on disk.
        tab.Document!.Text = "typed a moment before the wipe";

        await workspace.WipeAllAsync(TestContext.Current.CancellationToken);

        // Settle anything that might still have been in flight, then look again.
        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Null(directory.ReadBuffer(id));
        Assert.Null(directory.ReadTrashed(id));
    });

    [Fact]
    public void A_wipe_empties_the_reopen_stack() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var doomed = workspace.NewScratch();
        doomed.Document!.Text = "closed, and recoverable until the wipe";

        await workspace.CloseAsync(doomed, TestContext.Current.CancellationToken);

        Assert.True(workspace.CanReopenClosed);

        await workspace.WipeAllAsync(TestContext.Current.CancellationToken);

        // The files behind the stack are gone, so an entry left on it would be an offer
        // Etch could not honour, and the entry itself holds the tab's title.
        Assert.False(workspace.CanReopenClosed);
        Assert.Null(directory.ReadTrashed(doomed.Id));
    });

    [Fact]
    public void A_wipe_does_not_leave_the_old_session_index_behind() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;

        // A renamed tab, because the index holds titles the user wrote, which is the
        // reason republishing a pre-wipe snapshot would be a leak rather than a nuisance.
        tab.Title = "prod-db-password";

        await workspace.SaveSessionAsync(TestContext.Current.CancellationToken);

        Assert.Contains("prod-db-password", File.ReadAllText(directory.Paths.SessionFile), StringComparison.Ordinal);

        await workspace.WipeAllAsync(TestContext.Current.CancellationToken);
        await workspace.SaveSessionAsync(TestContext.Current.CancellationToken);

        var index = File.Exists(directory.Paths.SessionFile)
            ? File.ReadAllText(directory.Paths.SessionFile)
            : string.Empty;

        Assert.DoesNotContain("prod-db-password", index, StringComparison.Ordinal);
    });

    [Fact]
    public void Retention_of_zero_makes_the_next_close_unrecoverable() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        // The privacy affordance §12 requires, and the one setting whose consequence is
        // irreversible, so it is worth proving it reaches the trash sweep rather than
        // merely being stored.
        workspace.ApplySettings(EtchSettings.Default with { TrashRetentionDays = 0 });

        var tab = workspace.NewScratch();
        tab.Document!.Text = "should not survive the close";

        await workspace.FlushAsync(TestContext.Current.CancellationToken);
        await workspace.CloseAsync(tab, TestContext.Current.CancellationToken);

        Assert.Null(directory.ReadBuffer(tab.Id));
        Assert.Null(directory.ReadTrashed(tab.Id));
        Assert.False(workspace.CanReopenClosed);
    });

    [Fact]
    public void The_default_retention_keeps_a_closed_tab_recoverable() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        // The other side of the same switch. Without this, a retention bug that deleted
        // everything would satisfy the test above and look like a pass.
        workspace.ApplySettings(EtchSettings.Default);

        var tab = workspace.NewScratch();
        tab.Document!.Text = "should be in the trash afterwards";

        await workspace.FlushAsync(TestContext.Current.CancellationToken);
        await workspace.CloseAsync(tab, TestContext.Current.CancellationToken);

        Assert.NotNull(directory.ReadTrashed(tab.Id));
        Assert.True(workspace.CanReopenClosed);
    });

    [Fact]
    public void Applying_sanitised_settings_never_throws() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        // Retention becomes a TimeSpan, which int.MaxValue days is not. Every caller
        // sanitises first; this asserts the seam between the two actually holds, for the
        // hostile values a hand-edited settings.json can produce.
        EtchSettings[] hostile =
        [
            EtchSettings.Default with { TrashRetentionDays = -1 },
            EtchSettings.Default with { TrashRetentionDays = int.MaxValue },
        ];

        foreach (var candidate in hostile)
        {
            workspace.ApplySettings(candidate.Sanitised());
        }

        await Task.CompletedTask;
    });
}
