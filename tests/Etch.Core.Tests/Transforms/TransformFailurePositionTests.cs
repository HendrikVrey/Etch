using Etch.Core.Abstractions;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Transforms;

/// <summary>
/// Where a transform says its input stopped making sense.
/// </summary>
/// <remarks>
/// <para>
/// The offset is what the editor puts the caret on, so a wrong one is worse than none:
/// nothing about a caret in the wrong place looks broken.
/// </para>
/// <para>
/// <b>Why these assert a range of one rather than an exact offset.</b> Whether
/// <c>System.Text.Json</c> reports the position <em>at</em> the offending byte or just
/// past it is that library's convention, not Etch's, and pinning it here would make a
/// harmless framework change fail a test about something else entirely. What these tests
/// exist to catch is the <em>encoding</em> bug, a UTF-8 byte position used as a UTF-16
/// character offset, and every case below is arranged so that the buggy answer is at
/// least three positions away. A tolerance of one separates the two cleanly while leaving
/// the framework's own convention alone. See <see cref="Etch.Core.Text.Utf8Position"/>,
/// whose tests pin the mapping exactly, because that part is Etch's.
/// </para>
/// </remarks>
public class TransformFailurePositionTests
{
    private static TransformResult Run(string id, string text)
    {
        var transform = TransformRegistry.Find(id);

        Assert.NotNull(transform);

        return transform!.Apply(new TransformInput(text, WasSelection: false, TransformOptions.Default));
    }

    /// <summary>Asserts an offset points at <paramref name="expected"/>, give or take one.</summary>
    private static void AssertPointsAt(int expected, int? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual!.Value, expected - 1, expected + 1);
    }

    [Theory]
    [InlineData("json.format")]
    [InlineData("json.minify")]
    [InlineData("json.sortKeys")]
    [InlineData("json.validate")]
    public void A_broken_document_reports_where_it_broke(string id)
    {
        // The bad token is the 'x'. Everything before it is well-formed, so the parser
        // stops there, and every JSON transform reports it the same way, which is the
        // point of them sharing JsonFailure.
        const string Broken = """{"a": 1, "b": x}""";

        var result = Run(id, Broken);

        Assert.False(result.Success);
        AssertPointsAt(Broken.IndexOf('x', StringComparison.Ordinal), result.ErrorOffset);
    }

    [Fact]
    public void The_offset_survives_non_ascii_text_earlier_in_the_line()
    {
        // The whole reason Utf8Position exists. Three accented characters before the
        // error, two UTF-8 bytes each, so the byte position is three greater than the
        // character offset, and using it directly would land the caret three positions
        // early. Well outside the tolerance above.
        const string Broken = """{"café": "réservé", "b": x}""";

        var result = Run("json.validate", Broken);

        Assert.False(result.Success);
        AssertPointsAt(Broken.IndexOf('x', StringComparison.Ordinal), result.ErrorOffset);
    }

    [Fact]
    public void The_offset_survives_a_surrogate_pair_earlier_in_the_line()
    {
        // Four UTF-8 bytes and two UTF-16 chars, the largest disagreement between the
        // encodings, and the one that can go wrong in both directions. Two emoji put the
        // buggy answer four positions out.
        const string Broken = """{"party": "🎉🎉", "b": x}""";

        var result = Run("json.validate", Broken);

        Assert.False(result.Success);
        AssertPointsAt(Broken.IndexOf('x', StringComparison.Ordinal), result.ErrorOffset);
    }

    [Fact]
    public void A_failure_on_a_later_line_reports_a_document_offset()
    {
        // Not a line number. The offset has to be absolute, because that is what the
        // editor's caret takes, and a transform run over a selection would otherwise
        // have no way to express where it was.
        const string Broken = """
            {
              "a": 1,
              "b": x
            }
            """;

        var result = Run("json.validate", Broken);

        Assert.False(result.Success);
        AssertPointsAt(Broken.IndexOf('x', StringComparison.Ordinal), result.ErrorOffset);
    }

    [Fact]
    public void A_truncated_document_reports_a_position_inside_the_buffer()
    {
        // The most common failure there is, JSON cut off mid-write. The parser runs out
        // of input, so the position it reports is at or just past the end. What must not
        // happen is an offset outside the buffer: AvalonEdit throws on one past the end
        // of the document.
        const string Truncated = """{"a": 1, "b": [1, 2""";

        var result = Run("json.validate", Truncated);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorOffset);
        Assert.InRange(result.ErrorOffset!.Value, 0, Truncated.Length);
    }

    [Fact]
    public void A_lone_surrogate_fails_without_claiming_to_know_where()
    {
        // JsonDocument.Parse(string) transcodes to UTF-8 before parsing, so this fails
        // with an ArgumentException carrying no position at all. Null is the honest
        // answer; an invented zero would send the caret to the top of the document.
        var result = Run("json.validate", "{\"a\": \"\uD800\"}");

        Assert.False(result.Success);
        Assert.Null(result.ErrorOffset);
    }

    [Theory]
    [InlineData("base64.decode", "!!!not base64!!!")]
    [InlineData("text.sortLines", "")]
    public void A_failure_about_the_whole_input_reports_no_position(string id, string text)
    {
        // "That is not valid base64" is a statement about the buffer, not about a place
        // in it. Reporting offset zero for these would move the caret for no reason, and
        // the null is what tells the editor to leave it alone.
        var result = Run(id, text);

        Assert.False(result.Success);
        Assert.Null(result.ErrorOffset);
    }

    [Fact]
    public void Unescaping_maps_the_position_back_through_the_quoting_it_added()
    {
        // UnescapeJsonString parses a trimmed, quoted copy of the buffer rather than the
        // buffer itself, so every position the parser reports has to come back out
        // through both changes. Leading whitespace and the added quote pull in opposite
        // directions, which is why this is worth asserting rather than reasoning about.
        const string Broken = "  abc\\qdef  ";

        var result = Run("json.unescapeString", Broken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorOffset);

        // \q is not a JSON escape. Without the mapping the offset would be two short
        // (one for the quote added, two for the whitespace trimmed) and could point
        // before the backslash the user can actually see.
        AssertPointsAt(Broken.IndexOf('\\', StringComparison.Ordinal) + 1, result.ErrorOffset);
        Assert.InRange(result.ErrorOffset!.Value, 0, Broken.Length);
    }

    [Fact]
    public void A_negative_offset_is_treated_as_no_offset()
    {
        // The guard on the property itself. A negative value can only have come from
        // arithmetic that went wrong, and passing it on would throw in the editor rather
        // than here, a layer away from the mistake.
        var result = TransformResult.Failed("broken", -3);

        Assert.False(result.Success);
        Assert.Null(result.ErrorOffset);
    }

    [Fact]
    public void A_successful_transform_carries_no_position()
    {
        var result = Run("json.format", """{"a":1}""");

        Assert.True(result.Success);
        Assert.Null(result.ErrorOffset);
    }
}
