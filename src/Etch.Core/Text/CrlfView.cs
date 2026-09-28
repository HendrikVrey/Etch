namespace Etch.Core.Text;

/// <summary>
/// A view of a buffer with <c>\r\n</c> collapsed to <c>\n</c>, and a way back.
/// </summary>
/// <remarks>
/// <para>
/// Exists for one reason: in .NET, <c>.</c> matches every character except <c>\n</c>,
/// so on a CRLF buffer it matches the carriage return. <c>^(.*)$</c>, the most
/// ordinary line-oriented pattern there is, therefore captures a trailing <c>\r</c>,
/// and a replacement built from <c>$1</c> writes the line back without it. A
/// find-and-replace that silently converts a file's line endings is not a
/// find-and-replace.
/// </para>
/// <para>
/// The alternative was rewriting the user's pattern to make <c>$</c> tolerate
/// <c>\r</c>, which needs a parser that can tell an anchor from a literal <c>$</c>
/// from a <c>$</c> inside a character class. Getting that wrong corrupts a search in a
/// way that looks like the engine misbehaving. Normalising the *text* needs no parser
/// and cannot misread a pattern.
/// </para>
/// <para>
/// <b>Only <c>\r\n</c> pairs are collapsed.</b> A lone <c>\r</c> is left exactly where
/// it is: .NET's regular expressions do not treat it as a line terminator either, so
/// removing it would change what the pattern means rather than preserve it.
/// </para>
/// <para>
/// Nothing is copied when the buffer has no <c>\r\n</c> in it, which is every buffer on
/// a Unix-style file and every scratch tab Etch creates itself.
/// </para>
/// </remarks>
public sealed class CrlfView
{
    private readonly string _text;

    /// <summary>
    /// Normalised offsets at which a carriage return was removed.
    /// </summary>
    /// <remarks>
    /// Each entry is the index, <i>in the normalised text</i>, of a <c>\n</c> whose
    /// <c>\r</c> was dropped. Ascending by construction, so it is binary-searched rather
    /// than walked: a ten-megabyte log has a few hundred thousand of these and the
    /// mapping runs once per match.
    /// </remarks>
    private readonly int[] _removed;

    private CrlfView(string text, int[] removed)
    {
        _text = text;
        _removed = removed;
    }

    /// <summary>The text to search: the original, or a copy with <c>\r\n</c> collapsed.</summary>
    public string Text => _text;

    /// <summary>True when the view differs from the buffer it was made from.</summary>
    public bool IsNormalised => _removed.Length > 0;

    /// <summary>
    /// A view that changes nothing, for searches that must see the buffer as it is.
    /// </summary>
    /// <remarks>
    /// Cheaper than <see cref="Create"/> and, more importantly, unconditional: a literal
    /// search has to find a line ending when the user asks for one, so it must not be
    /// left to depend on whether the buffer happens to contain a CRLF pair.
    /// </remarks>
    public static CrlfView Identity(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new CrlfView(text, []);
    }

    /// <summary>Builds a view of <paramref name="text"/>.</summary>
    public static CrlfView Create(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var pairs = LineEndings.Count(text).Crlf;

        if (pairs == 0)
        {
            return new CrlfView(text, []);
        }

        var removed = new int[pairs];

        // string.Create writes straight into the final string. The obvious shape,
        // fill a char[] and then construct a string from it, holds a second full-size
        // copy live at the same time, and at the sizes this editor opens both land on
        // the large object heap. One 100 MB document should cost one 100 MB copy.
        var normalised = string.Create(
            text.Length - pairs,
            (text, removed),
            static (span, state) =>
            {
                var (source, removals) = state;

                var write = 0;
                var found = 0;

                for (var read = 0; read < source.Length; read++)
                {
                    if (source[read] == '\r' && read + 1 < source.Length && source[read + 1] == '\n')
                    {
                        // Record where the surviving \n lands, then skip the \r.
                        removals[found++] = write;
                        continue;
                    }

                    span[write++] = source[read];
                }
            });

        return new CrlfView(normalised, removed);
    }

    /// <summary>
    /// Maps a match found in <see cref="Text"/> back onto the original buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule, applied to both ends: an offset moves right by the number of carriage
    /// returns removed <i>strictly before</i> it. Counting one removed at exactly the
    /// offset would be wrong at both ends and for different reasons, which is why the
    /// rule is stated once rather than split.
    /// </para>
    /// <para>
    /// At the end of a match it is the bug this type exists to prevent: <c>^(.*)$</c>
    /// stops just before the terminator, and counting the removal there would stretch
    /// the match back over the <c>\r</c>. At a zero-width <c>$</c> it would put the
    /// caret between the <c>\r</c> and the <c>\n</c>: inside a line terminator, which
    /// is not a position in the document as far as the user is concerned.
    /// </para>
    /// <para>
    /// A pattern that matches the <c>\n</c> itself still selects the whole <c>\r\n</c>,
    /// which falls out of the same rule rather than needing a second one: the start does
    /// not count the removal, so it lands on the <c>\r</c>, and the end does, so it
    /// lands past the <c>\n</c>.
    /// </para>
    /// </remarks>
    public SearchMatch ToOriginal(SearchMatch match)
    {
        if (_removed.Length == 0)
        {
            return match;
        }

        var start = ToOriginal(match.Offset);
        var end = ToOriginal(match.EndOffset);

        return new SearchMatch(start, end - start);
    }

    /// <summary>Maps a single offset in <see cref="Text"/> back onto the original buffer.</summary>
    public int ToOriginal(int normalisedOffset) =>
        _removed.Length == 0 ? normalisedOffset : normalisedOffset + CountBefore(normalisedOffset);

    /// <summary>Maps an offset in the original buffer onto <see cref="Text"/>.</summary>
    /// <remarks>
    /// Needed so a search can resume from the caret, which is a position in the buffer
    /// the user is looking at rather than in this view of it.
    /// </remarks>
    public int ToNormalised(int originalOffset)
    {
        if (_removed.Length == 0)
        {
            return originalOffset;
        }

        // Binary search over the answer rather than over the removal list: mapping
        // normalised to original is monotonic, so the largest normalised offset that
        // still lands at or before the target is the inverse. Landing *on* a removed
        // carriage return therefore resolves to the position of the \n that followed
        // it, which is the only offset in the view that terminator has.
        var low = 0;
        var high = _text.Length;

        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);

            if (ToOriginal(middle) <= originalOffset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }

    /// <summary>
    /// The first offset in <see cref="Text"/> that is at or after
    /// <paramref name="originalOffset"/> in the buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ToNormalised"/> is a floor, and a floor is the wrong answer for a
    /// search's resume point. Both halves of a removed <c>\r\n</c> map down to the same
    /// normalised offset, so advancing the caret by one character across a line ending
    /// does not advance it in the view at all: a caller that searches "from the caret,
    /// which I have just nudged past the last match" gets the same match back and never
    /// terminates.
    /// </para>
    /// <para>
    /// Expressed in terms of the floor rather than as a second binary search, so there
    /// is still exactly one piece of arithmetic to be right about.
    /// </para>
    /// </remarks>
    public int ToNormalisedAtOrAfter(int originalOffset)
    {
        var floor = ToNormalised(originalOffset);

        return ToOriginal(floor) >= originalOffset ? floor : Math.Min(floor + 1, _text.Length);
    }

    /// <summary>How many removals sit strictly before <paramref name="offset"/>.</summary>
    private int CountBefore(int offset)
    {
        var low = 0;
        var high = _removed.Length;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_removed[middle] < offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
