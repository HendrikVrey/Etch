using System.Globalization;
using System.Text;
using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Identity;

/// <summary>
/// Expands a JSON Web Token into its header and payload.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decode only. The signature is never verified, and the output says so.</b> That
/// banner is not decoration — a tool that renders a token's claims as though they were
/// established facts is teaching its users to trust attacker-controlled input, and the
/// people using a JWT decoder are exactly the people who will act on what it shows
/// them. The plan makes this a security requirement; it is implemented as the first
/// line of the output, where it cannot be scrolled past before the claims are read.
/// </para>
/// <para>
/// The result is a comment-annotated document rather than strict JSON, which means it
/// cannot be chained into the JSON transforms. That is the right trade: the value here
/// is in reading the token, and a caller that wants the payload alone can select it.
/// </para>
/// </remarks>
internal sealed class DecodeJwt : ITransform
{
    /// <inheritdoc />
    public string Id => "jwt.decode";

    /// <inheritdoc />
    public string Name => "Decode JWT";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Identity;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["token", "jwt", "bearer", "claims"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Jwt);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        if (!Jwt.TryParse(input.Text, out var token))
        {
            return TransformResult.Failed(
                "That is not a JWT — a token is three dot-separated parts whose first two decode to JSON.");
        }

        var newLine = input.Options.NewLine;
        var builder = new StringBuilder(input.Text.Length * 2);

        builder.Append("// SIGNATURE NOT VERIFIED — these values are only decoded, not trusted.").Append(newLine);
        builder.Append(CultureInfo.InvariantCulture, $"// Algorithm: {token.Algorithm}").Append(newLine);
        builder.Append(newLine);
        builder.Append("// Header").Append(newLine);
        builder.Append(LineEndings.Normalise(token.Header, newLine)).Append(newLine);
        builder.Append(newLine);
        builder.Append("// Payload").Append(newLine);
        builder.Append(LineEndings.Normalise(token.Payload, newLine)).Append(newLine);

        AppendTimestamps(builder, token.Payload, newLine);

        return TransformResult.Ok(builder.ToString());
    }

    /// <summary>
    /// Renders <c>exp</c>, <c>iat</c> and <c>nbf</c> as dates.
    /// </summary>
    /// <remarks>
    /// The single most common reason to decode a token is to find out whether it has
    /// expired, and <c>1763058000</c> does not answer that question. Best-effort: a
    /// token whose claims are shaped unusually still gets its header and payload.
    /// </remarks>
    private static void AppendTimestamps(StringBuilder builder, string payload, string newLine)
    {
        ReadOnlySpan<string> claims = ["iat", "nbf", "exp"];
        var wrote = false;

        using var document = System.Text.Json.JsonDocument.Parse(payload);

        foreach (var claim in claims)
        {
            // ValueKind is checked before TryGetInt64, which only suppresses malformed
            // *numbers* — on a string it throws. Tokens in the wild really do carry
            // "exp":"1516239022", and this decoder's whole job is reading input nobody
            // vouched for.
            if (!document.RootElement.TryGetProperty(claim, out var element)
                || element.ValueKind != System.Text.Json.JsonValueKind.Number
                || !element.TryGetInt64(out var seconds))
            {
                continue;
            }

            DateTimeOffset moment;

            try
            {
                moment = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (!wrote)
            {
                builder.Append(newLine).Append("// Times").Append(newLine);
                wrote = true;
            }

            builder
                .Append(CultureInfo.InvariantCulture, $"// {claim}: {moment.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC")
                .Append(newLine);
        }
    }
}
