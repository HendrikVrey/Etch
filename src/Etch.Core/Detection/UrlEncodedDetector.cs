using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises percent-encoded text.
/// </summary>
/// <remarks>
/// <para>
/// A percent escape is the whole signal. <c>+</c> for space is deliberately not
/// treated as evidence: it appears in ordinary prose and in arithmetic, and a detector
/// that counted it would call half the plain text in the world URL-encoded.
/// </para>
/// <para>
/// A single malformed escape disqualifies the buffer. <c>%</c> followed by anything
/// other than two hex digits is not URL-encoded text with a typo in it — it is text
/// that contains a percent sign, which is far more common.
/// </para>
/// </remarks>
internal sealed class UrlEncodedDetector : IFormatDetector
{
    /// <inheritdoc />
    public FormatId Format => FormatId.UrlEncoded;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        var escapes = 0;

        for (var i = 0; i < sample.Length; i++)
        {
            if (sample[i] != '%')
            {
                continue;
            }

            // A truncated sample can end part-way through an escape. That is the cut
            // point's fault, not the text's.
            if (i + 2 >= sample.Length)
            {
                return !isComplete && escapes > 0 ? DetectionConfidence.Likely : DetectionConfidence.None;
            }

            if (!Sniff.IsHexDigit(sample[i + 1]) || !Sniff.IsHexDigit(sample[i + 2]))
            {
                return DetectionConfidence.None;
            }

            escapes++;
            i += 2;
        }

        if (escapes == 0)
        {
            return DetectionConfidence.None;
        }

        // One escape is a real signal but a thin one — a stray "%20" inside a log line
        // does not make the log line URL-encoded. Several is a value someone encoded.
        return escapes >= 2 && isComplete
            ? DetectionConfidence.Certain
            : DetectionConfidence.Likely;
    }
}
