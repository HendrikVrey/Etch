using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises a JSON Web Token.
/// </summary>
/// <remarks>
/// The one detector whose answer is worth real work: it decodes two of the three
/// segments and requires both to be JSON objects with an <c>alg</c> in the header.
/// That is expensive by the standards of this pass, and it is affordable because the
/// cheap shape test in front of it (exactly two dots, base64url alphabet, plausible
/// length) rules out essentially every buffer before the decode is reached.
/// </remarks>
internal sealed class JwtDetector : IFormatDetector
{
    /// <summary>
    /// The shortest thing that could be a token: two tiny JSON objects and a signature.
    /// </summary>
    private const int MinimumLength = 20;

    /// <inheritdoc />
    public FormatId Format => FormatId.Jwt;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        // A token is one line and well under the sample limit, so a sampled buffer is
        // not one, and running the decode on a truncated tail would fail anyway.
        if (!isComplete)
        {
            return DetectionConfidence.None;
        }

        var trimmed = sample.Trim();

        if (trimmed.Length < MinimumLength || !HasTokenShape(trimmed))
        {
            return DetectionConfidence.None;
        }

        // No middle ground. Either both segments decoded to JSON objects carrying an
        // algorithm, in which case this is a token, or it is something else.
        return Jwt.TryParse(trimmed, out _) ? DetectionConfidence.Certain : DetectionConfidence.None;
    }

    /// <summary>The cheap gate: base64url characters and exactly two dots.</summary>
    private static bool HasTokenShape(ReadOnlySpan<char> text)
    {
        var dots = 0;

        foreach (var character in text)
        {
            if (character == '.')
            {
                if (++dots > 2)
                {
                    return false;
                }

                continue;
            }

            if (!Sniff.IsBase64UrlSymbol(character) && character != '=')
            {
                return false;
            }
        }

        return dots == 2;
    }
}
