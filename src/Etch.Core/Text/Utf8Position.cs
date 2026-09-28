using System.Text;

namespace Etch.Core.Text;

/// <summary>
/// Turns a UTF-8 parser position into an offset into the UTF-16 buffer the user is
/// looking at.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the two are not the same number, and the difference is silent.</b>
/// <c>System.Text.Json</c> reports failures as a line number plus a
/// <c>BytePositionInLine</c>, and its own documentation is explicit that the second
/// "counts the number of bytes (i.e. UTF-8 code units) and not characters or scalars":
/// <see cref="System.Text.Json.JsonDocument.Parse(string, System.Text.Json.JsonDocumentOptions)"/>
/// transcodes to UTF-8 before parsing. Handing that byte count to an editor as a
/// character offset is correct for pure ASCII and wrong for every line containing an
/// accent, a CJK ideograph or an emoji, by one, two or three positions per character
/// before the error. A caret that lands near the problem on English input and drifts
/// further the more interesting the data gets is worse than no caret at all, because
/// nothing about it looks broken.
/// </para>
/// <para>
/// Line numbers need no such care: the reader counts <c>\n</c> and nothing else, a lone
/// <c>\r</c> is ordinary whitespace to it, and the <c>\r</c> of a <c>\r\n</c> is counted
/// as a byte on the line it ends. Verified against <c>JsonReaderHelper.CountNewLines</c>,
/// which searches for the line feed alone.
/// </para>
/// </remarks>
public static class Utf8Position
{
    /// <summary>
    /// Resolves a zero-based line and UTF-8 byte offset to a character offset in
    /// <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The buffer the parser was given.</param>
    /// <param name="lineNumber">Zero-based, counting <c>\n</c>.</param>
    /// <param name="bytePositionInLine">Zero-based UTF-8 byte offset from the start of that line.</param>
    /// <param name="offset">The character offset, when one could be resolved.</param>
    /// <returns>
    /// False when the position does not describe somewhere in <paramref name="text"/>:
    /// a negative input, or a line past the end. A caller that gets false should say what
    /// went wrong without claiming to know where, which is strictly better than pointing
    /// at the wrong place.
    /// </returns>
    /// <remarks>
    /// A byte position past the end of its line is <em>clamped</em> to the end of that
    /// line rather than rejected. That case is not an error but the most common failure
    /// there is: JSON truncated mid-write, where the parser stops at the end of the input
    /// and reports the position just past it.
    /// </remarks>
    public static bool TryResolve(string text, long lineNumber, long bytePositionInLine, out int offset)
    {
        ArgumentNullException.ThrowIfNull(text);

        offset = 0;

        if (lineNumber < 0 || bytePositionInLine < 0)
        {
            return false;
        }

        if (!TryFindLineStart(text, lineNumber, out var lineStart))
        {
            return false;
        }

        offset = AdvanceByBytes(text, lineStart, bytePositionInLine);
        return true;
    }

    /// <summary>Finds the character offset at which line <paramref name="lineNumber"/> begins.</summary>
    /// <remarks>
    /// The empty string is line 0 starting at offset 0, and a buffer ending in a newline
    /// has a final empty line that a parser can legitimately fail on, so a line start
    /// exactly equal to the length is valid, and only a line number beyond that is not.
    /// </remarks>
    private static bool TryFindLineStart(string text, long lineNumber, out int lineStart)
    {
        lineStart = 0;

        for (long line = 0; line < lineNumber; line++)
        {
            var next = text.IndexOf('\n', lineStart);

            if (next < 0)
            {
                // Fewer lines than the parser reported. Nothing sensible to point at.
                lineStart = 0;
                return false;
            }

            lineStart = next + 1;
        }

        return true;
    }

    /// <summary>
    /// Walks forward from <paramref name="start"/> until <paramref name="byteCount"/>
    /// UTF-8 bytes have been passed, stopping at the end of the line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runes rather than chars, because a surrogate pair is four UTF-8 bytes across two
    /// <see cref="char"/> values and stepping one <see cref="char"/> at a time would
    /// account for the pair twice.
    /// </para>
    /// <para>
    /// An unpaired surrogate, which a scratchpad buffer really can contain, is counted
    /// as the three bytes its replacement character would occupy and stepped over as one
    /// <see cref="char"/>. The alternative is refusing to resolve a position at all
    /// because of a stray character somewhere earlier on the line, and being off by a
    /// byte near genuinely broken text is the better failure. This branch is defensive
    /// rather than live: <c>JsonDocument.Parse(string)</c> rejects a lone surrogate while
    /// transcoding, before the parser has a position to report, so no JSON failure can
    /// reach it.
    /// </para>
    /// <para>
    /// A byte count that stops <em>inside</em> a multi-byte sequence steps over the whole
    /// character, so the offset lands just past it rather than inside it. There is no
    /// position between two halves of a character to return, and no caller asks for one:
    /// a UTF-8 parser reports boundaries.
    /// </para>
    /// </remarks>
    private static int AdvanceByBytes(string text, int start, long byteCount)
    {
        var index = start;
        long consumed = 0;

        while (consumed < byteCount && index < text.Length)
        {
            var character = text[index];

            if (character == '\n')
            {
                // The parser's byte position is scoped to one line; anything past its end
                // means the position was already clamped by the caller's own truncation.
                break;
            }

            if (Rune.TryCreate(character, out var rune))
            {
                consumed += rune.Utf8SequenceLength;
                index++;

                continue;
            }

            if (index + 1 < text.Length && Rune.TryCreate(character, text[index + 1], out var pair))
            {
                consumed += pair.Utf8SequenceLength;
                index += 2;

                continue;
            }

            // Unpaired surrogate. See the remarks.
            consumed += 3;
            index++;
        }

        return index;
    }
}
