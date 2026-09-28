using System.Text.Json;
using Etch.App.Tabs;
using Etch.Persistence.Model;
using Xunit;

namespace Etch.App.Tests.Tabs;

/// <summary>
/// The orderings that decide whether text survives.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Workspace"/> is where Etch's central promise is actually kept (no save
/// dialog, no unsaved-changes prompt, no destructive close) and every one of those
/// rests on an ordering that is invisible in the code unless you go looking: journal
/// before trashing, subscribe only after hydrating, suppress before deleting, revive
/// before restoring. Getting one of them backwards loses somebody's notes silently, so
/// they are asserted here rather than reasoned about.
/// </para>
/// <para>
/// Every test runs inside <see cref="UiThread.Run"/>. AvalonEdit's document has thread
/// affinity and xUnit resumes continuations wherever it likes; pumping a single thread
/// reproduces the dispatcher the application really runs on, so these tests exercise
/// the same threading model rather than a flattened one.
/// </para>
/// </remarks>
public class WorkspaceTests
{
    /// <summary>Builds a restored workspace over <paramref name="directory"/>.</summary>
    private static async Task<Workspace> OpenAsync(TemporaryDataDirectory directory)
    {
        var workspace = Workspace.Create(directory.Paths);
        await workspace.RestoreAsync(TestContext.Current.CancellationToken);

        return workspace;
    }

    private static void SetText(BufferTab tab, string text)
    {
        Assert.NotNull(tab.Document);

        // Through the document, exactly as typing does, so the journal subscription is
        // what carries the change: asserting on text set some other way would prove
        // nothing about the path that matters.
        tab.Document!.Text = text;
    }

    [Fact]
    public void A_first_launch_opens_exactly_one_empty_tab() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = Assert.Single(workspace.Tabs);

