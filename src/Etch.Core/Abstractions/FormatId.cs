namespace Etch.Core.Abstractions;

/// <summary>
/// A format Etch can recognise in a buffer.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a flat enum rather than a type hierarchy. Detection asks one question
/// — "what is this?" — and every consumer of the answer switches on it, so anything
/// richer would be ceremony around an integer.
/// </para>
/// <para>
/// The numeric values are not a priority order. Ties between equally confident
/// detectors are broken by an explicit list in <c>FormatDetection</c>, because the
/// correct order there ("a JWT is also three base64url segments, so JWT wins") is a
/// judgement that deserves to be written down rather than encoded in the accident of
/// how this enum happens to be sorted.
/// </para>
/// </remarks>
public enum FormatId
{
    /// <summary>Nothing was recognised. The honest default, not a failure.</summary>
    PlainText = 0,

    /// <summary>A JSON document.</summary>
    Json = 1,

    /// <summary>Newline-delimited JSON: one complete JSON value per line.</summary>
    Ndjson = 2,

    /// <summary>Standard base64, with or without padding.</summary>
    Base64 = 3,

    /// <summary>URL-safe base64 (<c>-</c> and <c>_</c> for <c>+</c> and <c>/</c>).</summary>
    Base64Url = 4,

    /// <summary>Hexadecimal bytes, optionally separated by spaces.</summary>
    Hex = 5,

    /// <summary>Percent-encoded text.</summary>
    UrlEncoded = 6,

    /// <summary>A JSON Web Token: three base64url segments separated by dots.</summary>
    Jwt = 7,

    /// <summary>A GUID, in any of the forms .NET round-trips.</summary>
    Guid = 8,

    /// <summary>A Unix timestamp, in seconds or milliseconds.</summary>
    UnixEpoch = 9,

    /// <summary>An ISO-8601 date or timestamp, in extended form.</summary>
    Iso8601 = 10,
}
