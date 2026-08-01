using Etch.Core.Abstractions;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Transforms;

/// <summary>
/// The case, line and whitespace transforms — the M2 slice-2 text block.
/// </summary>
/// <remarks>
/// Most of these are small enough that the interesting assertions are not "does it work"
/// but the edge cases every one of them shares: a trailing newline is not a line, a CRLF
/// buffer must come back as CRLF, and a transform that changed nothing has to say so rather
/// than claim it did something.
/// </remarks>
public class TextTransformTests
{
    private static TransformResult Run(string id, string text, TransformOptions? options = null)
    {
        var transform = TransformRegistry.Find(id);

        Assert.NotNull(transform);

        return transform!.Apply(new TransformInput(text, WasSelection: false, options ?? TransformOptions.Default));
    }

    [Theory]
    [InlineData("case.camel", "userAccountId")]
    [InlineData("case.pascal", "UserAccountId")]
    [InlineData("case.snake", "user_account_id")]
    [InlineData("case.kebab", "user-account-id")]
    [InlineData("case.constant", "USER_ACCOUNT_ID")]
    public void Every_identifier_conversion_reads_the_same_three_words(string id, string expected)
    {
        // One splitter behind all five, so they cannot disagree about where the words are.
        // Each of these four inputs divides into user / account / id and no other way.
        //
        // Title Case is deliberately absent: it does not go through the splitter, because a
        // splitter that drops separators is right for identifiers and destroys prose.
        foreach (var input in new[] { "user_account_id", "userAccountId", "user-account-id", "USER ACCOUNT ID" })
        {
            var result = Run(id, input);

            Assert.True(result.Success, $"{id} failed on {input}");
            Assert.Equal(expected, result.Text);
        }
    }

    [Theory]
    [InlineData("case.snake", "parseHTTPResponse", "parse_http_response")]
    [InlineData("case.snake", "utf8Encoder", "utf8_encoder")]
    [InlineData("case.pascal", "parse_http_response", "ParseHttpResponse")]
    [InlineData("case.camel", "HTTPResponse", "httpResponse")]
    public void An_acronym_is_one_word_and_a_digit_does_not_start_one(string id, string input, string expected)
    {
        // The last capital of a run belongs to the word after it — without that rule
        // HTTPResponse is a single word and comes back as "httpresponse". Digits attach to
        // the word in progress, or every version-numbered identifier grows a word.
        Assert.Equal(expected, Run(id, input).Text);
    }

    [Fact]
    public void Case_conversion_works_line_by_line()
    {
        // A column of identifiers is the case this has to get right; running the lines
        // together would be useless. A line with nothing alphanumeric survives untouched so
        // the shape of the document does not collapse.
        var result = Run("case.snake", "firstName\nlastName\n---\n");

        Assert.True(result.Success);
        Assert.Equal("first_name\nlast_name\n---\n", result.Text);
    }

    [Theory]
    // The one that made this transform stop using WordSplitter. Through the splitter every
    // separator is dropped and this reads "Don T Stop It S Fine" — the apostrophes and the
    // full stops simply gone from the user's buffer.
    [InlineData("don't stop. it's fine!", "Don't Stop. It's Fine!")]
    [InlineData("SHOUTED TEXT", "Shouted Text")]
    [InlineData("the well-known problem", "The Well-Known Problem")]
    [InlineData("mp3 player, 2 of them", "Mp3 Player, 2 Of Them")]
    // A camel hump is not a word boundary here, and that is the documented cost of not
    // going through the splitter. The five identifier conversions are what that is for.
    [InlineData("userAccountId", "Useraccountid")]
    public void Title_case_changes_capitals_and_nothing_else(string input, string expected)
    {
        var result = Run("case.title", input);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Text);

