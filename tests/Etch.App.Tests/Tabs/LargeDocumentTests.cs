using System.Text;
using Etch.App.Tabs;
using Etch.Core.Documents;
using Xunit;

namespace Etch.App.Tests.Tabs;

/// <summary>
/// What a tab switches on follows the text it holds, and what it saves does not change
/// under it.
/// </summary>
/// <remarks>
/// <para>
/// Small thresholds rather than the shipped megabytes, so the tests move kilobytes. The
/// rules are the same numbers scaled down; the long-line limit is a constant and is used
/// at its real size, which is only ten thousand characters.
/// </para>
/// <para>
/// Every edit goes through the document, the way typing and pasting do, because the
/// re-derivation hangs off the document's own change events and a test that set state some
/// other way would prove nothing about the path that matters.
/// </para>
/// </remarks>
public class LargeDocumentTests
{
    private const long Kib = 1024;

    /// <summary>Folding off past 1 KiB, highlighting off past 2 KiB, refused past 64 KiB.</summary>
    private static readonly DocumentSizePolicy Small = new(1 * Kib, 2 * Kib, 64 * Kib);

    private static readonly string LongLine = new('x', DocumentSizePolicy.LongLineLength + 1);

    private static async Task<Workspace> OpenAsync(TemporaryDataDirectory directory, DocumentSizePolicy? policy = null)
    {
        var workspace = Workspace.Create(
            directory.Paths,
            WorkspaceOptions.Default with { SizePolicy = policy ?? DocumentSizePolicy.Default });

        await workspace.RestoreAsync(TestContext.Current.CancellationToken);

        return workspace;
    }

    /// <summary>Text of about <paramref name="characters"/>, in short lines, so no line is long.</summary>
    private static string Lines(int characters)
    {
        var builder = new StringBuilder(characters + 64);

        for (var i = 0; builder.Length < characters; i++)
        {
            builder.Append("line ").Append(i).Append(" of ordinary text\n");
        }

        return builder.ToString();
    }

