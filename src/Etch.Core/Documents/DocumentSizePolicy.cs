using System.Globalization;

namespace Etch.Core.Documents;

/// <summary>
/// Maps a document size to the set of editor features it may use.
/// </summary>
/// <remarks>
/// The application runs on <see cref="Default"/> and nothing else. The thresholds were a
/// user setting until 2026-09-29 and were taken out: nobody could know what to set, and
/// any other number either brought back the freezes the tiers prevent or asked Etch to
/// hold more text than it can. The constructor stays public so tests can exercise the
/// tiers with small sizes. The boundaries are inclusive-below: a document of exactly
/// <see cref="ReducedThreshold"/> bytes is still <see cref="DocumentTier.Full"/>.
/// </remarks>
public sealed class DocumentSizePolicy
{
    private const long Mebibyte = 1024L * 1024L;

    /// <summary>The shipped defaults: 2 MiB, 10 MiB, 100 MiB.</summary>
    public static DocumentSizePolicy Default { get; } =
        new(reducedThreshold: 2 * Mebibyte, plainTextThreshold: 10 * Mebibyte, hardCeiling: 100 * Mebibyte);

    /// <summary>
    /// The longest line the editor lays out in full, in characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A limit on the shape of the text rather than on the amount of it, and it applies in
    /// every tier. AvalonEdit builds each document line as one visual line and formats all of
    /// it, so one line of two million characters, which is what a pasted minified JSON
    /// response is, costs seconds to draw and seconds again for every keystroke on it. With a
    /// grammar on top it does not finish at all: the highlighter splits the line's elements
    /// once per token. Measured on 2026-09-28, a 2 MB line took ten seconds to paste with no
    /// highlighting, and with JSON highlighting the window had not answered three minutes
    /// later. At 20 MB even the plain line had not.
    /// </para>
    /// <para>
    /// Past this length the editor shows the start of a line and says how much more there is,
    /// and a document holding such a line is neither highlighted nor folded. The text itself
    /// is untouched: transforms, find, copy and save all see every character of it.
    /// </para>
    /// </remarks>
    public const int LongLineLength = 10_000;

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

    /// <summary>Above this size, highlighting is off, and a file opened at this size is not journaled.</summary>
    public long PlainTextThreshold { get; }

    /// <summary>Above this size, the document is refused, and no paste or transform may take it there.</summary>
    public long HardCeiling { get; }

    /// <summary>
    /// Classifies a document by size in bytes.
    /// </summary>
    /// <param name="sizeInBytes">The document size. Must not be negative.</param>
    public DocumentCapabilities Evaluate(long sizeInBytes) => Evaluate(sizeInBytes, hasLongLines: false);

    /// <summary>
    /// Classifies a document that is about to be opened, by its size and by whether any of
    /// its lines is longer than <see cref="LongLineLength"/>.
    /// </summary>
    /// <param name="sizeInBytes">The document size. Must not be negative.</param>
    /// <param name="hasLongLines">Whether a line is longer than <see cref="LongLineLength"/>.</param>
    /// <remarks>
    /// This is where journaling is decided, and the only place: a document past the
    /// plain-text threshold is not journaled, because the file it came from is its copy on
    /// disk. <see cref="Reassess"/> carries the decision forward unchanged.
    /// </remarks>
    public DocumentCapabilities Evaluate(long sizeInBytes, bool hasLongLines)
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

        return Reassess(sizeInBytes, hasLongLines, journaling: sizeInBytes <= PlainTextThreshold);
    }

    /// <summary>
    /// Re-derives what a document that is already open may switch on, from what it holds now.
    /// </summary>
    /// <param name="sizeInBytes">
    /// The document's size now. Estimated for text that has not been written anywhere yet,
    /// which is fine: the thresholds are about cost, and the estimate is close enough to it.
    /// </param>
    /// <param name="hasLongLines">Whether a line is longer than <see cref="LongLineLength"/>.</param>
    /// <param name="journaling">
    /// Whether the document is journaled. Decided by <see cref="Evaluate(long, bool)"/> when
    /// it was opened and passed back in unchanged, never re-derived from the new size.
    /// Withdrawing it from a document that grew would leave the shadow copy behind as it
    /// was, and a tab restored from that copy would later be saved over its own file with
    /// text older than the file. Granting it to one that shrank buys nothing: its file is
    /// already its copy.
    /// </param>
    /// <remarks>
    /// <para>
    /// The size thresholds were only ever applied when a file was opened, so a scratch tab,
    /// born empty and therefore in the <see cref="DocumentTier.Full"/> tier, kept
    /// highlighting and folding whatever was pasted into it. Highlighting a 100 MB paste
    /// froze the window for nineteen seconds as soon as the caret landed at its end, because
    /// the grammar has to read every line above the viewport. This is what an edit consults.
    /// </para>
    /// <para>
    /// A document above the hard ceiling can still be open (a buffer restored after the
    /// ceiling was lowered, say) and is treated like a plain-text one: it is on screen, so
    /// the only question left is what it may switch on.
    /// </para>
    /// </remarks>
    public DocumentCapabilities Reassess(long sizeInBytes, bool hasLongLines, bool journaling)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBytes);

        var tier = sizeInBytes > HardCeiling ? DocumentTier.Rejected
            : sizeInBytes > PlainTextThreshold ? DocumentTier.PlainText
            : sizeInBytes > ReducedThreshold ? DocumentTier.Reduced
            : DocumentTier.Full;

        var highlighting = tier is DocumentTier.Full or DocumentTier.Reduced;
        var foldingAndDetection = tier == DocumentTier.Full;

        return new DocumentCapabilities(
            tier,
            CanOpen: tier != DocumentTier.Rejected,
            SyntaxHighlighting: highlighting && !hasLongLines,
            Folding: foldingAndDetection && !hasLongLines,
            DetectOnEdit: foldingAndDetection,
            Journaling: journaling,
            Notice: Explain(tier, sizeInBytes, hasLongLines, journaling));
    }

    /// <summary>The status-bar sentence for whatever was taken away, or null when nothing was.</summary>
    private string? Explain(DocumentTier tier, long sizeInBytes, bool hasLongLines, bool journaling)
    {
        var size = tier switch
        {
            DocumentTier.Full => null,
            DocumentTier.Reduced => $"Large file ({Describe(sizeInBytes)}) - folding off.",
            DocumentTier.PlainText when journaling => $"Plain-text mode for {Describe(sizeInBytes)}: highlighting is off.",
            DocumentTier.PlainText => $"Plain-text mode - {Describe(sizeInBytes)} file, highlighting and auto-save off.",
            _ => $"{Describe(sizeInBytes)} is over the {Describe(HardCeiling)} editor limit, so highlighting is off.",
        };

        if (!hasLongLines)
        {
            return size;
        }

        var lines = string.Create(
            CultureInfo.InvariantCulture,
            $"Lines over {LongLineLength:N0} characters are shown in part, and highlighting is off.");

        return size is null ? lines : $"{size} {lines}";
    }

    /// <summary>
    /// Formats a byte count for a status-bar message ("9.4 MB", "512 KB", "27 bytes").
    /// Binary units, decimal-ish labels: matching what Windows itself shows.
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
