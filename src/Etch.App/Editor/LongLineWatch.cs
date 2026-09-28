using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Editor;

/// <summary>
/// Keeps an up-to-date answer to "does this document hold a line too long to lay out?".
/// </summary>
/// <remarks>
/// <para>
/// Maintained from the document's own change events rather than recomputed, because the
/// question is asked on every keystroke (that is when the size policy is consulted) and
/// walking a million lines per keystroke is exactly the kind of cost this exists to keep
/// off the keyboard. A line can only become long through an edit that lands on it, so an
/// edit checks the lines it touched. It can only stop being the last long line through an
/// edit that touched it too, so only such an edit, and only when nothing it left behind is
/// still long, pays for a walk of the whole document.
/// </para>
/// <para>
/// Line lengths come from the document's own line tree, so what counts as a line break is
/// AvalonEdit's answer (CR, LF or CRLF) and cannot disagree with what the view lays out.
/// </para>
/// <para>
/// Owned by the UI thread, like the document it watches. Nothing is unsubscribed: the watch
/// and its document refer to each other and nothing else, so they are collected together.
/// </para>
/// </remarks>
internal sealed class LongLineWatch
{
    private readonly TextDocument _document;
    private readonly int _limit;

    /// <summary>Set while a change is under way, when it lands on a line that is long now.</summary>
    private bool _changeTouchesLongLine;

    /// <param name="document">The document to watch.</param>
    /// <param name="limit">The longest line, in characters, that does not count as long.</param>
    internal LongLineWatch(TextDocument document, int limit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        _document = document;
        _limit = limit;

        Any = AnyInDocument();

        document.Changing += OnChanging;
        document.Changed += OnChanged;
    }

    /// <summary>Whether any line is longer than the limit.</summary>
    internal bool Any { get; private set; }

    /// <summary>
    /// Notes, before the text moves, whether the change is about to edit a long line.
    /// </summary>
    /// <remarks>
    /// The line under the start of the change is always included, even for a pure insertion.
    /// Typing a line break into the middle of the only long line splits it into two short
    /// ones, and without knowing it had been long the answer would stay true for ever.
    /// </remarks>
    private void OnChanging(object? sender, DocumentChangeEventArgs e) =>
        _changeTouchesLongLine = Any && AnyBetween(e.Offset, e.Offset + e.RemovalLength);

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        var touchedLongLine = _changeTouchesLongLine;
        _changeTouchesLongLine = false;

        // The common case by far: a small document, where no line can be long enough to
        // matter, so neither a walk nor a lookup is needed.
        if (!Any && _document.TextLength <= _limit)
        {
            return;
        }

        if (AnyBetween(e.Offset, e.Offset + e.InsertionLength))
        {
            Any = true;
            return;
        }

        if (touchedLongLine)
        {
            Any = AnyInDocument();
        }
    }

    /// <summary>Whether a line from the one holding <paramref name="start"/> to the one holding <paramref name="end"/> is long.</summary>
    /// <remarks>
    /// Stops at the first line that extends past <paramref name="end"/>. A change that ends
    /// just after a line break has edited the line that starts there too, which is why the
    /// test is on where a line's delimiter ends rather than where its text does.
    /// </remarks>
    private bool AnyBetween(int start, int end)
    {
        var line = _document.GetLineByOffset(Math.Clamp(start, 0, _document.TextLength));

        while (line is not null)
        {
            if (line.Length > _limit)
            {
                return true;
            }

            if (line.Offset + line.TotalLength > end)
            {
                return false;
            }

            line = line.NextLine;
        }

        return false;
    }

    private bool AnyInDocument()
    {
        if (_document.TextLength <= _limit)
        {
            return false;
        }

        for (var line = _document.GetLineByNumber(1); line is not null; line = line.NextLine)
        {
            if (line.Length > _limit)
            {
                return true;
            }
        }

        return false;
    }
}
