using Etch.Core.Abstractions;
using Etch.Core.Detection;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Transforms;

/// <summary>
/// What the transforms actually do to text, including the cases where they refuse.
/// </summary>
public class TransformTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
        + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ"
        + ".SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static TransformResult Run(string id, string text, TransformOptions? options = null)
    {
        var transform = TransformRegistry.Find(id);

        Assert.NotNull(transform);

        return transform!.Apply(new TransformInput(text, WasSelection: false, options ?? TransformOptions.Default));
    }

    [Fact]
    public void Every_registered_transform_has_a_unique_id_and_a_name()
    {
        var ids = TransformRegistry.All.Select(static transform => transform.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(TransformRegistry.All, static transform =>
        {
            Assert.False(string.IsNullOrWhiteSpace(transform.Name));
            Assert.NotNull(transform.Aliases);
            Assert.Same(transform, TransformRegistry.Find(transform.Id));
        });
    }

    [Fact]
    public void Format_json_indents_and_reports_json_back()
    {
        var result = Run("json.format", """{"b":1,"a":[2,3]}""");

        Assert.True(result.Success);
        Assert.Equal(FormatId.Json, result.ResultingFormat);
        Assert.Contains("\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("\"b\": 1", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_json_does_not_escape_text_it_had_no_reason_to()
    {
        // Utf8JsonWriter escapes non-ASCII by default because its usual job is emitting
        // JSON into HTML. Here it is putting text back in a text editor, and turning
        // "café" into "café" is a corruption as far as the author is concerned.
        var result = Run("json.format", """{"city":"café","tag":"a<b&c"}""");

        Assert.True(result.Success);
        Assert.Contains("café", result.Text, StringComparison.Ordinal);
        Assert.Contains("a<b&c", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_json_emits_the_line_ending_the_document_uses()
    {
        var result = Run("json.format", """{"a":1}""", new TransformOptions(IndentSize: 4, NewLine: "\r\n"));

        Assert.True(result.Success);
        Assert.Contains("\r\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", result.Text!.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Minify_json_produces_one_line()
    {
        var result = Run("json.minify", "{\n  \"a\": 1,\n  \"b\": [2, 3]\n}");

        Assert.True(result.Success);
        Assert.Equal("""{"a":1,"b":[2,3]}""", result.Text);
    }

    [Fact]
    public void Sorting_json_keys_leaves_array_order_alone()
    {
        // Object order is presentation; array order is data. Sorting an array would
        // change what the document means.
        var result = Run("json.sortKeys", """{"b":1,"a":{"z":0,"y":[3,1,2]}}""");

        Assert.True(result.Success);
        Assert.True(result.Text!.IndexOf("\"a\"", StringComparison.Ordinal)
            < result.Text.IndexOf("\"b\"", StringComparison.Ordinal));
        Assert.Contains("3,", result.Text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_failure_says_where()
    {
        var result = Run("json.format", """{"a": }""");

        Assert.False(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("Not valid JSON", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SGVsbG8sIHdvcmxkIQ==", "Hello, world!")]
    [InlineData("SGVsbG8sIHdvcmxkIQ", "Hello, world!")]
    [InlineData("  SGVsbG8sIHdvcmxkIQ==  ", "Hello, world!")]
    public void Base64_decodes_padded_unpadded_and_untidy(string encoded, string expected)
    {
        var result = Run("base64.decode", encoded);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void Base64_round_trips()
    {
        const string Original = "Etch — a scratchpad. 日本語 too.";

        var encoded = Run("base64.encode", Original);

        Assert.True(encoded.Success);

        var decoded = Run("base64.decode", encoded.Text!);

        Assert.True(decoded.Success);
        Assert.Equal(Original, decoded.Text);
    }

    [Fact]
    public void Base64_that_decodes_to_binary_is_reported_rather_than_mangled()
    {
        // Replacing undecodable bytes with U+FFFD would destroy the data, and the user
        // would only discover it after pasting the result somewhere that mattered.
        var result = Run("base64.decode", Convert.ToBase64String([0xFF, 0xFE, 0xFD, 0xFC, 0x00, 0x01]));

        Assert.False(result.Success);
        Assert.Contains("binary", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Url_round_trips_and_leaves_plus_alone_on_the_way_back()
    {
        var encoded = Run("url.encode", "name=value&x= y");

        Assert.True(encoded.Success);
        Assert.DoesNotContain("&", encoded.Text, StringComparison.Ordinal);

        var decoded = Run("url.decode", encoded.Text!);

        Assert.True(decoded.Success);
        Assert.Equal("name=value&x= y", decoded.Text);
    }

    [Theory]
    [InlineData("48656c6c6f", "Hello")]
    [InlineData("48 65 6c 6c 6f", "Hello")]
    [InlineData("48:65:6c:6c:6f", "Hello")]
    public void Hex_decodes_however_it_was_separated(string hex, string expected)
    {
        var result = Run("hex.decode", hex);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void An_odd_number_of_hex_digits_is_refused_with_the_count()
    {
        var result = Run("hex.decode", "48656c6c6");

        Assert.False(result.Success);
        Assert.Contains("9", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decoding_a_jwt_leads_with_the_fact_that_it_is_not_verified()
    {
        // A security requirement from the plan, implemented where it cannot be scrolled
        // past before the claims are read.
        var result = Run("jwt.decode", Jwt);

        Assert.True(result.Success);
        Assert.StartsWith("// SIGNATURE NOT VERIFIED", result.Text, StringComparison.Ordinal);
        Assert.Contains("HS256", result.Text, StringComparison.Ordinal);
        Assert.Contains("John Doe", result.Text, StringComparison.Ordinal);
        Assert.Contains("2018-01-18", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Epoch_converts_both_precisions_to_utc()
    {
        Assert.Equal("2018-01-18T01:30:22.000Z", Run("time.epochToIso", "1516239022").Text);
        Assert.Equal("2018-01-18T01:30:22.123Z", Run("time.epochToIso", "1516239022123").Text);
    }

    [Fact]
    public void Sorting_lines_is_ordinal_and_keeps_the_trailing_newline()
    {
        // A trailing newline is not a line. Sorting it would move the blank to the top
        // and silently delete the file's final newline, which shows up in the next diff.
        var result = Run("text.sortLines", "beta\nalpha\nGamma\n");

        Assert.True(result.Success);
        Assert.Equal("Gamma\nalpha\nbeta\n", result.Text);
    }

    [Fact]
    public void Sorting_lines_survives_crlf()
    {
        var result = Run("text.sortLines", "beta\r\nalpha\r\n", new TransformOptions(NewLine: "\r\n"));

        Assert.True(result.Success);
        Assert.Equal("alpha\r\nbeta\r\n", result.Text);
    }

    [Fact]
    public void Encoding_transforms_are_never_the_suggested_action()
    {
        // Encoding is something you go looking for; decoding is something the buffer
        // tells you it needs. Suggesting "encode" for a base64 string would be the one
        // case where the suggestion is actively wrong.
        var detection = FormatDetection.Detect("SGVsbG8sIHdvcmxkISBUaGlzIGlzIGJhc2U2NC4=");

        Assert.False(TransformRegistry.Find("base64.encode")!.IsAvailable(detection));
        Assert.True(TransformRegistry.Find("base64.decode")!.IsAvailable(detection));
    }

    [Fact]
    public void Chaining_works_because_a_result_is_detectable_again()
    {
        // The product's headline claim: base64 to JSON to sorted keys, without leaving
        // the buffer. Each step's output is the next step's input, detected afresh.
        var encoded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("""{"b":2,"a":1,"c":[3,1]}"""));

        var first = FormatDetection.Detect(encoded);

        Assert.Equal(FormatId.Base64, first.Format);

        var decoded = Run("base64.decode", encoded);

        Assert.True(decoded.Success);

        var second = FormatDetection.Detect(decoded.Text!);

        Assert.Equal(FormatId.Json, second.Format);

        var sorted = Run("json.sortKeys", decoded.Text!);

        Assert.True(sorted.Success);
        Assert.True(sorted.Text!.IndexOf("\"a\"", StringComparison.Ordinal)
            < sorted.Text.IndexOf("\"b\"", StringComparison.Ordinal));
    }

    [Fact]
    public void No_transform_throws_on_hostile_input()
    {
        // Bad input is the expected case for a scratchpad, not an exceptional one, so
        // every transform reports it as a failed result rather than an exception.
        var inputs = new[] { string.Empty, "\0\0\0", new string('%', 500), new string('{', 500), "\ud800" };

        foreach (var transform in TransformRegistry.All)
        {
            foreach (var text in inputs)
            {
                var result = transform.Apply(TransformInput.Whole(text));

                // Three shapes are legal, and every one of them says something: replacement
                // text, a finding with no text, or an error. What is not legal is a success
                // carrying neither — the editor would have nothing to write and nothing to
                // show, and the keystroke would appear to have done nothing at all.
                Assert.True(
                    result.Success
                        ? result.Text is not null || result.Message is not null
                        : result.Error is not null,
                    $"{transform.Id} returned a result with nothing in it.");
            }
        }
    }
}
