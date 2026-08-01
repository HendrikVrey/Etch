using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Data;

/// <summary>
/// The parse-and-rewrite shared by every JSON transform.
/// </summary>
/// <remarks>
/// <para>
/// One place owns the reader options, the writer options and the error message, so
/// that "format" and "minify" cannot disagree about what counts as JSON or about how
/// a failure reads.
/// </para>
/// <para>
/// <b>The encoder is relaxed on purpose.</b> <c>Utf8JsonWriter</c> defaults to escaping
/// every non-ASCII character and several ASCII ones, because its usual job is emitting
/// JSON into an HTML page. Etch's job is putting text back in a text editor, and a
/// "format" that silently turns <c>"café"</c> into <c>"café"</c> has corrupted the
/// document as far as its author is concerned. There is no HTML context here to
/// protect.
/// </para>
/// </remarks>
internal static class JsonRewriter
{
    /// <summary>
    /// Nesting limit, matching the detector's. Untrusted input, and the depth cap is
    /// also what bounds the recursion in <see cref="WriteSorted"/>.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>Reads <paramref name="text"/> and writes it back through <paramref name="write"/>.</summary>
    /// <param name="text">The candidate JSON.</param>
    /// <param name="options">Editor settings the output must match.</param>
    /// <param name="indented">Whether to write with indentation.</param>
    /// <param name="write">Emits the parsed document. Sorting hooks in here.</param>
    public static TransformResult Rewrite(
        string text,
        TransformOptions options,
        bool indented,
        Action<JsonElement, Utf8JsonWriter> write)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    // The same generosity the detector applies: JSON reaching a
                    // developer's scratchpad has come from a commented config file at
                    // least as often as from an API.
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = MaxDepth,
                });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException as well as JsonException: Parse(string) transcodes to
            // UTF-8 first, and a lone surrogate in the buffer fails there — before the
            // parser has an opinion — with "Cannot transcode invalid UTF-16". A transform
            // is contracted to report bad input by returning a failure, and pasted text
            // is exactly where a stray surrogate comes from.
            //
            // The framework's message already names the line and position, which is the
            // actionable part. Passed through rather than replaced with something tidier
            // and less useful.
            return TransformResult.Failed($"Not valid JSON — {ex.Message}");
        }

        using (document)
        {
            var buffer = new ArrayBufferWriter<byte>(Math.Max(text.Length, 256));

            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = indented,
                IndentCharacter = ' ',
                IndentSize = options.IndentSize,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                write(document.RootElement, writer);
            }

            var written = Encoding.UTF8.GetString(buffer.WrittenSpan);

            return TransformResult.Ok(
                indented ? LineEndings.Normalise(written, options.NewLine) : written,
                FormatId.Json);
        }
    }

    /// <summary>Writes an element with every object's properties in ordinal order.</summary>
    /// <remarks>
    /// <para>
    /// Ordinal, not culture-aware. Sorting keys is something people do to make two
    /// documents comparable, and a comparison that changes with the machine's locale is
    /// worse than no sort at all.
    /// </para>
    /// <para>
    /// Recursion is bounded by <see cref="MaxDepth"/>, which the parse has already
    /// enforced — a document deep enough to overflow the stack here could not have been
    /// parsed in the first place.
    /// </para>
    /// </remarks>
    public static void WriteSorted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    // Array order is data, not presentation. Sorting it would change
                    // what the document means.
                    WriteSorted(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
