using System.Globalization;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Time;

/// <summary>
/// Turns a Unix timestamp into an ISO-8601 instant.
/// </summary>
/// <remarks>
/// <para>
/// The width decides the unit: ten digits is seconds, thirteen is milliseconds. That
/// is not a heuristic so much as an arithmetic fact for any date this century: the
/// two ranges do not overlap and will not until the year 2286.
/// </para>
/// <para>
/// UTC, always, and labelled. A timestamp rendered in the reader's local time is a
/// value that means something different on the next machine it is pasted into, which
/// defeats the purpose of writing it down.
/// </para>
/// </remarks>
internal sealed class EpochToIso : ITransform
{
    /// <inheritdoc />
    public string Id => "time.epochToIso";

    /// <inheritdoc />
    public string Name => "Unix time to ISO-8601";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Time;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["timestamp", "epoch", "unix time", "date"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => detection.Is(FormatId.UnixEpoch);

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var trimmed = input.Text.AsSpan().Trim();

        // AllowLeadingSign, not None. A Unix timestamp before 1970 is negative, and
        // NumberStyles.None forbids the sign outright, so this used to refuse the exact
        // values IsoToEpoch produces for anything in the past, breaking the round trip the
        // two transforms are documented to form. The detector still does not claim a
        // negative number (see IsoToEpoch), so this is reached from the palette rather than
        // from Ctrl+Enter, which is the honest limit rather than a hidden one.
        if (!long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return TransformResult.Failed("A Unix timestamp is a whole number of seconds or milliseconds.");
        }

        DateTimeOffset moment;

        try
        {
            // Twelve or more digits is milliseconds, matching what UnixEpochDetector
            // treats as the millisecond end of the range. The two files have to agree:
            // one decides what to offer and the other decides what it means, and a buffer
            // detected as a timestamp that then converts as the other unit is worse than
            // not offering the transform at all.
            //
            // Digits, not characters. Now that a leading sign is accepted, measuring the
            // whole string would make "-14182980123" one character wider than the same
            // number of digits without the sign and read eleven digits as milliseconds.
            var digits = trimmed.Length > 0 && trimmed[0] is '-' or '+' ? trimmed.Length - 1 : trimmed.Length;

            moment = digits >= 12
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return TransformResult.Failed($"{value} is outside the range of representable dates.");
        }

        return TransformResult.Ok(
            moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
    }
}
