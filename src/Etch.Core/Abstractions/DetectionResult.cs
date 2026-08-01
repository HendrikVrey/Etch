namespace Etch.Core.Abstractions;

/// <summary>
/// What the detection pass concluded about a buffer.
/// </summary>
/// <param name="Format">The format decided on, or <see cref="FormatId.PlainText"/>.</param>
/// <param name="Confidence">How sure the winning detector was.</param>
/// <param name="WasSampled">
/// True when only a prefix of the buffer was examined. The reason
/// <see cref="Confidence"/> can be <see cref="DetectionConfidence.Likely"/> rather
/// than <see cref="DetectionConfidence.Certain"/> for a perfectly valid document, and
/// worth surfacing so a status bar can say "JSON (sampled)" rather than implying the
/// whole buffer was checked.
/// </param>
/// <remarks>
/// A struct, and small on purpose: it is passed to <see cref="ITransform.IsAvailable"/>
/// for every registered transform each time the palette opens.
/// </remarks>
public readonly record struct DetectionResult(
    FormatId Format,
    DetectionConfidence Confidence,
    bool WasSampled = false)
{
    /// <summary>Nothing was recognised.</summary>
    public static DetectionResult PlainText { get; } =
        new(FormatId.PlainText, DetectionConfidence.None);

    /// <summary>True when a format was recognised at all.</summary>
    public bool IsRecognised => Confidence > DetectionConfidence.None && Format != FormatId.PlainText;

    /// <summary>True when this result names <paramref name="format"/> with any confidence.</summary>
    public bool Is(FormatId format) => IsRecognised && Format == format;
}
