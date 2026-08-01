using System.Text;
using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Encodings;

/// <summary>Encodes the text as standard base64.</summary>
internal sealed class EncodeBase64 : ITransform
{
    /// <inheritdoc />
    public string Id => "base64.encode";

    /// <inheritdoc />
    public string Name => "Base64 encode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["b64", "to base64"];

    /// <inheritdoc />
    /// <remarks>
    /// Never the suggested action. Encoding is something you go looking for; decoding is
    /// something the buffer tells you it needs. Offering "encode" as the top-ranked
    /// action for a base64 string — the one case where it is actively wrong — is exactly
    /// what ranking by availability is for.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default) =>
        TransformResult.Ok(
            Base64Text.Encode(Encoding.UTF8.GetBytes(input.Text)),
            FormatId.Base64);
}

/// <summary>
/// Encodes the text as URL-safe base64, unpadded.
/// </summary>
/// <remarks>
/// <para>
/// A separate transform from <see cref="EncodeBase64"/>, where decoding is deliberately
/// one. Decoding can tell the alphabets apart from the input, so asking the user which one
/// they have would be making them do work the tool has already done. Encoding cannot: only
/// the person knows whether the output is going into a URL, and getting it wrong is
/// silent — standard base64's <c>+</c> and <c>/</c> travel through a query string looking
/// fine and arrive as a space and a path separator.
/// </para>
/// <para>
/// Unpadded, because RFC 7515 — the specification that made this alphabet common — says so.
/// </para>
/// </remarks>
internal sealed class EncodeBase64Url : ITransform
{
    /// <inheritdoc />
    public string Id => "base64url.encode";

    /// <inheritdoc />
    public string Name => "Base64url encode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["url-safe base64", "b64url", "to base64url", "jwt segment"];

    /// <inheritdoc />
    /// <remarks>Never suggested, for the same reason as standard encoding.</remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default) =>
        TransformResult.Ok(
            Base64Text.EncodeUrl(Encoding.UTF8.GetBytes(input.Text)),
            FormatId.Base64Url);
}

/// <summary>
/// Decodes base64 or base64url back to text.
/// </summary>
/// <remarks>
/// One transform for both alphabets. They differ in two characters, the decoder
/// handles either, and a palette with "Base64 decode" and "Base64url decode" sitting
/// next to each other would make the user do a classification the tool has already
/// done.
/// </remarks>
internal sealed class DecodeBase64 : ITransform
{
    /// <inheritdoc />
    public string Id => "base64.decode";

    /// <inheritdoc />
    public string Name => "Base64 decode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["b64", "from base64", "base64url", "unbase64"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) =>
        detection.Is(FormatId.Base64) || detection.Is(FormatId.Base64Url);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        if (!Base64Text.TryDecode(input.Text, out var bytes) || bytes is null)
        {
            return TransformResult.Failed("That is not valid base64.");
        }

        // Strict decoding, so that binary is reported rather than silently replaced with
        // replacement characters. Decoding a PNG into a buffer full of U+FFFD would
        // destroy it, and the user would only find out by pasting it somewhere.
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);

            return TransformResult.Ok(text);
        }
        catch (DecoderFallbackException)
        {
            return TransformResult.Failed(
                $"That decodes to {bytes.Length} bytes of binary, not text.");
        }
    }
}
