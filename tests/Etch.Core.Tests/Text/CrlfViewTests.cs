using System.Text.RegularExpressions;
using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

/// <summary>
/// The offset mapping is the whole of this type, and it is the kind of arithmetic that
/// looks right and is off by one. These assert the property directly, normalising the
/// original substring a mapped match points at must give back the text that was matched,
/// rather than asserting particular numbers, because the numbers are not the contract.
/// </summary>
public class CrlfViewTests
{
    [Fact]
    public void A_buffer_with_no_carriage_returns_is_not_copied()
    {
        const string Text = "alpha\nbeta\ngamma";

        var view = CrlfView.Create(Text);

        Assert.False(view.IsNormalised);

        // Reference equality, not string equality: the common case is a Unix-ending file
        // or a scratch tab, and allocating a second copy of a ten-megabyte buffer to
        // arrive at the same characters would make the fix cost more than the bug.
        Assert.Same(Text, view.Text);
    }

    [Fact]
    public void A_lone_carriage_return_is_left_where_it_is()
    {
        // .NET's regular expressions do not treat a bare \r as a line terminator either,
        // so removing it would change what a pattern means rather than preserve it.
        var view = CrlfView.Create("alpha\rbeta");

        Assert.False(view.IsNormalised);
        Assert.Equal("alpha\rbeta", view.Text);
    }

    [Theory]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("\r\n", "\n")]
    [InlineData("a\r\r\nb", "a\r\nb")]
    [InlineData("a\n\r\nb", "a\n\nb")]
    [InlineData("trailing\r", "trailing\r")]
    public void Only_crlf_pairs_are_collapsed(string original, string expected)
    {
        Assert.Equal(expected, CrlfView.Create(original).Text);
    }

    [Fact]
    public void The_defect_this_exists_for_line_patterns_no_longer_capture_the_carriage_return()
    {
        // The bug, exactly as it was reported: ^(.*)$ over a CRLF buffer captured a
        // trailing \r, so a replace built from $1 rewrote the file's line endings.
        const string Text = "alpha\r\nbeta\r\ngamma\r\n";

        var view = CrlfView.Create(Text);
        var captured = new List<string>();

        foreach (Match match in Regex.Matches(view.Text, "^(.*)$", RegexOptions.Multiline))
        {
            var mapped = view.ToOriginal(new SearchMatch(match.Index, match.Length));
            captured.Add(Text.Substring(mapped.Offset, mapped.Length));
        }

        Assert.Equal(["alpha", "beta", "gamma", string.Empty], captured);
        Assert.DoesNotContain(captured, static line => line.Contains('\r', StringComparison.Ordinal));
    }

    [Fact]
    public void A_pattern_that_matches_the_newline_selects_the_whole_terminator()
    {
        // Falls out of the same single rule rather than needing a second one, and it is
        // the behaviour someone deleting line breaks is asking for.
        const string Text = "a\r\nb";

        var view = CrlfView.Create(Text);
        var match = Regex.Match(view.Text, "\n");
        var mapped = view.ToOriginal(new SearchMatch(match.Index, match.Length));

        Assert.Equal("\r\n", Text.Substring(mapped.Offset, mapped.Length));
    }

    [Fact]
    public void A_zero_width_end_of_line_lands_before_the_carriage_return()
    {
        // Not between the \r and the \n. That offset is inside a line terminator, which
        // is not a position in the document as far as the caret is concerned.
        var view = CrlfView.Create("a\r\nb");
        var match = Regex.Match(view.Text, "$", RegexOptions.Multiline);

        Assert.Equal(1, view.ToOriginal(match.Index));
    }

    [Fact]
    public void Mapping_an_offset_back_and_forth_returns_where_it_started()
    {
        var view = CrlfView.Create("one\r\ntwo\r\nthree");

        for (var offset = 0; offset <= view.Text.Length; offset++)
        {
            Assert.Equal(offset, view.ToNormalised(view.ToOriginal(offset)));
        }
    }

    /// <summary>
    /// Every arrangement of the four characters that matter, up to length five.
    /// </summary>
    /// <remarks>
    /// 1,365 buffers, and for each of them every possible match span. This is small
    /// enough to run exhaustively and large enough to contain every adjacency that has
    /// ever broken this kind of mapping: a pair at the very start, at the very end, two
    /// in a row, a lone carriage return beside a real pair, and a \r\r\n.
    /// </remarks>
    [Fact]
    public void Every_short_buffer_maps_every_match_back_correctly()
    {
        foreach (var original in Buffers(maxLength: 5))
        {
            var view = CrlfView.Create(original);

            Assert.Equal(original.Replace("\r\n", "\n", StringComparison.Ordinal), view.Text);

            for (var offset = 0; offset <= view.Text.Length; offset++)
            {
                for (var length = 0; length + offset <= view.Text.Length; length++)
                {
                    var mapped = view.ToOriginal(new SearchMatch(offset, length));

                    Assert.InRange(mapped.Offset, 0, original.Length);
                    Assert.InRange(mapped.EndOffset, mapped.Offset, original.Length);

                    var slice = original.Substring(mapped.Offset, mapped.Length);

                    Assert.Equal(
                        view.Text.Substring(offset, length),
                        slice.Replace("\r\n", "\n", StringComparison.Ordinal));
                }
            }
        }
    }

    private static IEnumerable<string> Buffers(int maxLength)
    {
        char[] alphabet = ['a', '\r', '\n', 'b'];

        var current = new List<string> { string.Empty };

        yield return string.Empty;

        for (var length = 1; length <= maxLength; length++)
        {
            var next = new List<string>(current.Count * alphabet.Length);

            foreach (var prefix in current)
            {
                foreach (var character in alphabet)
                {
                    var candidate = prefix + character;
                    next.Add(candidate);
                    yield return candidate;
                }
            }

            current = next;
        }
    }
}