        // The real invariant: same characters, same order, only their case differs. Nothing
        // this transform does may delete or move anything.
        Assert.Equal(input.ToLowerInvariant(), result.Text!.ToLowerInvariant());
    }

    [Fact]
    public void Case_conversion_keeps_characters_outside_the_basic_plane()
    {
        // char.IsLetterOrDigit answers for one UTF-16 code unit, so it says false for both
        // halves of every surrogate pair and for every combining mark — and a splitter built
        // on it silently deletes them, because whatever it calls a separator it drops. A
        // letter vanishing out of a buffer as a side effect of "snake_case" is exactly the
        // kind of quiet damage this tool must not do.
        //
        // U+20000 is a CJK Extension B ideograph: a letter, and two UTF-16 code units.
        Assert.Equal("\U00020000_test", Run("case.snake", "\U00020000 Test").Text);

        // NFD — c, a, f, e, U+0301 COMBINING ACUTE ACCENT — which is the normal form for
        // anything that has been near macOS. The mark belongs to the e before it rather than
        // standing between two words, so the accent survives and stays attached to its
        // letter instead of being read as a separator and dropped.
        Assert.Equal("cafe\u0301_bar", Run("case.snake", "cafe\u0301 bar").Text);
    }

    [Fact]
    public void Reversing_lines_keeps_the_trailing_newline_where_it_was()
    {
        var result = Run("text.reverseLines", "one\ntwo\nthree\n");

        Assert.True(result.Success);
        Assert.Equal("three\ntwo\none\n", result.Text);
    }

    [Fact]
    public void Dedupe_keeps_the_first_of_each_and_leaves_the_order_alone()
    {
        // What separates this from "sort then dedupe": a list of errors deduplicated in
        // place still reads in the order the errors happened.
        var result = Run("text.dedupeLines", "b\na\nb\nc\na\n");

        Assert.True(result.Success);
        Assert.Equal("b\na\nc\n", result.Text);
        Assert.Contains("Removed 2 lines", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dedupe_says_so_rather_than_rewriting_a_buffer_it_did_not_change()
    {
        // A line transform that produced the same lines it was given reports instead of
        // returning text. Writing a byte-identical copy back would cost an undo step and the
        // caret position to change nothing at all, and the message is then the only way to
        // tell a transform that ran from one that did not.
        var result = Run("text.dedupeLines", "a\nb\n");

        Assert.True(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("already unique", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_of_spaces_counts_as_blank()
    {
        // Blank to the person looking at the screen is what blank means here.
        var result = Run("text.removeBlankLines", "a\n   \n\nb\n");

        Assert.True(result.Success);
        Assert.Equal("a\nb\n", result.Text);
    }

    [Fact]
    public void Join_and_split_round_trip_through_the_comma()
    {
        var joined = Run("text.joinCommas", "alpha\nbeta\ngamma\n");

        Assert.True(joined.Success);
        Assert.Equal("alpha, beta, gamma\n", joined.Text);

        // Split has to be tolerant of the space join emits, or the pair does not compose.
        var split = Run("text.splitCommas", joined.Text!);

        Assert.True(split.Success);
        Assert.Equal("alpha\nbeta\ngamma\n", split.Text);
    }

    [Fact]
    public void Splitting_keeps_empty_values()
    {
        // a,,b is three values. Dropping the middle one loses the fact that it was there,
        // which matters to anybody pasting a row of columns.
        var result = Run("text.splitCommas", "a,,b");

        Assert.True(result.Success);
        Assert.Equal("a\n\nb", result.Text);
    }

    [Fact]
    public void Trimming_reports_when_there_was_nothing_to_trim()
    {
        var nothing = Run("text.trimTrailing", "clean\nlines\n");

        Assert.Null(nothing.Text);
        Assert.Contains("no trailing whitespace", nothing.Message, StringComparison.Ordinal);

        Assert.Equal("a\nb\n", Run("text.trimTrailing", "a   \nb\t\n").Text);
    }

    [Fact]
    public void Collapsing_whitespace_stays_inside_each_line()
    {
        // Collapsing across lines is "join lines", a different transform with a different
        // name. Doing it here quietly would make this one unusable on anything structured.
        var result = Run("text.collapseWhitespace", "  one   two  \nthree\tfour\n");

        Assert.True(result.Success);
        Assert.Equal("one two\nthree four\n", result.Text);
    }

    [Fact]
    public void Tabs_expand_to_the_next_tab_stop_not_to_a_fixed_width()
    {
        // Two spaces then a tab, at indent size 4: the tab fills to column 4, so it
        // contributes two spaces rather than four. A fixed substitution would move the text.
        var result = Run("text.tabsToSpaces", "  \tvalue\n", new TransformOptions(IndentSize: 4));

        Assert.True(result.Success);
        Assert.Equal("    value\n", result.Text);
    }

    [Fact]
    public void Only_leading_whitespace_is_converted_in_either_direction()
    {
        // A tab in the middle of a line is a column separator — pasted TSV, an aligned
        // table — and rewriting it destroys the alignment it existed to create.
        var tabbed = Run("text.tabsToSpaces", "\tname\tvalue\n", new TransformOptions(IndentSize: 2));

        Assert.Equal("  name\tvalue\n", tabbed.Text);

        var spaced = Run("text.spacesToTabs", "    name  value\n", new TransformOptions(IndentSize: 2));

        Assert.Equal("\t\tname  value\n", spaced.Text);
    }

    [Fact]
    public void Spaces_to_tabs_leaves_a_partial_indent_as_spaces()
    {
        // Whole indents only. Three spaces at indent size 2 is one tab and one space, and
        // that last space is what keeps a wrapped continuation line aligned.
        var result = Run("text.spacesToTabs", "   value\n", new TransformOptions(IndentSize: 2));

        Assert.True(result.Success);
        Assert.Equal("\t value\n", result.Text);
    }

    [Fact]
    public void Indent_follows_the_line_rather_than_the_setting()
    {
        // Putting spaces in front of a tab-indented line produces exactly the mixed
        // indentation the other two transforms exist to clean up.
        var result = Run("text.indent", "\tone\ntwo\n\nthree\n", new TransformOptions(IndentSize: 2));

        Assert.True(result.Success);

        // The blank line stays blank: indenting it would add trailing whitespace to a line
        // with nothing on it, invisible here and a diff comment later.
        Assert.Equal("\t\tone\n  two\n\n  three\n", result.Text);
    }

    [Fact]
    public void Dedent_removes_up_to_one_level_and_never_complains()
    {
        // "Up to", because a line indented by three in a four-space document should reach
        // the margin rather than being skipped for not fitting the arithmetic.
        var result = Run("text.dedent", "    four\n   three\nnone\n\tone tab\n", new TransformOptions(IndentSize: 4));

        Assert.True(result.Success);
        Assert.Equal("four\nthree\nnone\none tab\n", result.Text);
    }

    [Fact]
    public void Indent_and_dedent_are_inverses_for_a_normal_block()
    {
        const string Original = "def thing():\n    return 1\n";
        var options = new TransformOptions(IndentSize: 4);

        var indented = Run("text.indent", Original, options);
        var back = Run("text.dedent", indented.Text!, options);

        Assert.Equal(Original, back.Text);
    }

    [Fact]
    public void Every_line_transform_writes_the_buffers_own_line_ending()
    {
        // A transform that writes LF into a CRLF buffer leaves a file with both, which is
        // invisible on screen and very visible in a diff — and blamed on the editor.
        var options = new TransformOptions(NewLine: "\r\n");

        // Chosen so that every transform below actually changes something. One that changed
        // nothing would report rather than return text, and the assertion would be measuring
        // the wrong thing — or dereferencing null.
        const string Buffer = "  beta  \r\n  beta  \r\n\r\nalpha,gamma\r\n";

        foreach (var id in new[]
                 {
                     "text.reverseLines", "text.dedupeLines", "text.removeBlankLines",
                     "text.splitCommas", "text.trimTrailing", "text.collapseWhitespace",
                     "text.indent", "text.dedent", "case.snake",
                 })
        {
            var result = Run(id, Buffer, options);

            Assert.True(result.Success, id);
            Assert.NotNull(result.Text);
            Assert.DoesNotContain(
                "\n",
                result.Text!.Replace("\r\n", string.Empty, StringComparison.Ordinal),
                StringComparison.Ordinal);
            Assert.EndsWith("\r\n", result.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_text_transform_is_ever_the_suggested_action()
    {
        // No detector can tell you a buffer is a list or an identifier, so none of these is
        // ever what Ctrl+Enter should reach for. It is the one thing that would make the key
        // dangerous: re-casing a paragraph or scrambling a JSON document without asking.
        var formats = Enum.GetValues<FormatId>();

        foreach (var transform in TransformRegistry.All.Where(static t => t.Category == TransformCategory.Text))
        {
            foreach (var format in formats)
            {
                var detection = new DetectionResult(format, DetectionConfidence.Certain);

                Assert.False(transform.IsAvailable(detection), $"{transform.Id} claimed {format}.");
            }
        }
    }
}
