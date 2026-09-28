using System.Globalization;
using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises an ISO-8601 date or timestamp in extended form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape here, ranges in the BCL.</b> The scan below proves the text is
/// <c>YYYY-MM-DD</c> optionally followed by a time and a zone, and nothing else; it then
/// hands the string to <see cref="DateTimeOffset.TryParse(ReadOnlySpan{char}, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
/// purely to reject a month of 13 or a 30th of February. Splitting it that way is
/// deliberate: <c>TryParse</c> alone is far too permissive to be a detector (under the
/// invariant culture it happily accepts <c>08/01/2026</c> and <c>Aug 1 2026</c>, neither of
/// which is ISO-8601) while a hand-rolled calendar is a well-known source of leap-year
/// bugs that the framework has already got right.
/// </para>
/// <para>
/// <b>Extended form only.</b> The basic form <c>20260801</c> is legal ISO-8601 and is
/// deliberately rejected: eight digits are also a hex value, a bare number, and the front
/// of a great many identifiers, and there is nothing in the text to say which. The
/// separators are what make this format recognisable, which is exactly why the standard
/// has an extended form.
/// </para>
/// <para>
/// The whole buffer must be the timestamp. A date inside a log line does not make the line
/// a date, and <c>Ctrl+Enter</c>, which does not ask first, would otherwise replace the
/// entire line with a number.
/// </para>
/// </remarks>
internal sealed class Iso8601Detector : IFormatDetector
{
    /// <summary><c>2026-08-01</c>: the shortest thing this accepts.</summary>
    private const int DateLength = 10;

    /// <summary>
    /// Longer than any legal extended-form timestamp, with room to spare.
    /// </summary>
    /// <remarks>
    /// <c>2026-08-01T12:34:56.1234567+02:00</c> is 33 characters. The cap exists so the
    /// scan cannot be handed a 64 KB sample at all, not to police the last few characters.
    /// </remarks>
    private const int MaximumLength = 40;

    /// <summary>Nine, because that is nanoseconds; .NET keeps seven of them.</summary>
    private const int MaximumFractionDigits = 9;

    /// <inheritdoc />
    public FormatId Format => FormatId.Iso8601;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        // A sampled buffer is at least 64 KB, so it is not a timestamp.
        if (!isComplete)
        {
            return DetectionConfidence.None;
        }

        var trimmed = sample.Trim();

        if (trimmed.Length is < DateLength or > MaximumLength)
        {
            return DetectionConfidence.None;
        }

        if (!HasIsoShape(trimmed))
        {
            return DetectionConfidence.None;
        }

        // The same styles the time transforms parse with, and for the same reason: a
        // timestamp carrying no offset has to be read one agreed way, or the detector and the
        // transform disagree at the edges of the representable range and the format chip
        // says something different on a machine in Johannesburg than on one in New York.
        //
        // Note that RoundtripKind, the obvious flag to reach for, is silently discarded by
        // DateTimeOffset.TryParse ("RoundtripKind does not make sense for DateTimeOffset",
        // says the framework's own source), which would leave this parsing zone-less input as
        // machine-local. Passing a flag that does nothing while a comment claims it does is
        // worse than passing none.
        return DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _)
            ? DetectionConfidence.Certain
            : DetectionConfidence.None;
    }

    /// <summary>
    /// True when the span is <c>YYYY-MM-DD</c>, optionally followed by a time and a zone.
    /// </summary>
    /// <remarks>
    /// A straight left-to-right walk rather than a regular expression, matching every other
    /// detector: this runs on the same debounce as the rest and a regex here would be the
    /// only allocation in the whole pass.
    /// </remarks>
    private static bool HasIsoShape(ReadOnlySpan<char> text)
    {
        if (!IsDigits(text[..4]) || text[4] != '-'
            || !IsDigits(text.Slice(5, 2)) || text[7] != '-'
            || !IsDigits(text.Slice(8, 2)))
        {
            return false;
        }

        if (text.Length == DateLength)
        {
            return true;
        }

        // 'T' is the standard's separator and a space is what every database and log
        // formatter emits instead. Both are accepted; nothing else is, because a fourth
        // character here would mean the date is merely the start of some longer string.
        if (text[DateLength] is not ('T' or 't' or ' '))
        {
            return false;
        }

        var rest = text[(DateLength + 1)..];

        // HH:mm is the shortest legal time. Seconds and everything after them are optional.
        if (rest.Length < 5 || !IsDigits(rest[..2]) || rest[2] != ':' || !IsDigits(rest.Slice(3, 2)))
        {
            return false;
        }

        rest = rest[5..];

        if (rest.Length > 0 && rest[0] == ':')
        {
            if (rest.Length < 3 || !IsDigits(rest.Slice(1, 2)))
            {
                return false;
            }

            rest = rest[3..];
        }

        if (rest.Length > 0 && rest[0] == '.')
        {
            // The standard also allows a comma as the decimal separator. It is rejected
            // here on purpose: DateTimeOffset.TryParse does not accept one, so admitting it
            // to the shape test would produce a span this detector calls ISO-8601 and then
            // fails to parse, a disagreement between the two halves of this file.
            var digits = 0;

            while (digits < rest.Length - 1 && char.IsAsciiDigit(rest[digits + 1]))
            {
                digits++;
            }

            if (digits is 0 || digits > MaximumFractionDigits)
            {
                return false;
            }

            rest = rest[(digits + 1)..];
        }

        return IsZone(rest);
    }

    /// <summary>True for an empty zone, <c>Z</c>, or an offset in any of its written forms.</summary>
    /// <remarks>
    /// An absent zone is accepted rather than required. A local timestamp with no offset is
    /// ISO-8601 and is what most application logs print; the transforms are the place to
    /// say what that ambiguity means, not the detector.
    /// </remarks>
    private static bool IsZone(ReadOnlySpan<char> zone) => zone.Length switch
    {
        0 => true,
        1 => zone[0] is 'Z' or 'z',

        // +HH
        3 => IsSign(zone[0]) && IsDigits(zone[1..]),

        // +HHmm
        5 => IsSign(zone[0]) && IsDigits(zone[1..]),

        // +HH:mm
        6 => IsSign(zone[0]) && IsDigits(zone.Slice(1, 2)) && zone[3] == ':' && IsDigits(zone.Slice(4, 2)),

        _ => false,
    };

    private static bool IsSign(char character) => character is '+' or '-';

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
