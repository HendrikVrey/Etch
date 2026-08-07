using Etch.Core.Abstractions;
using Etch.Core.Palette;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Palette;

/// <summary>
/// What <c>Ctrl+Enter</c> does, pinned one format at a time.
/// </summary>
/// <remarks>
/// <para>
/// A hand-written table, in the same spirit as <c>KeyMapTests</c>: a test that asked the
/// ranking what it would pick and then asserted that it picked it would agree with any
/// change, including a wrong one. These expectations have to be edited on purpose.
/// </para>
/// <para>
/// The file exists because slice 2 tripled the size of the registry, and until
/// <c>ITransform.Precedence</c> landed the answer to this question was decided by
/// <b>alphabetical order</b> — "Format JSON" beat "Minify JSON" because F precedes M. That
/// was right by luck, and a transform named "Compact JSON" would have taken it over without
/// a single test failing.
/// </para>
/// <para>
/// <c>PaletteRankingTests</c> covers the ranking mechanism — fuzzy matching, recency, what
/// the palette lists. This file covers only the suggested-action table and the property that
/// makes it decidable.
/// </para>
/// </remarks>
public class SuggestionTests
{
    [Theory]
    [InlineData(FormatId.Json, "json.format")]
    [InlineData(FormatId.Base64, "base64.decode")]
    [InlineData(FormatId.Base64Url, "base64.decode")]
    [InlineData(FormatId.UrlEncoded, "url.decode")]
    [InlineData(FormatId.Hex, "hex.decode")]
    [InlineData(FormatId.Jwt, "jwt.decode")]
    [InlineData(FormatId.UnixEpoch, "time.epochToIso")]
    [InlineData(FormatId.Iso8601, "time.isoToEpoch")]
    public void The_obvious_action_for_each_format_is_the_documented_one(FormatId format, string expected)
    {
        var suggested = PaletteRanking.Suggested(new DetectionResult(format, DetectionConfidence.Certain));

        Assert.NotNull(suggested);
        Assert.Equal(expected, suggested!.Id);
    }

    [Theory]
    [InlineData(FormatId.Ndjson)]
    [InlineData(FormatId.Guid)]
    public void A_recognised_format_that_nothing_claims_suggests_nothing(FormatId format)
    {
        // Ctrl+Enter acts without showing what it is about to do, so "the obvious thing or
        // nothing" is the only safe rule. Both of these are detected and neither has a
        // transform yet — and a GUID in particular must not attract "New GUID", which would
        // replace the one in the buffer with a different one.
        Assert.Null(PaletteRanking.Suggested(new DetectionResult(format, DetectionConfidence.Certain)));
    }

    [Fact]
    public void Precedence_only_breaks_a_tie_and_never_beats_recency()
    {
        // The plan's rule: applicability first, then recency. Someone who has just used
        // "Minify JSON" gets it back on the next Ctrl+Enter even though "Format JSON" claims
        // the slot when nothing has been used — a preference the user expressed beats a
        // default the transform declared.
        var json = new DetectionResult(FormatId.Json, DetectionConfidence.Certain);

        Assert.Equal("json.format", PaletteRanking.Suggested(json)!.Id);
        Assert.Equal("json.minify", PaletteRanking.Suggested(json, ["json.minify"])!.Id);
    }

    [Fact]
    public void Everything_that_applies_to_a_format_sorts_above_everything_that_does_not()
    {
        // Applicability is the first sort key, not a nudge. It is what makes the right
        // answer the first row before anything has been typed — and the assertion is that
        // the list is *partitioned*, which is stronger than checking the top row.
        var detection = new DetectionResult(FormatId.Json, DetectionConfidence.Certain);
        var suggested = PaletteRanking.Rank(query: null, detection).Select(static e => e.IsSuggested).ToList();

        var lastSuggested = suggested.LastIndexOf(true);
        var firstUnsuggested = suggested.IndexOf(false);

        Assert.True(lastSuggested >= 0, "No transform applied to JSON at all.");

        // Guarded rather than assumed: if a future registry had every transform apply to
        // JSON there would be no unsuggested row, and IndexOf would return -1 and fail this
        // for the opposite of the reason it exists.
        Assert.True(firstUnsuggested < 0 || lastSuggested < firstUnsuggested);
    }

