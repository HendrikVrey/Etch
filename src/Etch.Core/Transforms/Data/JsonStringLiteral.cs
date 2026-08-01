using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Data;

/// <summary>
/// Wraps the buffer up as a JSON string literal, quotes and all.
/// </summary>
/// <remarks>
/// What you press when a payload has to go inside another payload: a JSON body in a test
/// fixture, a certificate in an environment variable, an error message in a log record.
/// The quotes are part of the output because a literal without them is not a literal, and
/// the person doing this is about to paste the result somewhere a bare escape sequence
/// would be wrong.
/// </remarks>
internal sealed class EscapeJsonString : ITransform
{
    /// <inheritdoc />
    public string Id => "json.escapeString";

    /// <inheritdoc />
    public string Name => "Escape as JSON string";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["escape", "quote", "stringify", "as string literal"];

    /// <inheritdoc />
    /// <remarks>
    /// Never suggested. Escaping is something you go looking for; the buffer cannot tell
    /// you that its contents are about to be embedded in something else.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(input.Text.Length + 2, 256));

        try
        {
            // The relaxed encoder, for the reason set out in JsonRewriter: the default one
            // escapes every non-ASCII character because its usual job is emitting into an
            // HTML page. Turning "café" into "café" here would be technically correct
            // and completely unhelpful.
            using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            writer.WriteStringValue(input.Text);
            writer.Flush();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Almost always a lone surrogate: Utf8JsonWriter transcodes to UTF-8 as it
            // writes and cannot encode half a code point, so it throws rather than
            // substituting — the same trap JsonDocument.Parse(string) sets, reached from the
            // other direction. A buffer holding one is a normal thing to paste out of a hex
            // viewer or a truncated log, and a transform reports bad input rather than
            // throwing.
            //
            // The framework's message is carried through rather than assumed away, because
            // this filter also catches the writer's length ceiling and its state faults, and
            // telling someone their 200 MB paste contains an unpaired surrogate would send
            // them looking for something that is not there.
            return TransformResult.Failed($"This text could not be written as a JSON string — {ex.Message}");
        }

        return TransformResult.Ok(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}

/// <summary>
/// Turns a JSON string literal back into the text it encodes.
/// </summary>
/// <remarks>
/// The inverse, and the one that gets used more: it is what turns the unreadable
/// <c>"{\"id\":1}"</c> pulled out of a log into something the JSON transforms can then work
/// on. Chaining is the point — unescape, and the buffer is detected as JSON, and
/// <c>Ctrl+Enter</c> formats it.
/// </remarks>
internal sealed class UnescapeJsonString : ITransform
{
    /// <inheritdoc />
    public string Id => "json.unescapeString";

    /// <inheritdoc />
    public string Name => "Unescape JSON string";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["unescape", "unquote", "unstringify", "from string literal"];

    /// <inheritdoc />
    /// <remarks>
    /// Not suggested either. A quoted string is valid JSON, but the JSON detector
    /// deliberately only claims objects and arrays, so nothing detects "a string literal" —
    /// and inventing a detector for it would misfire on every buffer that begins with a
    /// quotation mark.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var trimmed = input.Text.Trim();

        if (trimmed.Length == 0)
        {
            return TransformResult.Failed("There is nothing here to unescape.");
        }

        // Bare escape sequences with no surrounding quotes are the common paste — half a
        // literal, copied out of a log viewer that had already stripped them. Quoting it
        // makes it parseable; a raw quotation mark inside would then fail, which is
        // correct, because that text is not one string literal.
        //
        // The length check leads, so that a buffer holding a single quotation mark is
        // quoted rather than mistaken for a literal whose first and last character happen
        // to be the same one.
        var alreadyQuoted = trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"';
        var literal = alreadyQuoted ? trimmed : string.Concat("\"", trimmed, "\"");

        try
        {
            using var document = JsonDocument.Parse(literal);

            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return TransformResult.Failed("That is not a JSON string literal.");
            }

            return TransformResult.Ok(document.RootElement.GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return TransformResult.Failed($"Not a valid JSON string — {ex.Message}");
        }
    }
}
