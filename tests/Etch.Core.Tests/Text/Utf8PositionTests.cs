using System.Text;
using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

/// <summary>
/// <see cref="Utf8Position"/>, which exists because a UTF-8 byte offset and a UTF-16
/// character offset are the same number only for ASCII.
/// </summary>
public class Utf8PositionTests
{
    [Fact]
    public void An_ascii_position_is_the_byte_position()
    {
        // The case that would pass with no conversion at all, which is exactly why the
        // bug it guards against is so easy to ship.
        Assert.True(Utf8Position.TryResolve("abcdef", 0, 3, out var offset));
        Assert.Equal(3, offset);
    }

    [Fact]
    public void The_start_of_the_buffer_resolves_to_zero()
    {
        Assert.True(Utf8Position.TryResolve("abc", 0, 0, out var offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void An_empty_buffer_has_a_line_zero()
    {
        Assert.True(Utf8Position.TryResolve(string.Empty, 0, 0, out var offset));
        Assert.Equal(0, offset);
    }

    [Theory]
    // Two bytes per character in UTF-8, one char in UTF-16.
    [InlineData("café", 5, 4)]
    // Three bytes per ideograph.
    [InlineData("日本語", 9, 3)]
    // Four bytes per emoji, and two chars — a surrogate pair.
    [InlineData("🎉", 4, 2)]
    [InlineData("a🎉b", 6, 4)]
    public void A_non_ascii_line_maps_bytes_onto_characters(string line, long bytePosition, int expected)
    {
        // Every one of these would land in the wrong place if the byte count were used
        // as a character offset, and it would look plausible while doing so.
        Assert.Equal(bytePosition, Encoding.UTF8.GetByteCount(line));

        Assert.True(Utf8Position.TryResolve(line, 0, bytePosition, out var offset));
        Assert.Equal(expected, offset);
    }

    [Fact]
    public void A_position_inside_a_line_after_non_ascii_text_is_correct()
    {
        // The realistic shape: a JSON value with an accent in it, and the error a little
        // way past it. "café": is 4 chars for café plus the quotes and colon.
        const string Line = "\"café\": tru";

        // 12 UTF-8 bytes for 11 chars — the é is two.
        Assert.Equal(12, Encoding.UTF8.GetByteCount(Line));

        Assert.True(Utf8Position.TryResolve(Line, 0, 12, out var offset));
        Assert.Equal(11, offset);
    }

    [Fact]
    public void Lines_are_counted_by_line_feed()
    {
        const string Text = "one\ntwo\nthree";

        Assert.True(Utf8Position.TryResolve(Text, 1, 0, out var second));
        Assert.Equal(4, second);

        Assert.True(Utf8Position.TryResolve(Text, 2, 2, out var third));
        Assert.Equal(10, third);
    }

    [Fact]
    public void A_carriage_return_counts_as_a_byte_and_not_as_a_line()
    {
        // System.Text.Json counts \n alone; the \r of a \r\n is a byte on the line it
        // ends. Verified against JsonReaderHelper.CountNewLines, which searches for the
        // line feed by itself.
        const string Text = "one\r\ntwo";

        Assert.True(Utf8Position.TryResolve(Text, 1, 1, out var offset));
        Assert.Equal(6, offset);
    }

    [Fact]
    public void A_lone_carriage_return_does_not_start_a_line()
    {
        // JSON treats it as ordinary whitespace. A helper that split on it would put
        // every subsequent position one line out.
        Assert.False(Utf8Position.TryResolve("one\rtwo", 1, 0, out _));
    }

    [Fact]
    public void A_line_past_the_end_cannot_be_resolved()
    {
        // Better to say nothing than to point somewhere arbitrary.
        Assert.False(Utf8Position.TryResolve("one\ntwo", 5, 0, out var offset));
        Assert.Equal(0, offset);
    }

    [Fact]
    public void A_buffer_ending_in_a_newline_has_a_final_empty_line()
    {
        // Where a truncated document fails: the parser runs out of input at the start of
        // a line that has nothing on it yet.
        Assert.True(Utf8Position.TryResolve("one\n", 1, 0, out var offset));
        Assert.Equal(4, offset);
    }

    [Fact]
    public void A_byte_position_past_the_end_of_its_line_stops_at_the_line_end()
    {
        // The most common failure of all — JSON cut off mid-write, where the parser stops
        // at the end of the input and reports the position just past it. Clamped rather
        // than refused, because there is a right answer here and it is "the end".
        Assert.True(Utf8Position.TryResolve("ab\ncd", 0, 99, out var offset));
        Assert.Equal(2, offset);

        Assert.True(Utf8Position.TryResolve("ab", 0, 99, out var end));
        Assert.Equal(2, end);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, -1)]
    public void A_negative_position_is_refused(long line, long bytePosition)
    {
        Assert.False(Utf8Position.TryResolve("abc", line, bytePosition, out _));
    }

    [Fact]
    public void An_unpaired_surrogate_is_counted_as_three_bytes_and_one_char()
    {
        // A scratchpad buffer really can hold one — pasted out of a hex viewer or a
        // truncated log — and refusing to resolve any position on the line because of a
        // stray character earlier in it would be the worse failure.
        //
        // The exact answer, not merely a plausible one: 'a' is one byte, the lone
        // surrogate is charged the three bytes its replacement character would occupy, so
        // four bytes in is two chars in. Asserting only "somewhere in the string" would
        // pass for every possible return value, including "gave up at the start" — which
        // is precisely the failure this test is named after.
        var text = "a" + '\uD800' + "bc";

        Assert.True(Utf8Position.TryResolve(text, 0, 4, out var offset));
        Assert.Equal(2, offset);
    }

    [Fact]
    public void The_result_is_always_a_valid_index_into_the_text()
    {
        // The contract the caller depends on: this offset is handed to an editor, and
        // AvalonEdit throws on one past the end of the document.
        string[] texts = ["", "a", "ab\ncd", "café\n🎉", "\n\n\n", "a\r\nb"];

        foreach (var text in texts)
        {
            for (long line = 0; line < 4; line++)
            {
                for (long position = 0; position < 8; position++)
                {
                    if (Utf8Position.TryResolve(text, line, position, out var offset))
                    {
                        Assert.InRange(offset, 0, text.Length);
                    }
                }
            }
        }
    }
}