    [Fact]
    public void The_menu_never_offers_more_rows_than_it_asked_for()
    {
        // The editor's right-click menu asks for four. A registry that grew a fifth JSON
        // transform must not quietly grow the menu with it.
        var json = new DetectionResult(FormatId.Json, DetectionConfidence.Certain);
        var all = PaletteRanking.SuggestedTop(json, count: int.MaxValue);

        // Stated rather than assumed, so a shrunken registry fails with the reason rather
        // than with an off-by-one further down.
        Assert.True(all.Count >= 2, $"JSON has {all.Count} ready transforms; this test needs two.");

        Assert.Equal(2, PaletteRanking.SuggestedTop(json, count: 2).Count);
        Assert.Equal(all.Take(2), PaletteRanking.SuggestedTop(json, count: 2));
        Assert.True(PaletteRanking.SuggestedTop(json, count: 4).Count <= 4);
        Assert.Empty(PaletteRanking.SuggestedTop(json, count: 0));
        Assert.Empty(PaletteRanking.SuggestedTop(json, count: -1));
    }

    [Fact]
    public void The_menu_offers_only_transforms_that_apply_to_what_is_in_the_buffer()
    {
        // The green marker means "this is ready for what you have". A row that does not
        // apply would be a green dot that is not true, in the one place the user is
        // reading the dots rather than the names.
        foreach (var format in Enum.GetValues<FormatId>())
        {
            var detection = new DetectionResult(format, DetectionConfidence.Certain);

            foreach (var transform in PaletteRanking.SuggestedTop(detection))
            {
                Assert.True(
                    transform.IsAvailable(detection),
                    $"{transform.Id} was offered for {format} but does not apply to it.");
            }
        }
    }

    [Fact]
    public void Nothing_is_offered_for_text_that_was_not_recognised()
    {
        // Empty, not null: the menu shows an explanatory row in this case and a null would
        // make that path a NullReferenceException instead.
        Assert.Empty(PaletteRanking.SuggestedTop(DetectionResult.PlainText));
    }

    [Fact]
    public void The_first_row_is_the_one_Ctrl_Enter_would_run()
    {
        // The contract the menu's "Ctrl+Enter" gesture text depends on. If these two ever
        // disagree, the menu is showing the shortcut against a row the shortcut does not
        // run, which is worse than showing no shortcut at all.
        // Exercised with and without a recency list, because recency is what reorders the
        // leading run and therefore the only thing that could separate the two answers.
        var recencies = new IReadOnlyList<string>?[]
        {
            null,
            new[] { "json.minify" },
            new[] { "base64.encode" },
        };

        foreach (var format in Enum.GetValues<FormatId>())
        {
            var detection = new DetectionResult(format, DetectionConfidence.Certain);

            foreach (var recent in recencies)
            {
                var suggested = PaletteRanking.Suggested(detection, recent);
                var top = PaletteRanking.SuggestedTop(detection, recent);

                if (suggested is null)
                {
                    Assert.Empty(top);
                    continue;
                }

                Assert.NotEmpty(top);
                Assert.Same(suggested, top[0]);
            }
        }
    }

    [Fact]
    public void No_two_transforms_claim_the_same_format_at_the_same_top_precedence()
    {
        // The condition under which the suggested action falls back to alphabetical order,
        // which is where this whole mechanism came from. A tie further down the list is just
        // two rows in a list; a tie at the top is Ctrl+Enter deciding by name.
        //
        // If this fails, a Precedence is missing rather than a name being wrong.
        foreach (var format in Enum.GetValues<FormatId>())
        {
            var detection = new DetectionResult(format, DetectionConfidence.Certain);

            var applicable = TransformRegistry.All
                .Where(transform => transform.IsAvailable(detection))
                .ToArray();

            if (applicable.Length < 2)
            {
                continue;
            }

            var best = applicable.Min(static transform => transform.Precedence);

            var tied = applicable
                .Where(transform => transform.Precedence == best)
                .Select(static transform => transform.Id)
                .ToArray();

            Assert.True(
                tied.Length == 1,
                $"{format} is claimed by {string.Join(", ", tied)} at precedence {best}.");
        }
    }
}
