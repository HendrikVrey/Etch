using Etch.App.Tabs;
using Xunit;

namespace Etch.App.Tests.Tabs;

/// <summary>
/// Reordering and pinning: the invariant that pinned tabs occupy the front of the strip.
/// </summary>
/// <remarks>
/// <para>
/// The whole feature rests on one rule, pinned tabs are a contiguous run at index zero,
/// and three separate operations have to preserve it: pinning, unpinning, and dragging.
/// Break it in any of them and the symptom is not an exception, it is tabs that refuse to
/// move for no visible reason, because a drag clamps against a boundary that is not where
/// the strip says it is.
/// </para>
/// <para>
/// Every test runs inside <see cref="UiThread.Run"/>, for the reason set out in
/// <see cref="WorkspaceTests"/>: AvalonEdit's document has thread affinity and xUnit
/// resumes continuations wherever it likes.
/// </para>
/// </remarks>
public class TabOrderTests
{
    private static async Task<Workspace> OpenAsync(TemporaryDataDirectory directory)
    {
        var workspace = Workspace.Create(directory.Paths);
        await workspace.RestoreAsync(TestContext.Current.CancellationToken);

        return workspace;
    }

    /// <summary>Adds tabs until the strip holds <paramref name="count"/>, and names them.</summary>
    private static IReadOnlyList<BufferTab> Fill(Workspace workspace, int count)
    {
        while (workspace.Tabs.Count < count)
        {
            _ = workspace.NewScratch();
        }

        for (var i = 0; i < workspace.Tabs.Count; i++)
        {
            workspace.Tabs[i].Title = ((char)('a' + i)).ToString();
        }

        return [.. workspace.Tabs];
    }

    private static string Order(Workspace workspace) => string.Concat(workspace.Tabs.Select(static tab => tab.Title));

    [Fact]
    public void Pinning_moves_a_tab_to_the_front_of_the_strip() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        workspace.SetPinned(tabs[2], pinned: true);

        Assert.Equal("cabd", Order(workspace));
        Assert.True(tabs[2].IsPinned);
    });

    [Fact]
    public void A_second_pin_lands_behind_the_first() => UiThread.Run(async () =>
    {
        // Newest pinned last, so pinning a run of tabs keeps them in the order they were
        // pinned rather than reversing it.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        workspace.SetPinned(tabs[3], pinned: true);
        workspace.SetPinned(tabs[1], pinned: true);

        Assert.Equal("dbac", Order(workspace));
    });

    [Fact]
    public void Unpinning_lands_a_tab_at_the_head_of_the_rest() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        workspace.SetPinned(tabs[0], pinned: true);
        workspace.SetPinned(tabs[1], pinned: true);
        Assert.Equal("abcd", Order(workspace));

        workspace.SetPinned(tabs[0], pinned: false);

        Assert.Equal("bacd", Order(workspace));
        Assert.False(tabs[0].IsPinned);
    });

    [Fact]
    public void A_drag_reorders_within_the_strip() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        // Dragging rightwards onto a tab lands after it; leftwards lands before it. That
        // falls out of Move's remove-then-reinsert, and it is what makes the strip follow
        // the pointer rather than lag one position behind it.
        workspace.Move(tabs[0], 2);
        Assert.Equal("bcad", Order(workspace));

        workspace.Move(tabs[3], 0);
        Assert.Equal("dbca", Order(workspace));
    });

    [Fact]
    public void A_drag_cannot_carry_an_unpinned_tab_into_the_pinned_group() => UiThread.Run(async () =>
    {
        // The one that matters. Letting a drag pin a tab as a side effect would mean one
        // gesture doing two things, and the one the user did not intend is the one that
        // survives into the next session.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        workspace.SetPinned(tabs[0], pinned: true);
        workspace.SetPinned(tabs[1], pinned: true);

        workspace.Move(tabs[3], 0);

        Assert.Equal("abdc", Order(workspace));
        Assert.False(tabs[3].IsPinned);
    });

    [Fact]
    public void A_drag_cannot_carry_a_pinned_tab_out_of_the_pinned_group() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tabs = Fill(workspace, 4);

        workspace.SetPinned(tabs[0], pinned: true);
        workspace.SetPinned(tabs[1], pinned: true);

        workspace.Move(tabs[0], 3);

        Assert.Equal("bacd", Order(workspace));
        Assert.True(tabs[0].IsPinned);
    });

    [Fact]
    public void Pinned_tabs_are_still_at_the_front_after_a_restart() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();

        await using (var first = await OpenAsync(directory))
        {
            var tabs = Fill(first, 3);

            first.SetPinned(tabs[2], pinned: true);
            Assert.Equal("cab", Order(first));

            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using var second = await OpenAsync(directory);

        Assert.Equal("cab", Order(second));
        Assert.True(second.Tabs[0].IsPinned);
    });
}