    [Fact]
    public void Pasting_past_the_plain_text_threshold_switches_highlighting_off_and_keeps_saving() => UiThread.Run(async () =>
    {
        // The defect: a scratch tab is born empty, so it was evaluated once, in the full tier,
        // and kept highlighting whatever was pasted into it. On a 100 MB paste the grammar
        // then read every line above the caret, and the window froze for nineteen seconds.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var tab = workspace.Active!;
        var announced = new List<BufferTab>();
        workspace.CapabilitiesChanged += announced.Add;

        var text = Lines(3000);
        tab.Document!.Text = text;

        Assert.Equal(DocumentTier.PlainText, tab.Capabilities.Tier);
        Assert.False(tab.Capabilities.SyntaxHighlighting);
        Assert.False(tab.Capabilities.Folding);
        Assert.Same(tab, Assert.Single(announced));

        // A scratch tab's journal is the only copy of its text, so growing does not stop it
        // being written.
        Assert.True(tab.IsJournaled);

        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(text, directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void Taking_the_text_away_again_switches_highlighting_back_on() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var tab = workspace.Active!;
        tab.Document!.Text = Lines(3000);
        tab.Document.Text = "small again";

        Assert.Equal(DocumentTier.Full, tab.Capabilities.Tier);
        Assert.True(tab.Capabilities.SyntaxHighlighting);
        Assert.True(tab.Capabilities.Folding);
    });

    [Fact]
    public void Typing_into_a_large_tab_announces_nothing_new() => UiThread.Run(async () =>
    {
        // The notice names the size, so it changes with nearly every keystroke on a large
        // buffer. Redrawing the editor's syntax for a new number in a sentence would be a
        // cost on the keystroke path for nothing.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var tab = workspace.Active!;
        tab.Document!.Text = Lines(3000);

        var announced = 0;
        workspace.CapabilitiesChanged += _ => announced++;

        for (var i = 0; i < 20; i++)
        {
            tab.Document.Insert(tab.Document.TextLength, "typed");
        }

        Assert.Equal(0, announced);
    });

    [Fact]
    public void A_long_line_turns_highlighting_off_and_deleting_it_turns_it_back_on() => UiThread.Run(async () =>
    {
        // A 2 MB line of minified JSON is well inside the full tier by size, and highlighting
        // it did not finish in three minutes.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;
        tab.Document!.Text = "{\"a\":" + LongLine + "}";

        Assert.True(tab.HasLongLines);
        Assert.Equal(DocumentTier.Full, tab.Capabilities.Tier);
        Assert.False(tab.Capabilities.SyntaxHighlighting);
        Assert.False(tab.Capabilities.Folding);
        Assert.Contains("10,000", tab.Capabilities.Notice!, StringComparison.Ordinal);

        tab.Document.Text = "{\"a\": 1}";

        Assert.False(tab.HasLongLines);
        Assert.True(tab.Capabilities.SyntaxHighlighting);
        Assert.Null(tab.Capabilities.Notice);
    });

    [Fact]
    public void A_restored_tab_past_the_plain_text_threshold_keeps_saving() => UiThread.Run(async () =>
    {
        // The defect: a restored tab was evaluated by the size of its journal file, and past
        // the plain-text threshold that meant auto-save off. For a scratch tab the journal is
        // the only copy, so everything typed after the restart was lost at the next exit.
        using var directory = TemporaryDataDirectory.Create();

        await using (var first = await OpenAsync(directory))
        {
            first.Active!.Document!.Text = Lines(3000);
            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using var second = await OpenAsync(directory, Small);

        var tab = second.Active!;

        Assert.Equal(DocumentTier.PlainText, tab.Capabilities.Tier);
        Assert.True(tab.IsJournaled);

        var edited = tab.Document!.Text + "typed after the restart\n";
        tab.Document.Text = edited;

        await second.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(edited, directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void A_restored_tab_holding_a_long_line_is_not_highlighted() => UiThread.Run(async () =>
    {
        // The launch that restored such a tab never drew its first frame, and it restored it
        // again at every launch after that.
        using var directory = TemporaryDataDirectory.Create();

        await using (var first = await OpenAsync(directory))
        {
            first.Active!.Document!.Text = "[" + LongLine + "]";
            await first.ShutdownAsync(TestContext.Current.CancellationToken);
        }

        await using var second = await OpenAsync(directory);

        var tab = second.Active!;

        Assert.True(tab.HasLongLines);
        Assert.False(tab.Capabilities.SyntaxHighlighting);
        Assert.True(tab.IsJournaled);
    });

    [Fact]
    public void A_file_opened_past_the_plain_text_threshold_stays_unjournaled_when_it_shrinks() => UiThread.Run(async () =>
    {
        // Journaling is decided when the file is opened. The file is its copy, and a shadow
        // started halfway through would be a second, older truth to restore from.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var path = Path.Combine(directory.Root, "big.log");
        await File.WriteAllTextAsync(path, Lines(3000), TestContext.Current.CancellationToken);

        var tab = await workspace.OpenFileAsync(path, TestContext.Current.CancellationToken);

        Assert.NotNull(tab);
        Assert.False(tab!.IsJournaled);

        tab.Document!.Text = "now small";
        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.False(tab.IsJournaled);
        Assert.True(tab.Capabilities.SyntaxHighlighting);
        Assert.Null(directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void A_file_opened_small_keeps_saving_after_it_grows() => UiThread.Run(async () =>
    {
        // The other direction, and the one that would lose work: withdrawing auto-save from a
        // file tab somebody pasted into would leave its shadow copy behind as it was.
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var path = Path.Combine(directory.Root, "small.txt");
        await File.WriteAllTextAsync(path, "small", TestContext.Current.CancellationToken);

        var tab = await workspace.OpenFileAsync(path, TestContext.Current.CancellationToken);

        var grown = Lines(3000);
        tab!.Document!.Text = grown;
        await workspace.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DocumentTier.PlainText, tab.Capabilities.Tier);
        Assert.True(tab.IsJournaled);
        Assert.Equal(grown, directory.ReadBuffer(tab.Id));
    });

    [Fact]
    public void Journaling_cannot_be_changed_once_a_tab_is_open() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory);

        var tab = workspace.Active!;

        Assert.Throws<ArgumentException>(
            () => tab.AdoptCapabilities(tab.Capabilities with { Journaling = !tab.Capabilities.Journaling }));
    });

    [Fact]
    public void A_tab_may_not_grow_past_the_ceiling() => UiThread.Run(async () =>
    {
        using var directory = TemporaryDataDirectory.Create();
        await using var workspace = await OpenAsync(directory, Small);

        var tab = workspace.Active!;

        Assert.True(tab.Fits(64 * Kib, out _));
        Assert.False(tab.Fits((64 * Kib) + 1, out var refusal));
        Assert.Contains("64 KB", refusal, StringComparison.Ordinal);
    });

}
