using Etch.Core.Abstractions;
using Etch.Core.Detection;
using Etch.Core.Palette;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Palette;

/// <summary>
/// The palette's whole promise is that the right answer is the first row before
/// anything is typed. These assert that ordering rather than the fuzzy matcher's
/// arithmetic, because the ordering is what people actually experience.
/// </summary>
public class PaletteRankingTests
{
    [Fact]
    public void The_first_row_for_a_json_buffer_is_a_json_transform()
    {
        var ranked = PaletteRanking.Rank(query: null, FormatDetection.Detect("""{"a":1}"""));

        Assert.NotEmpty(ranked);
        Assert.True(ranked[0].IsSuggested);
        Assert.StartsWith("json.", ranked[0].Transform.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_is_listed_even_when_it_does_not_apply()
    {
        // Detection is a guess and the user is not. Someone base64-encoding a JSON
        // document is doing something the buffer could not have predicted, not
        // something wrong.
        var ranked = PaletteRanking.Rank(query: null, FormatDetection.Detect("""{"a":1}"""));

        Assert.Contains(ranked, entry => entry.Transform.Id == "base64.encode");
        Assert.Contains(ranked, entry => !entry.IsSuggested);
    }

    [Fact]
    public void Applicability_outranks_recency()
    {
        // The constants exist to make this true: eight recent uses are still worth less
        // than applying to the buffer in front of you.
        var recent = new[] { "text.sortLines", "base64.encode", "url.encode" };

        var ranked = PaletteRanking.Rank(query: null, FormatDetection.Detect("""{"a":1}"""), recent);

        Assert.True(ranked[0].IsSuggested);
    }

    [Fact]
    public void Recency_decides_between_two_transforms_that_both_apply()
    {
        var detection = FormatDetection.Detect("""{"a":1}""");

        var ranked = PaletteRanking.Rank(query: null, detection, ["json.minify"]);

        Assert.Equal("json.minify", ranked[0].Transform.Id);
    }

    [Theory]
    [InlineData("fj", "json.format")]
    [InlineData("format json", "json.format")]
    [InlineData("prettify", "json.format")]
    [InlineData("minify", "json.minify")]
    [InlineData("sort lines", "text.sortLines")]
    [InlineData("jwt", "jwt.decode")]
    [InlineData("epoch", "time.epochToIso")]
    public void A_query_finds_what_it_abbreviates(string query, string expectedId)
    {
        // Plain text, so nothing is suggested and the query is doing all the work.
        var ranked = PaletteRanking.Rank(query, DetectionResult.PlainText);

        Assert.NotEmpty(ranked);
        Assert.Equal(expectedId, ranked[0].Transform.Id);
    }

    [Fact]
    public void A_query_that_matches_nothing_returns_nothing()
    {
        Assert.Empty(PaletteRanking.Rank("zzzzqqqq", DetectionResult.PlainText));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("timestamp")]
    [InlineData("unix time")]
    public void The_detected_format_still_decides_which_way_a_reversible_pair_runs(string query)
    {
        // The other half of the rule that gave time.epochToIso the bare format nouns: a
        // typed word picks the transform that consumes that format, but only while the
        // buffer has nothing to say. Once it is recognisably ISO-8601 the suggested bonus
        // outranks every fuzzy score, and these queries have to arrive at the inverse.
        //
        // Worth pinning because the mechanism is invisible: time.isoToEpoch no longer
        // carries any of these words itself, so it is reached through "to epoch" and its
        // siblings. Narrow the aliases any further and the transform stops being findable
        // for the buffer it exists to serve.
        var ranked = PaletteRanking.Rank(query, FormatDetection.Detect("2026-08-02T09:45:00Z"));

        Assert.NotEmpty(ranked);
        Assert.Equal("time.isoToEpoch", ranked[0].Transform.Id);
    }

    [Fact]
    public void Neither_half_of_a_reversible_pair_answers_to_the_others_bare_noun()
    {
        // The defect this pins cost a test failure and was invisible in either file alone:
        // both transforms listed "epoch", so both scored 97, and Precedence — a value that
        // exists to settle Ctrl+Enter on a detected buffer — silently decided what a typed
        // word meant. A shared alias between inverses is a tie by construction.
        var epochToIso = TransformRegistry.All.Single(static t => t.Id == "time.epochToIso");
        var isoToEpoch = TransformRegistry.All.Single(static t => t.Id == "time.isoToEpoch");

        Assert.Empty(epochToIso.Aliases.Intersect(isoToEpoch.Aliases, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_order_is_stable_between_openings()
    {
        // A palette whose rows move when nothing changed is one people stop trusting to
        // muscle memory, which is most of what a palette is for.
        var detection = FormatDetection.Detect("""{"a":1}""");

        var first = PaletteRanking.Rank(query: null, detection).Select(static e => e.Transform.Id);
        var second = PaletteRanking.Rank(query: null, detection).Select(static e => e.Transform.Id);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Ctrl_enter_suggests_nothing_when_the_buffer_is_plain_text()
    {
        // The key applies a transform without showing what it is about to do, so it has
        // to be the obvious action or no action at all.
        Assert.Null(PaletteRanking.Suggested(DetectionResult.PlainText));
        Assert.Null(PaletteRanking.Suggested(FormatDetection.Detect("just some prose")));
    }

    [Theory]
    [InlineData("""{"a":1}""", "json.format")]
    [InlineData("SGVsbG8sIHdvcmxkISBUaGlzIGlzIGJhc2U2NC4=", "base64.decode")]
    [InlineData("1763058000", "time.epochToIso")]
    [InlineData("name%3Dvalue%26other%3D1", "url.decode")]
    public void Ctrl_enter_does_the_obvious_thing(string text, string expectedId)
    {
        var suggested = PaletteRanking.Suggested(FormatDetection.Detect(text));

        Assert.NotNull(suggested);
        Assert.Equal(expectedId, suggested!.Id);
    }

    /// <summary>What "b" moving to the front should leave behind, spelled out once.</summary>
    private static readonly string[] BMovedToTheFront = ["b", "a", "c"];

    [Fact]
    public void Remembering_moves_a_transform_to_the_front_without_duplicating_it()
    {
        var recent = PaletteRanking.Remember(["a", "b", "c"], "b");

        Assert.Equal(BMovedToTheFront, recent);
    }

    [Fact]
    public void The_recency_list_is_bounded()
    {
        IReadOnlyList<string> recent = [];

        for (var i = 0; i < PaletteRanking.RecencyDepth * 3; i++)
        {
            recent = PaletteRanking.Remember(recent, $"id{i}");
        }

        Assert.Equal(PaletteRanking.RecencyDepth, recent.Count);
    }

    [Fact]
    public void Initials_beat_letters_buried_in_the_middle_of_a_word()
    {
        Assert.True(FuzzyMatch.TryScore("Format JSON", "fj", out var initials));
        Assert.True(FuzzyMatch.TryScore("Minify JSON", "fj", out var buried));
        Assert.True(initials > buried);
    }

    [Fact]
    public void A_query_that_is_not_a_subsequence_does_not_match()
    {
        Assert.False(FuzzyMatch.TryScore("Format JSON", "xyz", out _));
        Assert.True(FuzzyMatch.TryScore("Format JSON", string.Empty, out _));
    }
}
