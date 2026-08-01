using System.Globalization;
using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises a Unix timestamp, in seconds or milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// Digit count does the work, and it is enough: ten digits is a second-precision
/// timestamp somewhere between 2001 and 2286, thirteen is the same window in
/// milliseconds, and both windows comfortably contain every timestamp a developer is
/// going to paste into a scratchpad. Neither range can be confused with the other.
/// </para>
/// <para>
/// The buffer has to be nothing but the number. This is the detector most likely to
/// fire on something that merely contains a number, so it does not get to look inside
/// anything.
/// </para>
/// </remarks>
internal sealed class UnixEpochDetector : IFormatDetector
{
    /// <summary>Ten digits: 2001-09-09 through 2286-11-20, in seconds.</summary>
    private const int SecondsDigits = 10;

    /// <summary>Thirteen digits: the same window, in milliseconds.</summary>
    private const int MillisecondsDigits = 13;

    /// <inheritdoc />
    public FormatId Format => FormatId.UnixEpoch;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        if (!isComplete)
        {
            return DetectionConfidence.None;
        }

        var trimmed = sample.Trim();

        if (trimmed.Length is < 9 or > MillisecondsDigits)
        {
            return DetectionConfidence.None;
        }

        foreach (var character in trimmed)
        {
            if (!char.IsAsciiDigit(character))
            {
                return DetectionConfidence.None;
            }
        }

        // Parsed rather than merely counted, so that a number too large for a timestamp
        // is rejected here instead of surfacing later as an out-of-range failure.
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return DetectionConfidence.None;
        }

        // Anything between the two exact widths is a number of the right shape but not
        // of a recognisable precision — worth offering, not worth asserting.
        return trimmed.Length is SecondsDigits or MillisecondsDigits
            ? DetectionConfidence.Certain
            : DetectionConfidence.Weak;
    }
}
