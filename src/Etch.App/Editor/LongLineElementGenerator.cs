using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Etch.App.Editor;

/// <summary>
/// Lays out the start of a very long line and stands one short label in for the rest.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit turns each document line into one visual line and formats every character of
/// it, however little of it is on screen, and does so again whenever the line changes. A
/// minified JSON response is one line, so pasting a 2 MB one froze Etch for ten seconds and
/// cost two more per keystroke on that line, and a 20 MB one never came back. The size
/// tiers could not help: they are about how much text there is, and this is about how it
/// is shaped.
/// </para>
/// <para>
/// So past <see cref="Limit"/> characters a line ends in one element that covers the rest
/// of it and draws as "… 20,961,574 more characters". It is the same mechanism a collapsed
/// fold uses, which is why the caret, the selection and the mouse already know how to step
/// over it, but unlike a fold it cannot be opened: opening it is the freeze. The document is
/// not touched. Find, copy, save and every transform still see the whole line, and
/// formatting a minified document with <c>Ctrl+Enter</c> is the way to read it.
/// </para>
/// <para>
/// Cheap for every ordinary line: it asks for the line under the offset, which is a tree
/// lookup, and answers "no interest" as soon as that line is short.
/// </para>
/// </remarks>
internal sealed class LongLineElementGenerator : VisualLineElementGenerator
{
    private Brush? _brush;
    private bool _dark;

    /// <param name="limit">How many characters of a line are laid out.</param>
    /// <param name="dark">Whether the dark palette is in use, for the label's colour.</param>
    internal LongLineElementGenerator(int limit, bool dark)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        Limit = limit;
        _dark = dark;
    }

    /// <summary>How many characters of a line are laid out.</summary>
    internal int Limit { get; }

    /// <summary>Switches the label's colour to the other palette.</summary>
    /// <returns>True when the colour changed and visual lines need rebuilding.</returns>
    internal bool Retheme(bool dark)
    {
        if (_dark == dark)
        {
            return false;
        }

        _dark = dark;
        _brush = null;

        return true;
    }

    /// <inheritdoc />
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var document = CurrentContext.Document;
        var line = document.GetLineByOffset(startOffset);

        if (line.Length <= Limit || startOffset >= line.EndOffset)
        {
            return -1;
        }

        var cut = CutOffset(document, line);

        // Past the cut already means something else drew an element that ended inside the
        // hidden part. Hiding from here is still right; laying the rest out is the freeze.
        return Math.Max(startOffset, cut);
    }

    /// <inheritdoc />
    public override VisualLineElement? ConstructElement(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);

        if (line.Length <= Limit || offset >= line.EndOffset)
        {
            return null;
        }

        var hidden = line.EndOffset - offset;
        var label = string.Create(CultureInfo.CurrentCulture, $" … {hidden:N0} more characters");

        return new HiddenTail(label, hidden, Brush());
    }

    /// <summary>
    /// Where the laid-out part of <paramref name="line"/> ends.
    /// </summary>
    /// <remarks>
    /// Never between the two halves of a surrogate pair: the visible part would end in half
    /// a character and the renderer would draw a replacement box there.
    /// </remarks>
    private int CutOffset(TextDocument document, DocumentLine line)
    {
        var cut = line.Offset + Limit;

        return char.IsHighSurrogate(document.GetCharAt(cut - 1)) ? cut - 1 : cut;
    }

    /// <summary>The comment colour, which is already held to the palette's contrast floor.</summary>
    private Brush Brush()
    {
        if (_brush is { } brush)
        {
            return brush;
        }

        // ForRole answers null only for an unmapped role, and Comment is mapped; the fallback
        // exists so that a palette edit cannot turn this into a crash, and it is still held to
        // the same floor.
        var colour = SyntaxPalette.ForRole(SyntaxRole.Comment, _dark) ?? SyntaxPalette.Rescue(Colors.Gray, _dark);
        var created = new SolidColorBrush(colour);
        created.Freeze();

        return _brush = created;
    }

    /// <summary>The label that stands in for the part of a line that is not laid out.</summary>
    /// <remarks>
    /// A <see cref="FormattedTextElement"/> so that it is one caret stop wide, which is what
    /// lets the caret, a selection and a click treat the hidden part as a single thing. The
    /// colour is applied as the run is created because the element's text properties are
    /// only given to it after the generator has returned it.
    /// </remarks>
    private sealed class HiddenTail : FormattedTextElement
    {
        private readonly Brush _brush;

        internal HiddenTail(string label, int documentLength, Brush brush)
            : base(label, documentLength)
        {
            _brush = brush;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetForegroundBrush(_brush);

            return base.CreateTextRun(startVisualColumn, context);
        }
    }
}
