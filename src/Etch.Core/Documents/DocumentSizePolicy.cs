using System.Globalization;

namespace Etch.Core.Documents;

/// <summary>
/// Maps a document size to the set of editor features it may use.
/// </summary>
/// <remarks>
/// Thresholds are configurable but ship with defaults chosen from the Etch plan
/// (section 10). The boundaries are inclusive-below: a document of exactly
/// <see cref="ReducedThreshold"/> bytes is still <see cref="DocumentTier.Full"/>.
/// </remarks>
public sealed class DocumentSizePolicy
{
    private const long Mebibyte = 1024L * 1024L;

    /// <summary>The shipped defaults: 2 MiB, 10 MiB, 100 MiB.</summary>
    public static DocumentSizePolicy Default { get; } =
        new(reducedThreshold: 2 * Mebibyte, plainTextThreshold: 10 * Mebibyte, hardCeiling: 100 * Mebibyte);

    /// <summary>
    /// Creates a policy.
    /// </summary>
    /// <param name="reducedThreshold">Size above which folding is disabled.</param>
    /// <param name="plainTextThreshold">Size above which highlighting and journaling are disabled.</param>
    /// <param name="hardCeiling">Size above which the document is not opened in the editor.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a threshold is not positive, or the thresholds are not strictly ascending.
    /// Silently accepting a nonsensical policy would produce degradation rules nobody could reason about.
    /// </exception>
    public DocumentSizePolicy(long reducedThreshold, long plainTextThreshold, long hardCeiling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reducedThreshold);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(plainTextThreshold, reducedThreshold);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hardCeiling, plainTextThreshold);

        ReducedThreshold = reducedThreshold;
        PlainTextThreshold = plainTextThreshold;
        HardCeiling = hardCeiling;
    }

    /// <summary>Above this size, folding is off and detection stops re-running on edit.</summary>
    public long ReducedThreshold { get; }

    /// <summary>Above this size, highlighting is off and the buffer is not journaled.</summary>
    public long PlainTextThreshold { get; }

    /// <summary>Above this size, the document is refused.</summary>
    public long HardCeiling { get; }

    /// <summary>
    /// Classifies a document by size in bytes.
    /// </summary>
    /// <param name="sizeInBytes">The document size. Must not be negative.</param>
    public DocumentCapabilities Evaluate(long sizeInBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        if (sizeInBytes > HardCeiling)
        {
            return new DocumentCapabilities(
                DocumentTier.Rejected,
                CanOpen: false,
                SyntaxHighlighting: false,
                Folding: false,
                DetectOnEdit: false,
                Journaling: false,
                Notice: $"{Describe(sizeInBytes)} exceeds the {Describe(HardCeiling)} editor limit.");
        }

        if (sizeInBytes > PlainTextThreshold)
        {
            return new DocumentCapabilities(
                DocumentTier.PlainText,
                CanOpen: true,
                SyntaxHighlighting: false,
                Folding: false,
                DetectOnEdit: false,
                Journaling: false,
                Notice: $"Plain-text mode - {Describe(sizeInBytes)} file, highlighting and auto-save off.");
        }

        if (sizeInBytes > ReducedThreshold)
        {
            return new DocumentCapabilities(
                DocumentTier.Reduced,
                CanOpen: true,
                SyntaxHighlighting: true,
                Folding: false,
                DetectOnEdit: false,
                Journaling: true,
                Notice: $"Large file ({Describe(sizeInBytes)}) - folding off.");
        }

        return new DocumentCapabilities(
            DocumentTier.Full,
            CanOpen: true,
            SyntaxHighlighting: true,
            Folding: true,
            DetectOnEdit: true,
            Journaling: true,
            Notice: null);
    }

    /// <summary>
    /// Formats a byte count for a status-bar message ("9.4 MB", "512 KB", "27 bytes").
    /// Binary units, decimal-ish labels — matching what Windows itself shows.
    /// </summary>
    /// <remarks>
    /// Invariant culture: this string goes into diagnostic logs that get compared
    /// across machines, and a decimal comma would make those diffs noise.
    /// </remarks>
    public static string Describe(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        return bytes switch
        {
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
            < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB"),
        };
    }
}
