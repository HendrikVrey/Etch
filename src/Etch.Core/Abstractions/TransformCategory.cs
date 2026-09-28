namespace Etch.Core.Abstractions;

/// <summary>
/// The grouping a transform appears under in the palette.
/// </summary>
/// <remarks>
/// Presentation only. Nothing routes on this: a category that decided behaviour would
/// be a second, weaker copy of <see cref="ITransform.IsAvailable"/>.
/// </remarks>
public enum TransformCategory
{
    /// <summary>JSON and structured data.</summary>
    Data = 0,

    /// <summary>Base64, hex, URL and entity encodings.</summary>
    Encoding = 1,

    /// <summary>Hashes, GUIDs, tokens.</summary>
    Identity = 2,

    /// <summary>Timestamps and durations.</summary>
    Time = 3,

    /// <summary>Line and case operations that apply to any text.</summary>
    Text = 4,
}