        Assert.Same(tab, workspace.Active);
        Assert.True(tab.IsHydrated);
        Assert.Equal(string.Empty, tab.Document!.Text);
        Assert.Equal("Untitled 1", tab.Title);
    });

    [Fact]
    public void Typing_reaches_the_disk_without_anyone_asking_for_it() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        SetText(tab, "a thought worth keeping");

        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal("a thought worth keeping", directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void A_session_comes_back_with_its_text_after_a_restart() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();

        BufferId id;

        await using (var first = await OpenAsync(directory))
        {
            var tab = first.Active!;
            id = tab.Id;

            tab.Title = "notes";
            SetText(tab, "written before the restart");

            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using var second = await OpenAsync(directory);

        var restored = Assert.Single(second.Tabs);

        Assert.Equal(id, restored.Id);
        Assert.Equal("notes", restored.Title);
        Assert.Equal("written before the restart", restored.Document!.Text);
    });

    [Fact]
    public void Hydrating_a_restored_tab_does_not_blank_its_file() => UiThread.Run(async () =>
    {
        // The failure this guards: the editor raises a change event for the empty document
        // a tab starts with, the journal writes that emptiness over the file it was about
        // to read, and the text is gone. Subscribing only after hydration is what prevents
        // it, and nothing else in the suite would notice if that ordering were reversed.
        using var directory = TemporaryDataDirectory.Create();

        BufferId id;

        await using (var first = await OpenAsync(directory))
        {
            id = first.Active!.Id;
            SetText(first.Active!, "must survive being reopened");
            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using (var second = await OpenAsync(directory))
        {
            await second.FlushAsync(TestContext.Current.CancellationToken);
            await second.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("must survive being reopened", directory.ReadBuffer(id));
    });

    [Fact]
    public void Closing_a_tab_flushes_it_first_and_then_trashes_it() => UiThread.Run(async () =>
    {
        // Typing and closing immediately is the ordinary case, and it is the one that
        // loses text if the close does not flush before it moves the file.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        SetText(tab, "typed a moment before closing");

        await workspace.CloseAsync(tab, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(tab, workspace.Tabs);
        Assert.Null(directory.ReadBuffer(tab.Id));
        Assert.Equal("typed a moment before closing", directory.ReadTrashed(tab.Id));
    });

    [Fact]
    public void Closing_the_last_tab_leaves_a_new_one_rather_than_an_empty_window() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        await workspace.CloseAsync(workspace.Active!, TestContext.Current.CancellationToken);

        Assert.Single(workspace.Tabs);
        Assert.NotNull(workspace.Active);
    });

    [Fact]
    public void A_closed_tab_reopens_with_its_text() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        SetText(tab, "closed by mistake");

        await workspace.CloseAsync(tab, TestContext.Current.CancellationToken);

        Assert.True(workspace.CanReopenClosed);

        var reopened = await workspace.ReopenLastClosedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reopened);
        Assert.Equal(tab.Id, reopened!.Id);
        Assert.Equal("closed by mistake", reopened.Document!.Text);
    });

    [Fact]
    public void A_reopened_file_tab_is_still_a_file_tab() => UiThread.Run(async () =>
    {
        // Without the record kept at close, a closed file tab comes back as an untitled
        // scratch buffer holding the file's text, and Ctrl+S stops writing through to the
        // file it came from, silently offering a Save As instead.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var file = Path.Combine(directory.Root, "settings.json");
        await File.WriteAllTextAsync(file, "{ \"real\": true }", TestContext.Current.CancellationToken);

        var opened = await workspace.OpenFileAsync(file, TestContext.Current.CancellationToken);

        Assert.NotNull(opened);
        Assert.Equal(BufferKind.File, opened!.Kind);

        await workspace.CloseAsync(opened, TestContext.Current.CancellationToken);

        var reopened = await workspace.ReopenLastClosedAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(reopened);
        Assert.Equal(BufferKind.File, reopened!.Kind);
        Assert.Equal(file, reopened.FilePath);
        Assert.Equal("settings.json", reopened.Title);
    });

    [Fact]
    public void An_ephemeral_tab_never_reaches_the_disk() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        workspace.SetEphemeral(tab, ephemeral: true);

        SetText(tab, "an api key");

        await workspace.FlushAsync(TestContext.Current.CancellationToken);
        await workspace.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.False(tab.IsJournaled);
        Assert.Null(directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void Marking_a_tab_ephemeral_removes_what_was_already_written() => UiThread.Run(async () =>
    {
        // The realistic sequence: someone pastes a secret, then remembers the toggle. Text
        // typed a moment ago is exactly the text they want gone.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        SetText(tab, "a password, pasted before thinking");
        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(directory.ReadBuffer(tab.Id));

        workspace.SetEphemeral(tab, ephemeral: true);

        Assert.Null(directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void An_ephemeral_tab_is_not_named_in_the_session_index() => UiThread.Run(async () =>
    {
        // The index carries titles, and a tab called "prod-db-password" surviving a restart
        // would defeat the whole point on its own.
        using var directory = TemporaryDataDirectory.Create();

        await using (var workspace = await OpenAsync(directory))
        {
            var tab = workspace.Active!;
            tab.Title = "prod-db-password";
            workspace.SetEphemeral(tab, ephemeral: true);

            await workspace.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        var index = await File.ReadAllTextAsync(directory.Paths.SessionFile, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("prod-db-password", index, StringComparison.Ordinal);
    });

    [Fact]
    public void An_index_write_that_started_first_cannot_land_last() => UiThread.Run(async () =>
    {
        // Index saves are fire-and-forget and shutdown writes the index too, so both
        // targeted one path with nothing deciding the order. The orderly exit could be
        // overwritten by a snapshot taken moments earlier, and the next launch would
        // announce a crash that never happened and restore a stale tab list.
        using var directory = TemporaryDataDirectory.Create();

        await using (var workspace = await OpenAsync(directory))
        {
            SetText(workspace.Active!, "text worth keeping");

            // Started and deliberately not awaited, so a write carrying cleanShutdown
            // false is outstanding when shutdown publishes. What makes this deterministic
            // rather than a race is that no await separates the two captures below: both
            // run to their first suspension point on this thread, so the shutdown
            // snapshot is guaranteed to be the newer of the two, whichever order the
            // write gate then hands them out in.
            var earlier = workspace.SaveSessionAsync(TestContext.Current.CancellationToken);

            var plan = workspace.PrepareShutdown();

            // Nothing may queue a *newer* snapshot after that point either: it would
            // carry a higher revision than the shutdown one, so no ordering rule could
            // discard it. This call has to be refused outright.
            await workspace.SaveSessionAsync(TestContext.Current.CancellationToken);

            await workspace.CompleteShutdownAsync(plan, TestContext.Current.CancellationToken);
            await earlier;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(directory.Paths.SessionFile, TestContext.Current.CancellationToken));

        Assert.True(document.RootElement.GetProperty("cleanShutdown").GetBoolean());
    });

    [Fact]
    public void Nothing_writes_the_index_after_the_workspace_is_disposed() => UiThread.Run(async () =>
    {
        // Index writes are fire-and-forget, so one could still be renaming a file over
        // session.json after the workspace said it was finished: behind a caller that
        // has already gone on to read that file, or to delete the directory.
        //
        // Disposed without shutting down first, deliberately: ShutdownAsync drains the
        // write gate, so a test that called it would leave nothing in flight to catch.
        using var directory = TemporaryDataDirectory.Create();

        await using (var workspace = await OpenAsync(directory))
        {
            SetText(workspace.Active!, "text worth keeping");

            // Two requests: the first is still in flight, so the second only sets the
            // flag that makes the save loop go round again, which is the iteration that
            // used to run after dispose.
            workspace.NewScratch();
            workspace.RequestSessionSave();
        }

        // Deleted rather than timestamped. A later write recreates the file, and
        // "does not exist" is not a comparison that can tie on a coarse clock, and the
        // delete succeeding is itself proof that no handle is still open on it.
        File.Delete(directory.Paths.SessionFile);

        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(directory.Paths.SessionFile));
    });

    [Fact]
    public void Lifting_the_ephemeral_mark_starts_saving_again() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        workspace.SetEphemeral(tab, ephemeral: true);
        SetText(tab, "not written");

        workspace.SetEphemeral(tab, ephemeral: false);
        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.True(tab.IsJournaled);
        Assert.Equal("not written", directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void A_new_tab_survives_a_restart_even_if_nothing_was_typed_into_it() => UiThread.Run(async () =>
    {
        // A tab created and renamed but never typed into has no text of its own. Without
        // an empty buffer file behind it the restore drops it as an index entry with
        // nothing there, and the rename goes with it.
        using var directory = TemporaryDataDirectory.Create();

        await using (var first = await OpenAsync(directory))
        {
            var second = first.NewScratch();
            second.Title = "for later";

            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using var reopened = await OpenAsync(directory);

        Assert.Equal(2, reopened.Tabs.Count);
        Assert.Contains(reopened.Tabs, tab => tab.Title == "for later");
    });

    [Fact]
    public void Opening_the_same_file_twice_activates_the_tab_already_holding_it() => UiThread.Run(async () =>
    {
        // Two tabs over one file would journal to two shadow copies and then race each
        // other on Ctrl+S, with no way for the user to tell which won.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var file = Path.Combine(directory.Root, "notes.txt");
        await File.WriteAllTextAsync(file, "contents", TestContext.Current.CancellationToken);

        var first = await workspace.OpenFileAsync(file, TestContext.Current.CancellationToken);
        var second = await workspace.OpenFileAsync(file, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(workspace.Tabs, tab => tab.Kind == BufferKind.File);
    });

    [Fact]
    public void A_shutdown_records_that_it_was_clean() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();

        await using (var workspace = await OpenAsync(directory))
        {
            await workspace.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        var index = await File.ReadAllTextAsync(directory.Paths.SessionFile, TestContext.Current.CancellationToken);

        Assert.Contains("\"cleanShutdown\": true", index, StringComparison.Ordinal);
    });
}
