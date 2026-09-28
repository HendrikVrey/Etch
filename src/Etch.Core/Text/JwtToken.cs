using System.Text;
using System.Text.Json;

namespace Etch.Core.Text;

/// <summary>
/// A JSON Web Token, split into its parts and decoded.
/// </summary>
/// <param name="Header">The decoded header JSON.</param>
/// <param name="Payload">The decoded payload JSON.</param>
/// <param name="Signature">
/// The signature segment, still encoded, and <b>never verified</b>. Etch decodes
/// tokens; it does not validate them, and anything built on this must say so where the
/// user can see it. A tool that displays a token's claims without that caveat teaches
/// people to trust unverified input, which is the entire attack.
/// </param>
/// <param name="Algorithm">The <c>alg</c> header value, for display.</param>
public readonly record struct JwtToken(string Header, string Payload, string Signature, string Algorithm);

/// <summary>Reads a JWT without verifying it.</summary>
public static class Jwt
{
    /// <summary>
    /// A ceiling on each segment, in characters.
    /// </summary>
    /// <remarks>
    /// Untrusted input. A real token's segments are a few hundred characters; this is
    /// generous by two orders of magnitude and still stops a megabyte of base64 with
    /// two dots in it from being decoded three times over on every debounce.
    /// </remarks>
    private const int MaxSegmentLength = 64 * 1024;

    /// <summary>
    /// Splits and decodes <paramref name="text"/> as a JWT.
    /// </summary>
    /// <param name="text">The candidate token.</param>
    /// <param name="token">The decoded token, when this returns true.</param>
    /// <returns>
    /// True when the text is three dot-separated base64url segments whose first two
    /// decode to JSON objects and whose header carries an <c>alg</c>.
    /// </returns>
    public static bool TryParse(ReadOnlySpan<char> text, out JwtToken token)
    {
        token = default;

        var trimmed = text.Trim();

        var firstDot = trimmed.IndexOf('.');

        if (firstDot <= 0)
        {
            return false;
        }

        var rest = trimmed[(firstDot + 1)..];
        var secondDot = rest.IndexOf('.');

        if (secondDot <= 0)
        {
            return false;
        }

        var headerText = trimmed[..firstDot];
        var payloadText = rest[..secondDot];
        var signatureText = rest[(secondDot + 1)..];

        // Exactly two dots. A fourth segment means this is JWE or something else
        // entirely, and decoding the first three of five parts would be misleading.
        if (signatureText.Contains('.')
            || headerText.Length > MaxSegmentLength
            || payloadText.Length > MaxSegmentLength)
        {
            return false;
        }

        if (!TryDecodeObject(headerText, out var header) || !TryDecodeObject(payloadText, out var payload))
        {
            return false;
        }

        if (!TryReadAlgorithm(header, out var algorithm))
        {
            return false;
        }

        token = new JwtToken(header, payload, signatureText.ToString(), algorithm);
        return true;
    }

    /// <summary>Decodes one segment and requires it to be a JSON object.</summary>
    /// <remarks>
    /// An object specifically, not any JSON value. Both of a token's decodable segments
    /// are objects by definition, and requiring it is most of what stops an ordinary
    /// dotted base64 string from being read as a token.
    /// </remarks>
    private static bool TryDecodeObject(ReadOnlySpan<char> segment, out string json)
    {
        json = string.Empty;

        if (segment.IsEmpty || !Base64Text.TryDecode(segment, out var bytes) || bytes is null)
        {
            return false;
        }

        // No try around this: Encoding.UTF8 uses the replacement fallback, so invalid
        // bytes become U+FFFD rather than an exception, and a segment that decoded to
        // replacement characters will fail the JSON parse below anyway.
        var text = Encoding.UTF8.GetString(bytes);

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        json = text;
        return true;
    }

    /// <summary>Reads the <c>alg</c> header, which every JWS header is required to carry.</summary>
    private static bool TryReadAlgorithm(string header, out string algorithm)
    {
        algorithm = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(header, new JsonDocumentOptions { MaxDepth = 64 });

            if (!document.RootElement.TryGetProperty("alg", out var element)
                || element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            algorithm = element.GetString() ?? string.Empty;
            return algorithm.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
