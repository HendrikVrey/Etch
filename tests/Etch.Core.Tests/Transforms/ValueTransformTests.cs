using System.Globalization;
using System.Text;
using Etch.Core.Abstractions;
using Etch.Core.Detection;
using Etch.Core.Transforms;
using Xunit;

namespace Etch.Core.Tests.Transforms;

/// <summary>
/// The M2 slice-2 transforms that work on a value rather than on lines: JSON validation and
/// string literals, the entity and base64url encodings, the hashes, and the time conversions.
/// </summary>
public class ValueTransformTests
{
    private static TransformResult Run(string id, string text, TransformOptions? options = null)
    {
        var transform = TransformRegistry.Find(id);

        Assert.NotNull(transform);

        return transform!.Apply(new TransformInput(text, WasSelection: false, options ?? TransformOptions.Default));
    }

    [Fact]
    public void Validating_json_reports_without_touching_the_buffer()
    {
        // The whole reason this is not "format and see if it fails": someone checking a file
        // they are about to commit does not want its indentation changed as a side effect of
        // asking whether it is valid.
        var result = Run("json.validate", """{"a":1,"b":2}""");

        Assert.True(result.Success);
        Assert.Null(result.Text);
        Assert.Contains("Valid JSON", result.Message, StringComparison.Ordinal);
        Assert.Contains("2 keys", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validating_is_stricter_than_formatting_and_says_where_it_stopped()
    {
        // "Etch can format this" and "this is JSON" are different questions. A file with
        // trailing commas is a yes to the first and a no to the second, and anything that
        // rejects it downstream will be stricter than a scratchpad.
        const string TrailingComma = """{"a":1,}""";

        Assert.True(Run("json.format", TrailingComma).Success);

        var validated = Run("json.validate", TrailingComma);

        Assert.False(validated.Success);
        Assert.Contains("Not valid JSON", validated.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Escaping_and_unescaping_a_json_string_round_trips()
    {
        const string Original = """{"id":1,"note":"a \"quoted\" thing"}""";

        var escaped = Run("json.escapeString", Original);

        Assert.True(escaped.Success);
        Assert.StartsWith("\"", escaped.Text, StringComparison.Ordinal);
        Assert.EndsWith("\"", escaped.Text, StringComparison.Ordinal);

        var back = Run("json.unescapeString", escaped.Text!);

        Assert.True(back.Success);
        Assert.Equal(Original, back.Text);
    }

    [Fact]
    public void Escaping_leaves_text_that_needed_no_escaping_alone()
    {
        // The relaxed encoder, for the same reason the formatter uses one: turning "café"
        // into "café" is technically correct and completely unhelpful in an editor.
        var result = Run("json.escapeString", "café");

        Assert.Equal("\"café\"", result.Text);
    }

    [Fact]
    public void Unescaping_accepts_a_literal_that_lost_its_quotes()
    {
        // Half a literal, copied out of a log viewer that had already stripped them. It is
        // the common paste, and refusing it would send people to find the quotes by hand.
        var result = Run("json.unescapeString", """{\"id\":1}""");

        Assert.True(result.Success);
        Assert.Equal("""{"id":1}""", result.Text);
    }

    [Fact]
    public void Entity_encoding_escapes_the_five_and_nothing_else()
    {
        // Deliberately narrower than WebUtility.HtmlEncode, which also rewrites everything
        // above U+009F as a numeric entity — correct for an unknown output encoding, and
        // wrong for a scratchpad that is UTF-8 throughout.
        var result = Run("html.encodeEntities", "<a href='x'>café & co</a>");

        Assert.True(result.Success);
        Assert.Equal("&lt;a href=&#39;x&#39;&gt;café &amp; co&lt;/a&gt;", result.Text);
    }

    [Fact]
    public void Entity_decoding_handles_the_named_table_and_says_when_there_was_nothing()
    {
        // &nbsp; is U+00A0, not a space. Asserting the real character rather than an
        // ordinary one is the difference between testing the decoder and testing that it
        // produced something roughly the right shape.
        Assert.Equal("a\u2014b\u00A0c", Run("html.decodeEntities", "a&mdash;b&nbsp;c").Text);

        var nothing = Run("html.decodeEntities", "plain text");

        Assert.True(nothing.Success);
        Assert.Null(nothing.Text);
        Assert.Contains("no HTML entities", nothing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_encoding_survives_a_round_trip_through_the_decoder()
    {
        const string Original = "if (a < b && c > d) { say(\"it's fine\"); }";

        Assert.Equal(Original, Run("html.decodeEntities", Run("html.encodeEntities", Original).Text!).Text);
    }

    [Fact]
    public void Base64url_encoding_is_unpadded_and_uses_the_url_safe_alphabet()
    {
        // RFC 7515's form — the specification that made this alphabet common. "???~~~" is
        // chosen because standard base64 encodes it as "Pz8/fn5+", which carries both of the
        // two characters the alphabets differ in.
        const string Input = "???~~~";

        Assert.Equal("Pz8/fn5+", Convert.ToBase64String(Encoding.UTF8.GetBytes(Input)));

        var result = Run("base64url.encode", Input);

        Assert.True(result.Success);
        Assert.Equal(FormatId.Base64Url, result.ResultingFormat);
        Assert.Equal("Pz8_fn5-", result.Text);

        // The one decoder handles both alphabets, so the pair still composes.
        Assert.Equal(Input, Run("base64.decode", result.Text!).Text);
    }

    [Theory]
    [InlineData("hash.md5")]
    [InlineData("hash.sha1")]
    [InlineData("hash.sha256")]
    [InlineData("hash.sha512")]
    public void Hashes_are_lower_case_hex(string id)
    {
        // Lower-case hex, because that is what sha256sum, git and openssl print — the tools
        // the answer is going to be compared against. The encoding is pinned separately, in
        // Sha256_matches_the_value_every_other_tool_prints, where a literal can catch it.
        var result = Run(id, "café");

        Assert.True(result.Success);
        Assert.All(result.Text!, static c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    [Fact]
    public void Sha256_matches_the_value_every_other_tool_prints()
    {
        // "abc" is the published SHA-256 test vector, and it is identical under UTF-8,
        // ASCII and Latin-1 — so on its own it says nothing about the encoding.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Run("hash.sha256", "abc").Text);

        // "café" is where the encoding shows. UTF-8 gives the first value; Latin-1, which is
        // what Encoding.Default used to mean, gives a completely different one. Pinning the
        // literal is the only thing that would catch a regression to the wrong encoding —
        // computing the expectation with Encoding.UTF8 here would just agree with whatever
        // the transform did.
        Assert.Equal(
            "850f7dc43910ff890f8879c0ed26fe697c93a067ad93a7d50f466a7028a9bf4e",
            Run("hash.sha256", "café").Text);

        Assert.NotEqual(
            "dafd66c0b98965e688be1fc12942c09f0350e6be0685017c3f234e97d0adc92e",
            Run("hash.sha256", "café").Text);
    }

    [Fact]
    public void The_legacy_hashes_say_so_in_their_names()
    {
        // In the palette, where it is read at the moment someone reaches for one — not in
        // documentation they will not open.
        Assert.Contains("legacy", TransformRegistry.Find("hash.md5")!.Name, StringComparison.Ordinal);
        Assert.Contains("legacy", TransformRegistry.Find("hash.sha1")!.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_guid_replaces_an_empty_tab_and_is_detected_as_one()
    {
        var result = Run("guid.new", string.Empty);

        Assert.True(result.Success);
        Assert.True(Guid.TryParseExact(result.Text, "D", out _));
        Assert.Equal(FormatId.Guid, FormatDetection.Detect(result.Text).Format);
    }

    [Fact]
    public void A_new_guid_is_appended_rather_than_replacing_a_tab_with_notes_in_it()
    {
        // Replacing a buffer full of notes with 36 characters would be consistent with every
        // other transform and completely indefensible.
        var result = Run("guid.new", "some notes\n");

        Assert.True(result.Success);
        Assert.StartsWith("some notes\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("Appended", result.Message, StringComparison.Ordinal);

        // One separator, not two: the buffer already ended in a newline, and appending a
        // blank line every time would turn a list of ten identifiers into nineteen lines.
        Assert.DoesNotContain("\n\n", result.Text, StringComparison.Ordinal);

        var lines = result.Text!.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.True(Guid.TryParseExact(lines[1], "D", out _));
    }

    [Fact]
    public void A_new_guid_replaces_a_selection()
    {
        var transform = TransformRegistry.Find("guid.new");
        var result = transform!.Apply(new TransformInput("PLACEHOLDER", WasSelection: true, TransformOptions.Default));

        Assert.True(result.Success);
        Assert.True(Guid.TryParseExact(result.Text, "D", out _));
    }

    [Fact]
    public void The_generator_is_the_only_transform_that_does_not_need_input()
    {
        // The application refuses to run a transform over an empty tab, so this flag is what
        // keeps "New GUID" reachable in its normal starting case.
        var independent = TransformRegistry.All.Where(static t => !t.NeedsInput).Select(static t => t.Id).ToArray();

        Assert.Equal(new[] { "guid.new" }, independent);
    }

    [Fact]
    public void Iso_and_epoch_are_exact_inverses()
    {
        // Both are suggested for their own format, so Ctrl+Enter pressed twice has to return
        // the value it started with. That is why seconds are emitted when there is no
        // sub-second component: EpochToIso writes .000 for a whole second, and reading that
        // back as milliseconds would multiply the value by a thousand and never come back.
        const string Epoch = "1516239022";

        var iso = Run("time.epochToIso", Epoch);

        Assert.Equal("2018-01-18T01:30:22.000Z", iso.Text);

        var back = Run("time.isoToEpoch", iso.Text!);

        Assert.True(back.Success);
        Assert.Equal(Epoch, back.Text);
        Assert.Equal(FormatId.UnixEpoch, back.ResultingFormat);
    }

    [Fact]
    public void A_timestamp_with_a_fraction_converts_to_milliseconds()
    {
        var result = Run("time.isoToEpoch", "2018-01-18T01:30:22.123Z");

        Assert.Equal("1516239022123", result.Text);
        Assert.Contains("milliseconds", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_is_honoured_rather_than_reinterpreted()
    {
        // The same instant, written three ways. If any of them produced a different number,
        // the answer would depend on the machine's own zone.
        Assert.Equal("1516239022", Run("time.isoToEpoch", "2018-01-18T01:30:22Z").Text);
        Assert.Equal("1516239022", Run("time.isoToEpoch", "2018-01-18T03:30:22+02:00").Text);
        Assert.Equal("1516239022", Run("time.isoToEpoch", "2018-01-17T20:30:22-05:00").Text);
    }

    [Fact]
    public void A_timestamp_with_no_zone_is_read_as_utc_and_says_so()
    {
        var result = Run("time.isoToEpoch", "2018-01-18T01:30:22");

        Assert.Equal("1516239022", result.Text);
        Assert.Contains("read as UTC", result.Message, StringComparison.Ordinal);

        // A bare date carries no zone either, and the dashes in it must not be mistaken for
        // an offset sign — the check that got this wrong first time round.
        Assert.Contains("read as UTC", Run("time.isoToEpoch", "2018-01-18").Message, StringComparison.Ordinal);

        // ...while one that does carry a zone must not claim the assumption was made.
        Assert.DoesNotContain("read as UTC", Run("time.isoToEpoch", "2018-01-18T01:30:22Z").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Converting_to_utc_writes_z_and_keeps_the_instant()
    {
        var result = Run("time.toUtc", "2018-01-18T03:30:22+02:00");

        Assert.True(result.Success);
        Assert.Equal("2018-01-18T01:30:22Z", result.Text);
        Assert.Equal(FormatId.Iso8601, result.ResultingFormat);
    }

    [Fact]
    public void Converting_to_local_time_keeps_the_instant_whatever_the_machine_is_set_to()
    {
        // Asserted against TimeZoneInfo.Local rather than against a fixed offset: the value
        // is machine-dependent by design, and a test that hard-coded one zone would only
        // pass where it was written.
        var result = Run("time.toLocal", "2018-01-18T01:30:22Z");

        Assert.True(result.Success);

        var parsed = DateTimeOffset.Parse(result.Text!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var instant = DateTimeOffset.Parse("2018-01-18T01:30:22Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.Equal(instant.UtcDateTime, parsed.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(instant), parsed.Offset);
    }

    [Fact]
    public void Whole_seconds_do_not_grow_a_fractional_part()
    {
        // Capital F in the format string: the digits and their decimal point disappear
        // together, so a whole second does not come back as .0000000.
        Assert.DoesNotContain(".", Run("time.toUtc", "2018-01-18T01:30:22Z").Text, StringComparison.Ordinal);
    }
}
