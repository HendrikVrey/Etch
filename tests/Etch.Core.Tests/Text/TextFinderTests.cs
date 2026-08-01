using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

/// <summary>
/// Find and replace.
/// </summary>
/// <remarks>
/// This is the component whose failures are silent and destructive — a replace-all
/// that drifts by a character corrupts a file and says nothing — so the awkward cases
/// are asserted here rather than discovered by clicking: patterns that match the empty
/// string, replacements containing the pattern, regex metacharacters in a literal
/// search, and wrapping at both ends.
/// </remarks>
public class TextFinderTests
{
    private static TextFinder Compile(string pattern, SearchOptions options = default)
    {
        var compilation = TextFinder.Compile(pattern, options);

        Assert.Null(compilation.Error);
        Assert.NotNull(compilation.Finder);

        return compilation.Finder!;
    }

    [Fact]
    public void An_empty_pattern_compiles_to_nothing_rather_than_an_error()
    {
        // The state the box is in before anyone has typed. Neither a match nor a
        // mistake.
        var compilation = TextFinder.Compile(string.Empty, default);

        Assert.Null(compilation.Finder);
        Assert.Null(compilation.Error);
    }

    [Fact]
    public void A_literal_search_treats_regex_metacharacters_as_text()
    {
        var search = Compile("a.c");

        Assert.Equal([new SearchMatch(4, 3)], search.FindAll("abc a.c"));
    }

    [Fact]
    public void A_regex_search_honours_metacharacters()
    {
        var search = Compile("a.c", new SearchOptions(UseRegex: true));

        Assert.Equal([new SearchMatch(0, 3), new SearchMatch(4, 3)], search.FindAll("abc a.c"));
    }

    [Fact]
    public void Case_sensitivity_is_off_by_default_and_respected_when_asked_for()
    {
        Assert.Equal(2, Compile("etch").FindAll("Etch etch").Count);
        Assert.Single(Compile("etch", new SearchOptions(MatchCase: true)).FindAll("Etch etch"));
    }

    [Fact]
    public void Whole_word_matching_requires_word_boundaries()
    {
        var search = Compile("cat", new SearchOptions(WholeWord: true));

        Assert.Equal([new SearchMatch(0, 3)], search.FindAll("cat concatenate"));
    }

    [Fact]
    public void Whole_word_wraps_an_alternation_so_every_branch_is_bounded()
    {
        // Without the non-capturing group the boundaries would bind only to the first
        // and last branches, and "concatenate" would match on "cat".
        var search = Compile("cat|dog", new SearchOptions(WholeWord: true, UseRegex: true));

        Assert.Equal([new SearchMatch(12, 3)], search.FindAll("concatenate dog"));
    }

    [Fact]
    public void A_pattern_that_matches_the_empty_string_still_terminates()
    {
        // The classic hang: advancing by the match length advances by zero, forever.
        var search = Compile("a*", new SearchOptions(UseRegex: true));

        var matches = search.FindAll("bb");

        Assert.Equal(3, matches.Count);
        Assert.All(matches, match => Assert.Equal(0, match.Length));
    }

    [Fact]
    public void Find_next_advances_and_then_wraps()
    {
        var search = Compile("x");
        const string Text = "x_x_x";

        Assert.Equal(new SearchMatch(2, 1), search.FindNext(Text, 1));
        Assert.Equal(new SearchMatch(4, 1), search.FindNext(Text, 3));

        // Past the last match, so it comes back to the top rather than reporting
        // nothing — repeated Enter has to keep cycling.
        Assert.Equal(new SearchMatch(0, 1), search.FindNext(Text, 5));
    }

    [Fact]
    public void Find_previous_moves_backwards_and_then_wraps()
    {
        var search = Compile("x");
        const string Text = "x_x_x";

        Assert.Equal(new SearchMatch(0, 1), search.FindPrevious(Text, 2));
        Assert.Equal(new SearchMatch(2, 1), search.FindPrevious(Text, 4));
        Assert.Equal(new SearchMatch(4, 1), search.FindPrevious(Text, 0));
    }

    [Fact]
    public void Searching_for_something_absent_reports_nothing_rather_than_looping()
    {
        var search = Compile("zzz");

        Assert.Null(search.FindNext("abc", 0));
        Assert.Null(search.FindPrevious("abc", 3));
        Assert.Empty(search.FindAll("abc"));
    }

    [Fact]
    public void An_offset_outside_the_buffer_is_clamped_rather_than_throwing()
    {
        var search = Compile("a");

        Assert.Equal(new SearchMatch(0, 1), search.FindNext("abc", 9_999));
        Assert.Equal(new SearchMatch(0, 1), search.FindPrevious("abc", -9_999));
    }

    [Fact]
    public void A_literal_replacement_is_used_verbatim()
    {
        // Someone replacing text with "$1" has not opted into substitution syntax and
        // would be astonished to find it applied.
        var search = Compile("b");
        var match = Assert.Single(search.FindAll("abc"));

        Assert.Equal("$1", search.Expand("abc", match, "$1"));
    }

    [Fact]
    public void A_regex_replacement_expands_capture_groups()
    {
        var search = Compile(@"(\w+)@(\w+)", new SearchOptions(UseRegex: true));
        var match = Assert.Single(search.FindAll("user@example"));

        Assert.Equal("example/user", search.Expand("user@example", match, "$2/$1"));
    }

    [Fact]
    public void A_replacement_containing_the_pattern_does_not_match_itself()
    {
        // Matches are found once, against the original text. A naive implementation that
        // re-scanned after each edit would loop forever on this.
        var search = Compile("a");

        var matches = search.FindAll("aaa");

        Assert.Equal(3, matches.Count);
        Assert.All(matches, match => Assert.Equal("aa", search.Expand("aaa", match, "aa")));
    }

    [Fact]
    public void Matches_come_back_in_ascending_order_so_a_reverse_replace_is_safe()
    {
        // Replace-all applies matches back to front precisely because replacing forwards
        // shifts every offset after the edit. That is only sound if the order is
        // guaranteed here.
        var matches = Compile("ab").FindAll("ab_ab_ab");

        Assert.Equal([new SearchMatch(0, 2), new SearchMatch(3, 2), new SearchMatch(6, 2)], matches);
    }

    [Fact]
    public void An_invalid_regex_is_reported_rather_than_thrown()
    {
        // Patterns arrive one keystroke at a time, so most intermediate ones are
        // syntactically invalid by definition.
        var compilation = TextFinder.Compile("(unclosed", new SearchOptions(UseRegex: true));

        Assert.Null(compilation.Finder);
        Assert.NotNull(compilation.Error);
    }

    [Fact]
    public void Multiline_anchors_match_per_line()
    {
        var search = Compile("^b", new SearchOptions(UseRegex: true));

        Assert.Equal([new SearchMatch(2, 1)], search.FindAll("a\nb\nc"));
    }
}
