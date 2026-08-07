using System.Globalization;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Time;

/// <summary>
/// Turns an ISO-8601 timestamp into a Unix timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>The exact inverse of <see cref="EpochToIso"/>, and that is a design requirement
/// rather than a coincidence.</b> Both are suggested for their own format, so
/// <c>Ctrl+Enter</c> pressed twice on a timestamp has to return the value it started with.
/// That is why seconds are emitted when there is no sub-second component and milliseconds
/// only when there is: <c>EpochToIso</c> writes <c>.000</c> for a whole second, and reading
/// it back as milliseconds would turn 1 754 038 500 into 1 754 038 500 000 and never turn
/// back.
/// </para>
/// <para>
/// <b>A timestamp with no zone is read as UTC.</b> There is no good answer here — the text
/// genuinely does not say — but there is a defensible one: the machine's zone would make
/// the same buffer convert to different numbers on different machines, which is the
/// property that makes a scratchpad untrustworthy. The message says which assumption was
/// made, so the ambiguity is surfaced rather than hidden.
/// </para>
/// <para>
/// <b>The toggle is one-way before 1970.</b> A pre-epoch instant converts to a negative
/// number, <c>EpochToIso</c> reads it back correctly, but <c>UnixEpochDetector</c> does not
/// claim a leading minus sign — so the format chip drops to plain text and the second
/// <c>Ctrl+Enter</c> does nothing rather than converting back. Widening the detector to
/// accept a sign would make every negative nine-to-thirteen-digit number in a buffer a
/// timestamp, which is a worse trade than a one-way conversion for dates most people never
/// paste. The palette still reaches it.
/// </para>
/// </remarks>
internal sealed class IsoToEpoch : ITransform
{
    /// <inheritdoc />
    public string Id => "time.isoToEpoch";

    /// <inheritdoc />
    public string Name => "ISO-8601 to Unix time";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Time;

    /// <inheritdoc />
    /// <remarks>
    /// <b>Directional forms only, deliberately.</b> This transform and
    /// <see cref="EpochToIso"/> are inverses, so any alias they share is a tie neither can
    /// win on merit: both scored 97 on a query of <c>epoch</c>, and the winner was decided
    /// by <see cref="Precedence"/> — a value chosen to settle <c>Ctrl+Enter</c> on a
    /// detected buffer, which has nothing to say about what a typed word means. The rule
    /// that resolves it: <b>a bare format noun belongs to the transform that consumes that
    /// format, and its inverse takes the "to …" form.</b> Someone typing <c>epoch</c> at a
    /// buffer that is an epoch wants it made readable.
    /// <para>
    /// Nothing is lost in the other direction. When the buffer really is ISO-8601,
    /// detection applies the suggested bonus, which outranks every fuzzy score by two
    /// orders of magnitude — so <c>epoch</c>, <c>timestamp</c> and <c>unix time</c> all
    /// still land here first, reached through the "to …" aliases below.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Aliases { get; } = ["to epoch", "to unix time", "to timestamp"];

    /// <inheritdoc />
    /// <remarks>
    /// Claims the suggested slot for ISO-8601, ahead of the two zone shifts. Pairing it
    /// with <see cref="EpochToIso"/> makes <c>Ctrl+Enter</c> a toggle between the two ways
    /// of writing an instant, which is a thing a person can learn; "sometimes converts to
    /// local time" is not.
    /// </remarks>
    public int Precedence => -100;

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Iso8601);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var trimmed = input.Text.Trim();

        // AssumeUniversal with AdjustToUniversal: the first says what to do with a
        // timestamp carrying no offset, the second stops one that *does* carry an offset
        // from being re-expressed in the machine's zone on the way through. Without the
        // pair, the same instant parses differently in Johannesburg and in London.
        if (!DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var moment))
        {
            return TransformResult.Failed("That is not an ISO-8601 timestamp.");
        }

        // All three components, so that a fraction too small to survive the conversion at
        // least decides the unit. Unix milliseconds cannot carry more precision than that;
        // what matters is that a value with a fraction is not silently rounded to a whole
        // second and reported as though nothing was lost.
        var hasSubSeconds = moment.Millisecond != 0 || moment.Microsecond != 0 || moment.Nanosecond != 0;

        var value = hasSubSeconds
            ? moment.ToUnixTimeMilliseconds()
            : moment.ToUnixTimeSeconds();

        var units = hasSubSeconds ? "milliseconds" : "seconds";
        var assumed = HasZone(trimmed) ? string.Empty : " - no zone given, read as UTC";

        return TransformResult.Ok(
            value.ToString(CultureInfo.InvariantCulture),
            FormatId.UnixEpoch,
            $"Unix time in {units}{assumed}.");
    }

    /// <summary>True when the text carries a zone designator.</summary>
    /// <remarks>
    /// The scan starts past the date, because the date's own separators are dashes and a
    /// search over the whole string would call every plain <c>2026-08-01</c> zone-qualified
    /// — reporting UTC as though the text had said so.
    /// </remarks>
    private static bool HasZone(string text)
    {
        // "2026-08-01". Anything a zone can appear in is longer than this.
        const int DateLength = 10;

        for (var i = DateLength; i < text.Length; i++)
        {
            if (text[i] is 'Z' or 'z' or '+' or '-')
            {
                return true;
            }
        }

        return false;
    }
}
