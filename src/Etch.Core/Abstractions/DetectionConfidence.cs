namespace Etch.Core.Abstractions;

/// <summary>
/// How sure a detector is that the text is its format.
/// </summary>
/// <remarks>
/// Four levels rather than a boolean, because the interesting cases are the ones in
/// between. A 64 KB prefix of a 4 MB JSON file cannot be fully parsed, so a detector
/// that could only say yes or no would have to say no — and the status bar would call
/// the largest, most useful documents plain text. <see cref="Likely"/> is where those
/// live.
/// </remarks>
public enum DetectionConfidence
{
    /// <summary>Not this format.</summary>
    None = 0,

    /// <summary>
    /// The shape fits, but the evidence is circumstantial and a false positive is
    /// plausible. Enough to offer a transform; not enough to relabel the buffer.
    /// </summary>
    Weak = 1,

    /// <summary>
    /// Everything examined was valid, but not everything could be examined — a
    /// truncated sample, or a format with no terminator to check against.
    /// </summary>
    Likely = 2,

    /// <summary>The whole text was examined and it is unambiguously this format.</summary>
    Certain = 3,
}
