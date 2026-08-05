using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

/// <summary>
/// The CRLF behaviour of <see cref="TextFinder"/>, exercised through its public surface.
/// </summary>
/// <remarks>
/// <see cref="CrlfViewTests"/> pins the mapping arithmetic; these pin the thing the user
/// experiences, which is a different question. Every buffer here uses CRLF endings on
/// purpose — the rest of the suite is LF-only, which is why a whole class of defects in
/// this area was invisible to it.
/// </remarks>
public class TextFinderCrlfTests
{
    private const string Crlf = "alpha\r\nbeta\r\ngamma\r\n";

    private static TextFinder Compile(string pattern, SearchOptions options = default)
    {
        var compilation = TextFinder.Compile(pattern, options);

        Assert.Null(compilation.Error);
        Assert.NotNull(compilation.Finder);

        return compilation.Finder!;
    }

    private static TextFinder Regex(string pattern) => Compile(pattern, new SearchOptions(UseRegex: true));

    [Fact]
    public void A_line_pattern_does_not_capture_the_carriage_return()
    {
        // The defect the whole change exists for: $1 built from ^(.*)$ used to carry a
        // trailing \r, so replacing with it rewrote the file's line endings.
        var search = Regex("^(.*)$");

        foreach (var match in search.FindAll(Crlf))
        {
            Assert.DoesNotContain('\r', Crlf.Substring(match.Offset, match.Length));
        }
    }

    [Fact]
    public void Trimming_trailing_whitespace_leaves_the_line_ending_alone()
    {
        // \s+$ matched the spaces *and* the carriage return, so the one pattern everybody
        // uses to tidy a file also converted it to LF.
        //
        // The buffer ends in text rather than in a newline on purpose. \s matches \n, so
        // at the very end of a buffer \s+$ genuinely does consume the final terminator —
        // that is .NET being greedy where there is no following line, not the defect under
        // test, and a buffer ending in a blank line would be asserting the wrong thing.
        const string Text = "alpha   \r\nbeta\t\r\ngamma";

        var search = Regex(@"\s+$");
        var matches = search.FindAll(Text);

        Assert.Equal(2, matches.Count);

        foreach (var match in matches)
        {
            Assert.DoesNotContain('\r', Text.Substring(match.Offset, match.Length));
        }
    }

    [Fact]
    public void Matches_are_reported_in_the_buffers_own_coordinates()
    {
        var search = Regex("beta");
        var match = Assert.Single(search.FindAll(Crlf));

        Assert.Equal(Crlf.IndexOf("beta", StringComparison.Ordinal), match.Offset);
    }

