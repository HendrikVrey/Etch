using Etch.Core.Abstractions;
using Etch.Core.Detection;
using Xunit;

namespace Etch.Core.Tests.Detection;

/// <summary>
/// The corpus. Detection is the component most able to regress silently — nothing
/// throws when it guesses wrong, the format chip just says something slightly untrue
/// and the palette offers the wrong first row — so the guard against that is a table
/// of real-world samples with asserted answers rather than a handful of happy paths.
/// </summary>
public class FormatDetectionTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
        + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ"
        + ".SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    [Theory]
    [InlineData("""{"name":"etch","tags":["fast","small"]}""", FormatId.Json)]
    [InlineData("[1, 2, 3]", FormatId.Json)]
    [InlineData("{\n  \"a\": 1\n}", FormatId.Json)]
    [InlineData("{\"a\":1}\n{\"a\":2}\n{\"a\":3}", FormatId.Ndjson)]
    [InlineData("SGVsbG8sIHdvcmxkISBUaGlzIGlzIGJhc2U2NC4=", FormatId.Base64)]
    [InlineData("48656c6c6f2c20776f726c6421", FormatId.Hex)]
    [InlineData("de:ad:be:ef:ca:fe", FormatId.Hex)]
    [InlineData("name%3Dvalue%26other%3D1", FormatId.UrlEncoded)]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", FormatId.Guid)]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}", FormatId.Guid)]
    [InlineData("1763058000", FormatId.UnixEpoch)]
    [InlineData("1763058000123", FormatId.UnixEpoch)]
    public void Recognises(string text, FormatId expected)
    {
        var result = FormatDetection.Detect(text);

        Assert.Equal(expected, result.Format);
        Assert.True(result.IsRecognised);
    }

    [Fact]
    public void Recognises_a_jwt_ahead_of_the_base64url_it_is_made_of()
    {
        // Both detectors fire. The more specific answer is the useful one, and getting
        // this backwards would offer "base64 decode" for a token — which produces three
        // lines of mangled bytes instead of the claims.
        var result = FormatDetection.Detect(Jwt);

        Assert.Equal(FormatId.Jwt, result.Format);
        Assert.Equal(DetectionConfidence.Certain, result.Confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    [InlineData("Just some ordinary prose, written in whole words.")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    [InlineData("TODO: work out why the deploy is failing")]
    [InlineData("42")]
    // A date that is only the start of a longer string is not a date. The shape test has to
    // reach the end of the buffer, or every log line beginning with a timestamp would be
    // claimed as one.
    [InlineData("2026-07-31 deploy failed")]
    [InlineData("2026-07-31 2026-08-01")]
    public void Leaves_ordinary_text_alone(string text)
    {
        // The failure that matters most. A detector that fires on prose puts a wrong
        // transform under Ctrl+Enter, and Ctrl+Enter does not ask first.
        var result = FormatDetection.Detect(text);

        Assert.Equal(FormatId.PlainText, result.Format);
        Assert.False(result.IsRecognised);
    }

    [Fact]
    public void A_bare_number_is_a_timestamp_rather_than_a_json_document()
    {
        // 42 is legal JSON. It is never what someone pasting it meant.
        Assert.Equal(FormatId.PlainText, FormatDetection.Detect("42").Format);
        Assert.Equal(FormatId.UnixEpoch, FormatDetection.Detect("1763058000").Format);
    }

    [Fact]
    public void Half_typed_json_is_weak_rather_than_plain_text()
    {
        // Detection re-runs on a debounce while someone types. Reporting plain text for
        // every intermediate state would make the format chip flicker on every keystroke.
        var result = FormatDetection.Detect("""{"name": "etch", "tags": [""");

        Assert.Equal(FormatId.Json, result.Format);
        Assert.Equal(DetectionConfidence.Weak, result.Confidence);
    }

    [Fact]
    public void A_large_document_is_sampled_and_still_recognised()
    {
        // The case that shaped IFormatDetector: a prefix cannot be fully parsed, and a
        // detector that could only answer yes or no would have to call the largest and
        // most useful documents plain text.
        var padding = string.Join(",", Enumerable.Repeat("""{"k":"vvvvvvvvvvvvvvvvvvvv"}""", 8_000));
        var text = $"[{padding}]";

        Assert.True(text.Length > FormatDetection.SampleLimit);

        var result = FormatDetection.Detect(text);

        Assert.Equal(FormatId.Json, result.Format);
        Assert.Equal(DetectionConfidence.Likely, result.Confidence);
        Assert.True(result.WasSampled);
    }

    [Fact]
    public void A_single_json_object_on_its_own_line_is_not_ndjson()
    {
        Assert.Equal(FormatId.Json, FormatDetection.Detect("{\"a\":1}\n").Format);
    }

    [Fact]
    public void Base64_will_not_fire_on_a_sentence_of_the_right_length()
    {
        // Interior spaces are the rule that does this. Base64 in the wild is wrapped
        // with newlines and never broken with spaces.
        Assert.Equal(FormatId.PlainText, FormatDetection.Detect("this is a sentence okay").Format);
    }

    [Fact]
    public void Wrapped_base64_survives_its_line_breaks()
    {
        var wrapped = "SGVsbG8sIHdvcmxkISBUaGlz\r\nIGlzIGJhc2U2NCB3cmFwcGVk\r\nIGF0IHR3ZW50eS1mb3VyLg==";

        Assert.Equal(FormatId.Base64, FormatDetection.Detect(wrapped).Format);
    }

    [Fact]
    public void A_bare_thirty_two_character_hash_is_not_claimed_as_a_guid()
    {
        // It is far more often an MD5, and nothing in the text can distinguish them.
        // Claiming either would be guessing, so the dashless form is left to hex.
        var result = FormatDetection.Detect("3f2504e04f8911d39a0c0305e82c3301");

        Assert.Equal(FormatId.Hex, result.Format);
    }

    [Fact]
    public void An_iso_date_is_not_hexadecimal()
    {
        // Every decimal digit is a hex digit and '-' is a separator, so without the
        // uniform-group rule this is Hex with full confidence — and Ctrl+Enter, which
        // does not ask first, rewrites the date as four bytes of control characters.
        //
        // Slice 2 added a detector that claims the single date properly, which does not
        // retire the rule: Hex must still decline, or the tie-break becomes the only thing
        // standing between a date and being rewritten as bytes.
        //
        // The two-date buffer is what still proves that. ISO-8601 refuses it — a date that is
        // merely the start of a longer string is not a date — so nothing but Hex could claim
        // it, and the answer is plain text.
        Assert.Equal(FormatId.PlainText, FormatDetection.Detect("2026-07-31 2026-08-01").Format);
        Assert.Equal(FormatId.PlainText, FormatDetection.Detect("2026-07-31 2026-08-01 2026-09-01").Format);
        Assert.Equal(FormatId.Iso8601, FormatDetection.Detect("2026-07-31").Format);

        // And the forms the separator rule exists to keep still work.
        Assert.Equal(FormatId.Hex, FormatDetection.Detect("de-ad-be-ef-ca-fe").Format);
        Assert.Equal(FormatId.Hex, FormatDetection.Detect("48 65 6c 6c 6f 21").Format);
    }

    [Theory]
    [InlineData("2026-07-31")]
    [InlineData("2026-07-31T09:15:00Z")]
    [InlineData("2026-07-31 09:15:00")]
    [InlineData("2026-07-31T09:15")]
    [InlineData("2026-07-31T09:15:00.123Z")]
    [InlineData("2026-07-31T09:15:00.1234567+02:00")]
    [InlineData("2026-07-31T09:15:00-05:00")]
    [InlineData("  2026-07-31T09:15:00Z\n")]
    public void Recognises_iso_8601_in_the_forms_things_actually_emit(string text)
    {
        var result = FormatDetection.Detect(text);

        Assert.Equal(FormatId.Iso8601, result.Format);
        Assert.Equal(DetectionConfidence.Certain, result.Confidence);
    }

    [Theory]
    [InlineData("2026-07-31T09:15:00+0200")]
    [InlineData("2026-07-31T09:15:00-05")]
    [InlineData("2026-07-31t09:15:00z")]
    public void The_looser_spellings_are_the_frameworks_call_and_are_never_an_encoding(string text)
    {
        // Whether these are *claimed* is DateTimeOffset.TryParse's decision, not this
        // detector's. The shape test admits all three deliberately — a compact offset, a
        // two-digit offset, and the lower-case separators some emitters produce — and then
        // lets the parser have the last word, which costs nothing when it says no.
        //
        // What must hold either way is that they never end up as hex or base64. That is the
        // family of bug the uniform-group rule was written for, and it is worth pinning
        // without also asserting somebody else's parser grammar.
        var format = FormatDetection.Detect(text).Format;

        Assert.True(
            format is FormatId.Iso8601 or FormatId.PlainText,
            $"{text} was detected as {format}.");
    }

    [Theory]
    // The basic form. Legal ISO-8601 and deliberately refused: eight digits are also a hex
    // value, a bare number and the front of a great many identifiers, and nothing in the text
    // says which. The separators are what make the format recognisable.
    [InlineData("20260731")]
    [InlineData("20260731T091500Z")]
    // Shape right, values impossible. The scan proves the layout and DateTimeOffset.TryParse
    // rejects the calendar, which is the split this detector is built on.
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("2026-07-31T25:15:00Z")]
    // Other orderings are dates, but they are not this format, and TryParse on its own would
    // happily take every one of them.
    [InlineData("31-07-2026")]
    [InlineData("07/31/2026")]
    [InlineData("Jul 31 2026")]
    // A trailing decimal point with no digits after it.
    [InlineData("2026-07-31T09:15:00.")]
    // A comma decimal separator is legal ISO-8601 and is refused on purpose: TryParse does
    // not accept one, so admitting it to the shape test would produce text this detector
    // called ISO-8601 and then failed to parse.
    [InlineData("2026-07-31T09:15:00,123")]
    public void Does_not_claim_dates_that_are_not_extended_iso_8601(string text)
    {
        Assert.NotEqual(FormatId.Iso8601, FormatDetection.Detect(text).Format);
    }

    [Fact]
    public void Base64_without_a_distinguishing_character_is_read_as_the_standard_alphabet()
    {
        // The two alphabets differ in two characters, so anything using neither matches
        // both detectors at the same confidence and the tie-break decides. Standard is
        // the far more common form; a value that really is URL-safe says so.
        Assert.Equal(FormatId.Base64, FormatDetection.Detect("SGVsbG8gd29ybGQgYWJjZGVm").Format);
        Assert.Equal(FormatId.Base64, FormatDetection.Detect("SGVsbG8/d29ybGQrYWJjZGVm").Format);
        Assert.Equal(FormatId.Base64Url, FormatDetection.Detect("SGVsbG8_d29ybGQtYWJjZGVm").Format);
    }

    [Fact]
    public void A_percent_sign_in_prose_is_not_url_encoding()
    {
        Assert.Equal(FormatId.PlainText, FormatDetection.Detect("CPU usage hit 95% overnight").Format);
    }

    [Fact]
    public void Detecting_never_throws_on_hostile_input()
    {
        // Every buffer is untrusted, detection runs on a background thread, and an
        // exception there takes the process down with everything unsaved in it.
        var hostile = new[]
        {
            new string('{', 5_000),
            new string('%', 1_000),
            "\0\0\0\0\0\0\0\0\0\0\0\0",
            "\ud800",
            "[" + string.Concat(Enumerable.Repeat("[", 500)),
        };

        foreach (var text in hostile)
        {
            var result = FormatDetection.Detect(text);

            // Not merely "it returned something" — Enum.ToString() is never null, so that
            // assertion only ever restated the fact that the call did not throw. What is
            // worth pinning is that none of these is *claimed*: each one is malformed, and a
            // detector confident about malformed input puts a transform under Ctrl+Enter
            // that is about to fail or, worse, succeed on the wrong reading.
            Assert.Equal(FormatId.PlainText, result.Format);
            Assert.False(result.IsRecognised);
        }
    }
}
