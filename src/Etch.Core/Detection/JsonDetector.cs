using System.Text.Json;
using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises a JSON object or array.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only objects and arrays count.</b> A bare <c>42</c> or <c>"hello"</c> is legal
/// JSON and is almost never what someone pasting into a scratchpad meant: <c>42</c>
/// is a Unix timestamp far more often than it is a JSON document. Narrowing here is
/// what stops the format chip from being wrong in the most common case of all.
/// </para>
/// <para>
/// <b>Three answers, not two.</b> A buffer someone is halfway through typing is
/// well-formed-so-far but not valid, and reporting plain text for it would make the
/// chip flicker on every keystroke. So a strict parse gives
/// <see cref="DetectionConfidence.Certain"/>, and text that merely runs out of input
/// gives <see cref="DetectionConfidence.Weak"/>: enough to offer "Format JSON"
/// without claiming the document is valid.
/// </para>
/// </remarks>
internal sealed class JsonDetector : IFormatDetector
{
    /// <summary>
    /// Nesting limit. Untrusted input, and the reader is the only thing standing
    /// between a pasted payload and a stack overflow.
    /// </summary>
    internal const int MaxDepth = 64;

    /// <inheritdoc />
    public FormatId Format => FormatId.Json;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        if (Sniff.FirstMeaningful(sample) is not ('{' or '['))
        {
            return DetectionConfidence.None;
        }

        return Utf8Scratch.Use(sample, isComplete, static (utf8, complete) =>
        {
            // A whole buffer gets the strict reading first, because "this is valid
            // JSON" is a stronger and more useful statement than "this could become
            // valid JSON", and it is the only route to Certain.
            if (complete && Parses(utf8, isFinalBlock: true))
            {
                return DetectionConfidence.Certain;
            }

            if (!Parses(utf8, isFinalBlock: false))
            {
                return DetectionConfidence.None;
            }

            // Everything read was valid and the data simply ran out. For a prefix of a
            // large document that is as good as it gets; for a whole buffer it means
            // the document is still being typed.
            return complete ? DetectionConfidence.Weak : DetectionConfidence.Likely;
        });
    }

    /// <summary>
    /// Reads every token, reporting whether the reader ever objected.
    /// </summary>
    /// <param name="utf8">The transcoded sample.</param>
    /// <param name="isFinalBlock">
    /// False to treat running out of data as an ordinary stop rather than an error,
    /// which is exactly the distinction between "truncated" and "malformed".
    /// </param>
    internal static bool Parses(ReadOnlySpan<byte> utf8, bool isFinalBlock)
    {
        var state = new JsonReaderState(new JsonReaderOptions
        {
            // Generous about what it accepts, because this is a developer's scratchpad
            // and the JSON that arrives in one has come from a config file as often as
            // from an API. Note that formatting such a buffer drops the comments: the
            // transform says so.
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = MaxDepth,
        });

        var reader = new Utf8JsonReader(utf8, isFinalBlock, state);

        try
        {
            while (reader.Read())
            {
                // The tokens themselves do not matter. Reaching the end without the
                // reader objecting is the entire question.
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
