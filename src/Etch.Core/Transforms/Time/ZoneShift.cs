using System.Globalization;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Time;

/// <summary>
/// Re-expresses an instant in another zone, without changing the instant.
/// </summary>
/// <remarks>
/// <para>
/// Both directions share this because they differ in one line and are wrong in the same
/// three ways if they drift apart. The output keeps its offset — <c>+02:00</c> rather than
/// a bare local time — because a timestamp that has been moved into a zone and then lost
/// the record of which zone is worse than the one that came in.
/// </para>
/// <para>
/// <b>Input with no offset is read as UTC</b>, matching <see cref="IsoToEpoch"/>. The
/// alternative — the machine's zone — would make "convert to local" a no-op on some
/// machines and a real shift on others, from identical text.
/// </para>
/// </remarks>
internal abstract class ZoneShift : ITransform
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Time;

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Aliases { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Offered for an ISO-8601 buffer but never the suggested action — <see cref="IsoToEpoch"/>
    /// claims that. Both of these are reachable in two keystrokes from the palette, and
    /// keeping Ctrl+Enter a predictable toggle is worth more than saving one of them.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.Iso8601);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var trimmed = input.Text.Trim();

        if (!DateTimeOffset.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var moment))
        {
            return TransformResult.Failed("That is not an ISO-8601 timestamp.");
        }

        var shifted = Shift(moment);

        return TransformResult.Ok(
            shifted.ToString(Format, CultureInfo.InvariantCulture),
            FormatId.Iso8601,
            Describe(shifted));
    }

    /// <summary>
    /// How to write the result.
    /// </summary>
    /// <remarks>
    /// Seven fractional digits in both implementations, so nothing is lost on the way
    /// through, and capital <c>F</c> so the digits and their decimal point disappear
    /// together — a whole second reads <c>12:34:56+02:00</c> rather than
    /// <c>12:34:56.0000000+02:00</c>. What differs is the zone: a literal <c>Z</c> for UTC,
    /// a real offset for anywhere else.
    /// </remarks>
    protected abstract string Format { get; }

    /// <summary>Moves the instant into this transform's zone.</summary>
    protected abstract DateTimeOffset Shift(DateTimeOffset moment);

    /// <summary>What to say about the result.</summary>
    protected abstract string Describe(DateTimeOffset shifted);
}

/// <summary>Re-expresses an instant in UTC.</summary>
internal sealed class ToUtc : ZoneShift
{
    /// <inheritdoc />
    public override string Id => "time.toUtc";

    /// <inheritdoc />
    public override string Name => "To UTC";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["utc", "zulu", "gmt", "universal time"];

    /// <inheritdoc />
    /// <remarks>
    /// <c>'Z'</c> rather than <c>+00:00</c>, and quoted rather than left bare. Both forms
    /// are legal ISO-8601 and <c>Z</c> is what <see cref="EpochToIso"/> emits, so the two
    /// agree on what a UTC instant looks like. The quotes make it unambiguously a literal
    /// instead of relying on <c>Z</c> not being a format specifier.
    /// </remarks>
    protected override string Format => "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'";

    /// <inheritdoc />
    protected override DateTimeOffset Shift(DateTimeOffset moment) => moment.ToUniversalTime();

    /// <inheritdoc />
    protected override string Describe(DateTimeOffset shifted) => "Converted to UTC.";
}

/// <summary>
/// Re-expresses an instant in the machine's own zone.
/// </summary>
/// <remarks>
/// The one transform in the catalogue whose output depends on the machine it runs on, which
/// is exactly what makes it useful: the question it answers is "what time was this
/// <em>here</em>". The zone is named in the message so the answer carries its own context —
/// a result of <c>11:15+02:00</c> pasted into a ticket means nothing without knowing what
/// produced it.
/// </remarks>
internal sealed class ToLocalTime : ZoneShift
{
    /// <inheritdoc />
    public override string Id => "time.toLocal";

    /// <inheritdoc />
    public override string Name => "To local time";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["local", "my time zone", "here"];

    /// <inheritdoc />
    protected override string Format => "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz";

    /// <inheritdoc />
    protected override DateTimeOffset Shift(DateTimeOffset moment) => moment.ToLocalTime();

    /// <inheritdoc />
    /// <remarks>
    /// The zone is read fresh each time rather than cached. <c>TimeZoneInfo.Local</c> can
    /// change while a process is running — a laptop crossing a border, or the twice-yearly
    /// daylight-saving switch — and a cached name would then describe the wrong offset.
    /// </remarks>
    protected override string Describe(DateTimeOffset shifted) =>
        $"Converted to {TimeZoneInfo.Local.StandardName}.";
}