    [Fact]
    public void Repeated_find_next_always_advances_across_a_line_ending()
    {
        // A zero-width match plus the caller's "resume one past the last match" nudge is
        // where a floor-valued offset map stops advancing: both halves of a removed \r\n
        // share one offset in the view, so the search kept returning the same match and
        // the Enter key silently stopped working.
        var search = Regex("$");

        var seen = new List<int>();
        var caret = 0;

        for (var press = 0; press < 4; press++)
        {
            var match = search.FindNext(Crlf, caret);

            Assert.NotNull(match);

            seen.Add(match.Value.Offset);
            caret = match.Value.Offset + Math.Max(match.Value.Length, 1);
        }

        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    [Fact]
    public void A_literal_search_can_still_find_a_line_ending()
    {
        // The justification for leaving literal searches over the raw buffer. Someone who
        // types a line break into the find box must find one.
        var search = Compile("\r\n");

        Assert.Equal(3, search.FindAll(Crlf).Count);
        Assert.False(search.SearchesNormalisedText);
    }

    [Theory]
    [InlineData(@"\r")]
    [InlineData(@"\cM")]
    [InlineData(@"\cm")]
    [InlineData(@"\x0D")]
    [InlineData(@"\x0d")]
    [InlineData(@"\u000D")]
    [InlineData(@"\015")]
    public void A_pattern_that_names_a_carriage_return_searches_the_raw_buffer(string pattern)
    {
        // Each of these is U+000D to .NET. Normalising the text under any one of them
        // would make the search find nothing at all, with no error to explain it.
        //
        // Only the octal spellings that begin with a zero appear here, and that is a fact
        // about the parser rather than a gap. Outside RegexOptions.ECMAScript, a backslash
        // followed by 1-9 is read as a backreference: .NET scans the whole decimal run and
        // throws "reference to undefined group number" when no such group exists. So \15
        // is not an octal carriage return in any pattern that compiles — it is either a
        // backreference or a parse error. NeedsCarriageReturns still treats it as a
        // carriage return, deliberately, because over-reporting costs only the
        // normalisation; there is simply no valid pattern with which to assert it.
        var search = Regex(pattern);

        Assert.False(search.SearchesNormalisedText);
        Assert.Equal(3, search.FindAll(Crlf).Count);
    }

    [Theory]
    [InlineData("^(.*)$")]
    [InlineData(@"\s+$")]
    [InlineData(@"\010")]
    [InlineData(@"(a)\1")]
    public void A_pattern_that_does_not_name_one_searches_the_normalised_view(string pattern)
    {
        // \010 is a backspace, not a carriage return — the octal escape is greedy, and a
        // leading zero is what makes it an octal escape at all. \1 is a backreference to a
        // group that exists, which the conservative octal test must not mistake for U+000D.
        Assert.True(Regex(pattern).SearchesNormalisedText);
    }

    [Fact]
    public void An_expansion_of_the_whole_match_keeps_the_line_ending_it_replaces()
    {
        // $& expanded from the normalised view is an LF-only copy of the match, and
        // splicing it over the original span converts that terminator — the original
        // defect, arriving through the replacement syntax instead of the search.
        var search = Regex(".*\n");
        var match = search.FindAll(Crlf)[0];

        Assert.Equal("alpha\r\n", Crlf.Substring(match.Offset, match.Length));
        Assert.Equal("alpha\r\n", search.Expand(Crlf, match, "$&"));
    }

    [Fact]
    public void An_expansion_of_the_whole_input_keeps_every_line_ending()
    {
        var search = Regex("beta");
        var match = Assert.Single(search.FindAll(Crlf));

        Assert.Equal(Crlf, search.Expand(Crlf, match, "$_"));
    }

    [Fact]
    public void Surrounding_text_expansions_keep_their_line_endings()
    {
        var search = Regex("beta");
        var match = Assert.Single(search.FindAll(Crlf));

        Assert.Equal("alpha\r\n", search.Expand(Crlf, match, "$`"));
        Assert.Equal("\r\ngamma\r\n", search.Expand(Crlf, match, "$'"));
    }

    [Fact]
    public void Numbered_and_named_groups_expand_from_the_original_buffer()
    {
        var search = Regex("(?<line>^.*)(?<break>\n)");
        var match = search.FindAll(Crlf)[0];

        Assert.Equal("alpha", search.Expand(Crlf, match, "$1"));
        Assert.Equal("alpha", search.Expand(Crlf, match, "${line}"));
        Assert.Equal("\r\n", search.Expand(Crlf, match, "${break}"));
        Assert.Equal("[alpha]", search.Expand(Crlf, match, "[$1]"));
    }

    [Fact]
    public void A_dollar_that_names_nothing_is_left_alone()
    {
        var search = Regex("(beta)");
        var match = Assert.Single(search.FindAll(Crlf));

        Assert.Equal("$$", search.Expand(Crlf, match, "$$$$"));
        Assert.Equal("$z", search.Expand(Crlf, match, "$z"));
        Assert.Equal("${nope}", search.Expand(Crlf, match, "${nope}"));

        // One group, so $12 is group 1 followed by a literal 2 — .NET reads digits
        // greedily and then backs off until the number names a group.
        Assert.Equal("beta2", search.Expand(Crlf, match, "$12"));
    }

    [Fact]
    public void An_lf_only_buffer_expands_exactly_as_dot_net_does()
    {
        // The fast path. Nothing about the custom expansion should be reachable here.
        const string Text = "alpha\nbeta\n";

        var search = Regex("(a)(l)");
        var match = search.FindAll(Text)[0];

        Assert.Equal("al|a|l|alpha\nbeta\n", search.Expand(Text, match, "$&|$1|$2|$_"));
    }
}
