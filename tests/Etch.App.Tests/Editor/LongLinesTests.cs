using System.Windows.Media.TextFormatting;
using Etch.App.Editor;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;
using Xunit;

namespace Etch.App.Tests.Editor;

/// <summary>
/// The two halves of the long-line fix: knowing a document has such a line, and not laying
/// all of one out.
/// </summary>
/// <remarks>
/// A limit of ten rather than the real ten thousand, so each case can be read off the page.
/// Both types take the limit as an argument precisely so that the rule can be tested rather
/// than the constant.
/// </remarks>
public class LongLinesTests
{
    private const int Limit = 10;

    private static readonly string Long = new('x', Limit + 1);
    private static readonly string AtLimit = new('x', Limit);

    [Fact]
    public void A_document_of_short_lines_has_none()
    {
        var watch = new LongLineWatch(new TextDocument("short\nlines\nonly"), Limit);

        Assert.False(watch.Any);
    }

    [Fact]
    public void A_line_exactly_at_the_limit_is_not_long()
    {
        // The limit is the longest line laid out in full, so it is inclusive: the same
        // convention the size thresholds follow.
        var watch = new LongLineWatch(new TextDocument($"a\n{AtLimit}\nb"), Limit);

        Assert.False(watch.Any);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void A_long_line_already_there_is_found_whatever_ends_the_lines(string newLine)
    {
        var watch = new LongLineWatch(new TextDocument($"a{newLine}{Long}{newLine}b"), Limit);

        Assert.True(watch.Any);
    }

    [Fact]
    public void Pasting_a_long_line_is_noticed()
    {
        var document = new TextDocument("a\nb");
        var watch = new LongLineWatch(document, Limit);

        document.Insert(2, Long + "\n");

        Assert.True(watch.Any);
    }

    [Fact]
    public void Typing_a_line_past_the_limit_is_noticed()
    {
        var document = new TextDocument(AtLimit);
        var watch = new LongLineWatch(document, Limit);

        document.Insert(document.TextLength, "y");

        Assert.True(watch.Any);
    }

    [Fact]
    public void Joining_two_short_lines_into_a_long_one_is_noticed()
    {
        // A removal can make a line long too: deleting the break between two lines.
        var document = new TextDocument("xxxxxx\nxxxxxx");
        var watch = new LongLineWatch(document, Limit);

        Assert.False(watch.Any);

        document.Remove(6, 1);

        Assert.True(watch.Any);
    }

    [Fact]
    public void Typing_inside_a_long_line_keeps_it_long()
    {
        var document = new TextDocument(Long);
        var watch = new LongLineWatch(document, Limit);

        document.Insert(3, "y");
        document.Remove(3, 1);

        Assert.True(watch.Any);
    }

    [Fact]
    public void Deleting_the_only_long_line_clears_it()
    {
        var document = new TextDocument($"a\n{Long}\nb");
        var watch = new LongLineWatch(document, Limit);

        document.Remove(2, Long.Length + 1);

        Assert.False(watch.Any);
        Assert.Equal("a\nb", document.Text);
    }

    [Fact]
    public void Breaking_the_only_long_line_in_two_clears_it()
    {
        // A pure insertion that makes the long line short. Nothing was removed, so a watch
        // that only looked again after a removal would have said "long" for ever.
        var document = new TextDocument(Long);
        var watch = new LongLineWatch(document, Limit);

        document.Insert(Limit / 2, "\n");

        Assert.False(watch.Any);
    }

    [Fact]
    public void Removing_one_long_line_leaves_the_other_one_counted()
    {
        var document = new TextDocument($"{Long}\n{Long}");
        var watch = new LongLineWatch(document, Limit);

        document.Remove(0, Long.Length + 1);

        Assert.True(watch.Any);
    }

    [Fact]
    public void Replacing_everything_with_something_short_clears_it()
    {
        var document = new TextDocument($"a\n{Long}");
        var watch = new LongLineWatch(document, Limit);

        document.Text = "short";

        Assert.False(watch.Any);
    }

    [Fact]
    public void Undoing_the_paste_of_a_long_line_clears_it()
    {
        var document = new TextDocument("a");
        var watch = new LongLineWatch(document, Limit);

        document.Insert(1, "\n" + Long);
        Assert.True(watch.Any);

        document.UndoStack.Undo();

        Assert.False(watch.Any);
    }

    [Fact]
    public void A_short_line_is_left_to_the_other_generators()
    {
        var generator = Generating($"short\n{Long}", out _);

        Assert.Equal(-1, generator.GetFirstInterestedOffset(0));
    }

    [Fact]
    public void A_long_line_is_cut_at_the_limit_and_the_rest_is_one_element()
    {
        var generator = Generating($"ab\n{Long}xyz\ncd", out var document);
        var line = document.GetLineByNumber(2);

        var cut = generator.GetFirstInterestedOffset(line.Offset);

        Assert.Equal(line.Offset + Limit, cut);

        var element = generator.ConstructElement(cut);

        Assert.NotNull(element);
        Assert.Equal(line.EndOffset - cut, element!.DocumentLength);

        // One caret stop wide: that is what lets the caret, a selection and a click step
        // over the hidden part as a single thing.
        Assert.Equal(1, element.VisualLength);
    }

    [Fact]
    public void The_hidden_part_does_not_reach_into_the_next_line()
    {
        var generator = Generating($"{Long}\nnext", out var document);
        var line = document.GetLineByNumber(1);

        var element = generator.ConstructElement(generator.GetFirstInterestedOffset(line.Offset));

        Assert.Equal(line.EndOffset, line.Offset + Limit + element!.DocumentLength);
    }

    [Fact]
    public void The_cut_never_splits_a_surrogate_pair()
    {
        // A high surrogate at the last laid-out position would end the visible part in half
        // a character.
        var text = new string('x', Limit - 1) + "\U0001F600" + new string('x', Limit);
        var generator = Generating(text, out _);

        var cut = generator.GetFirstInterestedOffset(0);

        Assert.Equal(Limit - 1, cut);
        Assert.False(char.IsHighSurrogate(text[cut - 1]));
    }

    [Fact]
    public void Asked_from_inside_the_hidden_part_it_hides_from_there()
    {
        var generator = Generating(Long + Long, out _);

        Assert.Equal(Limit + 3, generator.GetFirstInterestedOffset(Limit + 3));
    }

    [Fact]
    public void Nothing_is_hidden_twice_once_the_end_of_the_line_is_reached()
    {
        var generator = Generating($"{Long}\nb", out var document);
        var line = document.GetLineByNumber(1);

        Assert.Equal(-1, generator.GetFirstInterestedOffset(line.EndOffset));
        Assert.Null(generator.ConstructElement(line.EndOffset));
    }

    [Fact]
    public void Rethemeing_reports_a_change_only_when_the_palette_moved()
    {
        var generator = new LongLineElementGenerator(Limit, dark: true);

        Assert.False(generator.Retheme(dark: true));
        Assert.True(generator.Retheme(dark: false));
    }

    private static LongLineElementGenerator Generating(string text, out TextDocument document)
    {
        document = new TextDocument(text);

        var generator = new LongLineElementGenerator(Limit, dark: true);
        generator.StartGeneration(new Context(document));

        return generator;
    }

    /// <summary>
    /// Just enough of a view for a generator to be asked questions.
    /// </summary>
    /// <remarks>
    /// The generator reads only the document, so the view-shaped members are left null: a
    /// generator that started reaching for them would fail here with a null reference rather
    /// than pass against a view nobody built.
    /// </remarks>
    private sealed class Context(TextDocument document) : ITextRunConstructionContext
    {
        public TextDocument Document { get; } = document;

        public TextView TextView => null!;

        public VisualLine VisualLine => null!;

        public TextRunProperties GlobalTextRunProperties => null!;

        public StringSegment GetText(int offset, int length) => new(Document.GetText(offset, length));
    }
}
