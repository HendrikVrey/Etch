using System.Globalization;
using System.Text.Json;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Data;

/// <summary>
/// Says whether the buffer is JSON, and where it stops being JSON.
/// </summary>
/// <remarks>
/// <para>
/// The one transform that deliberately writes nothing. Formatting already fails with the
/// parser's position when a document is broken, so the value here is the <em>other</em>
/// answer: confirming that a document is fine without reformatting it. Someone checking a
/// file they are about to commit does not want its indentation changed as a side effect of
/// asking.
/// </para>
/// <para>
/// It concedes the suggested slot, <see cref="Precedence"/> is behind
/// <see cref="FormatJson"/>'s, because <c>Ctrl+Enter</c> should do something to a JSON
/// buffer, and "it is valid" is what the format chip already says.
/// </para>
/// </remarks>
internal sealed class ValidateJson : ITransform
{
    /// <summary>Matches <see cref="JsonRewriter"/>'s. Untrusted input gets a bounded parse.</summary>
    private const int MaxDepth = 64;

    /// <inheritdoc />
    public string Id => "json.validate";

    /// <inheritdoc />
    public string Name => "Validate JSON";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Data;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["check", "lint", "verify", "is this valid"];

    /// <inheritdoc />
    /// <remarks>Behind every JSON transform that actually changes something.</remarks>
    public int Precedence => 100;

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Json);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = JsonDocument.Parse(
                input.Text,
                new JsonDocumentOptions
                {
                    // Strict where the rewriters are generous, and that difference is the
                    // point of having a separate validator: "Etch can format this" and
                    // "this is JSON" are different questions, and a file with trailing
                    // commas is a yes to the first and a no to the second. Anything that
                    // rejects it (a parser in production, most likely) is going to be
                    // stricter than a scratchpad.
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaxDepth,
                });

            return TransformResult.Reported($"Valid JSON - {Describe(document.RootElement)}.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Same two exception types as JsonRewriter, for the same reason: Parse(string)
            // transcodes to UTF-8 before parsing, so a lone surrogate raises
            // ArgumentException rather than JsonException.
            //
            // This is the transform whose whole job is saying where the document stops
            // being JSON, so it is the one the offset matters most for.
            return JsonFailure.Describe("Not valid JSON", ex, input.Text);
        }
    }

    /// <summary>Describes the root in the terms someone validating actually wants.</summary>
    /// <remarks>
    /// A count, not just a "yes". The common reason to validate a payload is to check that
    /// it holds what it should, and "Valid JSON: an object with 3 keys" answers a
    /// truncated-response question that a bare "valid" does not.
    /// </remarks>
    private static string Describe(JsonElement root) => root.ValueKind switch
    {
        JsonValueKind.Object => Count(CountProperties(root), "an object with {0} key{1}"),
        JsonValueKind.Array => Count(root.GetArrayLength(), "an array of {0} item{1}"),
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "a value",
    };

    private static int CountProperties(JsonElement root)
    {
        var count = 0;

        foreach (var _ in root.EnumerateObject())
        {
            count++;
        }

        return count;
    }

    private static string Count(int value, string shape) =>
        string.Format(CultureInfo.InvariantCulture, shape, value, value == 1 ? string.Empty : "s");
}
