using Etch.Core.Text;
using Xunit;

namespace Etch.Core.Tests.Text;

/// <summary>
/// The bracket-matching folding strategy.
/// </summary>
/// <remarks>
/// Every case here is a way a naive bracket counter goes wrong. A <c>{</c> inside a string
/// is not a block; a <c>}</c> inside a comment does not close one; and one stray quote must
/// not silently delete every fold below it, which is the failure nobody reports because it
/// looks like the feature simply not being there.
/// </remarks>
public class BraceFoldingTests
{
    private static IReadOnlyList<FoldRegion> Scan(string text) => BraceFolding.Scan(text);

    [Fact]
    public void A_multi_line_object_folds()
    {
        const string Text = "{\n  \"a\": 1\n}";

        var region = Assert.Single(Scan(Text));

        Assert.Equal(0, region.StartOffset);
        Assert.Equal(Text.Length, region.EndOffset);
        Assert.Equal("{...}", region.Label);
    }

    [Fact]
    public void A_single_line_region_does_not_fold()
    {
        // Collapsing it would hide nothing and cost a marker in the margin, which is how a
        // fold margin turns into noise.
        Assert.Empty(Scan("{ \"a\": 1 }"));
        Assert.Empty(Scan("var x = new[] { 1, 2, 3 };"));
    }

    [Fact]
    public void Nested_regions_are_returned_outermost_first()
    {
        // AvalonEdit's UpdateFoldings requires ascending start offsets and misbehaves
        // quietly rather than throwing when it does not get them. Matching brackets are
        // discovered innermost-first, so the ordering is something this has to do, not
        // something it gets.
        var regions = Scan("{\n  \"a\": {\n    \"b\": 1\n  }\n}");

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].StartOffset < regions[1].StartOffset);
        Assert.True(regions[0].EndOffset > regions[1].EndOffset);
    }

    [Fact]
    public void Brackets_inside_a_string_are_not_structure()
    {
        // The whole reason this cannot be a bracket counter.
        Assert.Empty(Scan("var pattern = \"{\";\nvar other = 1;\n"));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_a_string()
    {
        // "\"" is a string holding one quote. Treating the escaped quote as the terminator
        // would leave the scanner outside a string when it is inside one, and every bracket
        // after it would be counted.
        Assert.Empty(Scan("var quote = \"\\\"{\";\nvar other = 1;\n"));
    }

    [Fact]
    public void An_escaped_backslash_does_end_a_string()
    {
        // The mirror image, and the one a naive escape rule gets wrong: in "\\" the second
        // backslash is escaped *data*, so the quote after it really does close the string
        // and the brace that follows really is structure.
        var region = Assert.Single(Scan("x = \"\\\\\" + {\n  1\n};\n"));

        Assert.Equal("{...}", region.Label);
    }

    [Fact]
    public void Brackets_inside_comments_are_not_structure()
    {
        Assert.Empty(Scan("// {\nvar x = 1;\n// }\n"));
        Assert.Empty(Scan("/* {\n   still a comment\n} */\nvar x = 1;\n"));
    }

    [Fact]
    public void A_stray_quote_costs_only_its_own_line()
    {
        // The recovery this type documents. A quote that is never closed ends at the line
        // break, so the block starting on the next line still folds. Without that, one
        // apostrophe would swallow the rest of the file as string content and every fold
        // below would vanish with nothing to explain it.
        var region = Assert.Single(Scan("var broken = \"oops;\nif (x) {\n  y();\n}\n"));

        Assert.Equal("{...}", region.Label);
    }

    [Fact]
    public void An_unmatched_closer_is_discarded_rather_than_popping()
    {
        // Popping whatever is on top would let a stray } close an outer block and produce
        // a fold spanning the wrong half of the file — a visibly broken margin, where
        // discarding merely produces one fewer fold.
        var regions = Scan("{\n  ]\n  \"a\": 1\n}");

        var region = Assert.Single(regions);
        Assert.Equal(0, region.StartOffset);
    }

    [Fact]
    public void An_unclosed_bracket_produces_no_region()
    {
        Assert.Empty(Scan("{\n  \"a\": 1\n"));
    }

    [Fact]
    public void Square_brackets_fold_and_are_labelled_as_themselves()
    {
        var region = Assert.Single(Scan("[\n  1,\n  2\n]"));

        Assert.Equal("[...]", region.Label);
    }

    [Fact]
    public void Line_numbers_survive_an_escape_at_the_end_of_a_line()
    {
        // A backslash immediately before a newline must not consume the newline, or the
        // line counter falls behind and a genuinely multi-line region is dismissed as
        // single-line. The region below spans two lines only if the count is right.
        var region = Assert.Single(Scan("x = \"a\\\n{\n}\n"));

        Assert.Equal("{...}", region.Label);
    }

    [Fact]
    public void Nesting_past_the_depth_limit_is_ignored_rather_than_unbounded()
    {
        // Untrusted input: a file that is a megabyte of '[' is not a plausible document but
        // it is a trivially constructible one, and an unbounded stack costs an entry per
        // byte. Everything shallower than the cap still folds correctly.
        var deep = new string('[', BraceFolding.MaxDepth + 50) + "\n" + new string(']', BraceFolding.MaxDepth + 50);

        var regions = Scan(deep);

        Assert.Equal(BraceFolding.MaxDepth, regions.Count);
        Assert.Equal(0, regions[0].StartOffset);
    }

    [Fact]
    public void An_empty_buffer_is_not_a_special_case()
    {
        Assert.Empty(Scan(string.Empty));
    }

    [Fact]
    public void A_null_buffer_is_rejected_rather_than_treated_as_empty()
    {
        Assert.Throws<ArgumentNullException>(() => BraceFolding.Scan(null!));
    }

    [Fact]
    public void Every_region_is_a_well_formed_span_of_the_buffer()
    {
        const string Text = """
            {
              "list": [
                { "inline": true },
                {
                  "nested": {
                    "deep": 1
                  }
                }
              ],
              "text": "a } and a { inside a string",
              // a } in a comment
              "end": 0
            }
            """;

        var regions = Scan(Text);

        Assert.NotEmpty(regions);

        foreach (var region in regions)
        {
            Assert.InRange(region.StartOffset, 0, Text.Length - 1);
            Assert.InRange(region.EndOffset, region.StartOffset + 1, Text.Length);

            // The label is derived from the opening bracket, so this also asserts that the
            // pair actually matches rather than that two unrelated brackets were joined.
            var opener = region.Label[0] == '{' ? '{' : '[';
            var closer = opener == '{' ? '}' : ']';

            Assert.Equal(opener, Text[region.StartOffset]);
            Assert.Equal(closer, Text[region.EndOffset - 1]);
        }
    }
}
