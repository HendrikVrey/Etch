namespace Etch.Core.Abstractions;

/// <summary>
/// Recognises one format in a sample of text.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be pure, allocation-light and fast enough to run on every
/// registered detector for a 64 KB sample inside the plan's 15 ms budget. They take a
/// <see cref="ReadOnlySpan{T}"/> precisely so that no implementation can quietly copy
/// the buffer.
/// </para>
/// <para>
/// The sample may be a <em>prefix</em> of the real text. A detector that needs to see
/// a closing brace to be sure must return <see cref="DetectionConfidence.Likely"/> in
/// that case rather than <see cref="DetectionConfidence.None"/>: declaring a 4 MB
/// JSON file to be plain text because its end was never read is the failure this whole
/// interface is shaped around.
/// </para>
/// </remarks>
public interface IFormatDetector
{
    /// <summary>The format this detector recognises.</summary>
    FormatId Format { get; }

    /// <summary>
    /// Examines <paramref name="sample"/> and reports how sure it is.
    /// </summary>
    /// <param name="sample">The text to examine, already trimmed of leading whitespace.</param>
    /// <param name="isComplete">
    /// True when <paramref name="sample"/> is the entire buffer rather than a prefix.
    /// Only <see cref="DetectionConfidence.Certain"/> may be returned when it is true.
    /// </param>
    DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete);
}
