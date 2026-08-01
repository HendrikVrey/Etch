using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises a GUID.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bare 32-hex form is deliberately not recognised.</b> <c>Guid.TryParse</c>
/// accepts it, but a 32-character hex string is an MD5 hash far more often than it is
/// a GUID, and there is no way to tell them apart — so claiming either would be
/// guessing. The dashed, braced and parenthesised forms are unambiguous, and they are
/// what anything that prints a GUID actually prints.
/// </para>
/// <para>
/// The whole buffer must be the GUID and nothing else. A GUID inside a log line is not
/// a buffer whose format is "GUID", and offering to reformat the entire line as one
/// would be wrong in a way the user would have to undo.
/// </para>
/// </remarks>
internal sealed class GuidDetector : IFormatDetector
{
    /// <summary>The forms that cannot be confused with a hash.</summary>
    private static readonly string[] UnambiguousFormats = ["D", "B", "P"];

    /// <inheritdoc />
    public FormatId Format => FormatId.Guid;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        // A sampled buffer is at least 64 KB, so it is not a GUID.
        if (!isComplete)
        {
            return DetectionConfidence.None;
        }

        var trimmed = sample.Trim();

        foreach (var format in UnambiguousFormats)
        {
            if (Guid.TryParseExact(trimmed, format, out _))
            {
                return DetectionConfidence.Certain;
            }
        }

        return DetectionConfidence.None;
    }
}
