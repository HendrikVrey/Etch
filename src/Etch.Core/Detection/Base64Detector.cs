using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>Recognises standard base64.</summary>
internal sealed class Base64Detector : IFormatDetector
{
    /// <inheritdoc />
    public FormatId Format => FormatId.Base64;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete) =>
        Base64Shape.Detect(sample, isComplete, urlSafe: false);
}

/// <summary>Recognises URL-safe base64.</summary>
internal sealed class Base64UrlDetector : IFormatDetector
{
    /// <inheritdoc />
    public FormatId Format => FormatId.Base64Url;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete) =>
        Base64Shape.Detect(sample, isComplete, urlSafe: true);
}

/// <summary>
/// The shared shape test behind both base64 detectors.
/// </summary>
/// <remarks>
/// <para>
/// Base64 is the format most able to embarrass a detector, because a great deal of
/// ordinary English is also valid base64. Three rules do the work:
/// </para>
/// <list type="bullet">
/// <item><b>No interior spaces or tabs.</b> Base64 in the wild is wrapped with
/// newlines (by PEM, by MIME, by every shell tool that emits it) and never broken
/// with spaces. Refusing them is what stops a plain English sentence from being
/// offered a "decode base64" as its top-ranked action.</item>
/// <item><b>A length that works.</b> Sixteen significant characters minimum, and a
/// total that a base64 group boundary can actually land on.</item>
/// <item><b>Evidence beyond the alphabet.</b> Padding is conclusive; a mixture of
/// digits and both letter cases is suggestive; neither is only ever
/// <see cref="DetectionConfidence.Weak"/>.</item>
/// </list>
/// </remarks>
internal static class Base64Shape
{
    /// <summary>Shorter than this and the alphabet proves nothing at all.</summary>
    private const int MinimumSymbols = 16;

    /// <summary>Classifies a sample against one of the two alphabets.</summary>
    public static DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete, bool urlSafe)
    {
        var symbols = 0;
        var padding = 0;
        var hasDigit = false;
        var hasUpper = false;
        var hasLower = false;
        var hasAlphabetMarker = false;
        var started = false;
        var gapAfterStart = false;

        foreach (var character in sample)
        {
            if (character is '\r' or '\n')
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                // Leading and trailing whitespace is somebody's newline habit, not part
                // of the value. Anything between the first and last symbol is not.
                //
                // Recorded as a flag rather than answered by scanning ahead: a lookahead
                // here runs once per whitespace character, which on a 64 KB sample with a
                // long run of trailing spaces is quadratic against a 15 ms budget.
                gapAfterStart |= started;
                continue;
            }

            if (gapAfterStart)
            {
                // A symbol after a gap. This is a sentence, not a value.
                return DetectionConfidence.None;
            }

            if (padding > 0 && character != '=')
            {
                // Something after the padding. Not a base64 value with a tidy end.
                return DetectionConfidence.None;
            }

            started = true;

            switch (character)
            {
                case '=':
                    padding++;
                    continue;

                case '+' or '/' when !urlSafe:
                case '-' or '_' when urlSafe:
                    hasAlphabetMarker = true;
                    break;

                default:
                    if (!char.IsAsciiLetterOrDigit(character))
                    {
                        return DetectionConfidence.None;
                    }

                    hasDigit |= char.IsAsciiDigit(character);
                    hasUpper |= char.IsAsciiLetterUpper(character);
                    hasLower |= char.IsAsciiLetterLower(character);
                    break;
            }

            symbols++;
        }

        if (symbols < MinimumSymbols || padding > 2)
        {
            return DetectionConfidence.None;
        }

        // A truncated sample was cut at an arbitrary byte, so its length carries no
        // information and the group-boundary test has to be skipped rather than failed.
        if (isComplete && (symbols + padding) % 4 != 0)
        {
            return DetectionConfidence.None;
        }

        if (padding > 0)
        {
            return isComplete ? DetectionConfidence.Certain : DetectionConfidence.Likely;
        }

        var looksEncoded = hasAlphabetMarker || (hasDigit && hasUpper && hasLower);

        return looksEncoded ? DetectionConfidence.Likely : DetectionConfidence.Weak;
    }
}
