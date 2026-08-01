using System.Text;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Encodings;

/// <summary>
/// Percent-encodes the text.
/// </summary>
/// <remarks>
/// <c>Uri.EscapeDataString</c>, not <c>EscapeUriString</c>: this encodes a value that
/// is going to be put <em>into</em> a URL, so <c>&amp;</c>, <c>=</c> and <c>?</c> must
/// be escaped rather than preserved. Escaping a whole URL is a different job and is
/// almost never the one someone wants from a scratchpad.
/// </remarks>
internal sealed class EncodeUrl : ITransform
{
    /// <inheritdoc />
    public string Id => "url.encode";

    /// <inheritdoc />
    public string Name => "URL encode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["percent encode", "escape", "uri encode"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            return TransformResult.Ok(Uri.EscapeDataString(input.Text), FormatId.UrlEncoded);
        }
        catch (UriFormatException ex)
        {
            return TransformResult.Failed($"Could not URL-encode that: {ex.Message}");
        }
    }
}

/// <summary>Decodes percent-encoded text.</summary>
internal sealed class DecodeUrl : ITransform
{
    /// <inheritdoc />
    public string Id => "url.decode";

    /// <inheritdoc />
    public string Name => "URL decode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["percent decode", "unescape", "uri decode"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.UrlEncoded);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(input.Text);

            // Query strings use + for space and URL paths do not, and nothing in the
            // text says which this is. Decoding + would corrupt every path containing a
            // literal plus; leaving it corrupts nothing and is undone by one more step.
            return TransformResult.Ok(decoded);
        }
        catch (UriFormatException ex)
        {
            return TransformResult.Failed($"That is not valid percent-encoded text: {ex.Message}");
        }
    }
}

/// <summary>
/// Reads hexadecimal bytes back as text.
/// </summary>
/// <remarks>
/// Separators are stripped first, so the colon-delimited form a fingerprint is printed
/// in and the space-delimited form a hex dump uses both work without the user having
/// to tidy them up.
/// </remarks>
internal sealed class DecodeHex : ITransform
{
    /// <inheritdoc />
    public string Id => "hex.decode";

    /// <inheritdoc />
    public string Name => "Hex to text";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["from hex", "unhex", "hex decode"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Hex);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var digits = new char[input.Text.Length];
        var count = 0;

        foreach (var character in input.Text)
        {
            if (char.IsWhiteSpace(character) || character is ':' or '-')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(character))
            {
                return TransformResult.Failed($"'{character}' is not a hexadecimal digit.");
            }

            digits[count++] = character;
        }

        if (count == 0)
        {
            return TransformResult.Failed("There are no hexadecimal digits here.");
        }

        if (count % 2 != 0)
        {
            return TransformResult.Failed($"{count} hexadecimal digits is not a whole number of bytes.");
        }

        var bytes = Convert.FromHexString(digits.AsSpan(0, count));

        try
        {
            return TransformResult.Ok(
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes));
        }
        catch (DecoderFallbackException)
        {
            return TransformResult.Failed($"Those {bytes.Length} bytes are not UTF-8 text.");
        }
    }
}
