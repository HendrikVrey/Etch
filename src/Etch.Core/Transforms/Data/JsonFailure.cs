using System.Text.Json;
using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Data;

/// <summary>
/// Turns a JSON parse failure into a message and a place to put the caret.
/// </summary>
/// <remarks>
/// <para>
/// One place owns this so that every JSON transform reports a failure the same way. They
/// did not before: three call sites each built their own string out of <c>ex.Message</c>,
/// which is the ordinary way a rule with several homes ends up stated differently in
/// each.
/// </para>
/// <para>
/// The framework's message is kept rather than replaced with something tidier. It already
/// names the line and the position in prose, and rewriting it would mean maintaining a
/// worse copy of a good message. What is added is the
/// <see cref="TransformResult.ErrorOffset"/> beside it, because prose cannot move a caret,
/// and on a minified document, where the whole payload is line one, "line 1, position
/// 20143" is not an answer anybody can act on.
/// </para>
/// </remarks>
internal static class JsonFailure
{
    /// <summary>
    /// Describes <paramref name="exception"/> against the text that produced it.
    /// </summary>
    /// <param name="prefix">
    /// How the sentence starts, e.g. "Not valid JSON". Each transform keeps its own,
    /// because "not valid JSON" and "not a valid JSON string" are genuinely different
    /// findings.
    /// </param>
    /// <param name="exception">The failure. Only a <see cref="JsonException"/> carries a position.</param>
    /// <param name="parsedText">The exact text handed to the parser, for resolving that position.</param>
    /// <param name="shift">
    /// Added to the resolved offset to move it from <paramref name="parsedText"/>'s
    /// coordinates into the caller's. Non-zero only when the two differ: see
    /// <see cref="UnescapeJsonString"/>, which parses a trimmed and quoted copy of the
    /// buffer rather than the buffer itself.
    /// </param>
    /// <param name="limit">
    /// The length of the text the offset will be used against, when that is not
    /// <paramref name="parsedText"/>. An offset outside it is dropped rather than
    /// clamped: a position that does not fall inside the buffer is evidence the mapping
    /// is wrong, and a confidently wrong caret is worse than none.
    /// </param>
    public static TransformResult Describe(
        string prefix,
        Exception exception,
        string parsedText,
        int shift = 0,
        int? limit = null)
    {
        var message = $"{prefix} - {exception.Message}";

        // ArgumentException, a lone surrogate failing to transcode before the parser has
        // an opinion, carries no position at all, and neither does a JsonException built
        // without one. Null is the honest answer; an invented zero would send the caret to
        // the top of the document for no reason.
        if (exception is not JsonException { LineNumber: { } line, BytePositionInLine: { } bytePosition }
            || !Utf8Position.TryResolve(parsedText, line, bytePosition, out var offset))
        {
            return TransformResult.Failed(message);
        }

        var shifted = (long)offset + shift;
        var ceiling = limit ?? parsedText.Length;

        return TransformResult.Failed(
            message,
            shifted >= 0 && shifted <= ceiling ? (int)shifted : null);
    }
}
