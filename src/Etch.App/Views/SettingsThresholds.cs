using System.Globalization;
using Etch.Persistence.Model;

namespace Etch.App.Views;

/// <summary>
/// Converts between the settings panel's megabyte text boxes and the byte counts
/// <c>DocumentSizePolicy</c> is built from.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than more members on the window, because this is a pure
/// string-to-number rule and nothing about it needs a <c>Window</c>. Reaching it through
/// <c>MainWindow</c> would also mean every test of it ran that type's static
/// initialiser, which constructs WPF objects, for no reason at all.
/// </para>
/// <para>
/// <see cref="Format"/> and <see cref="TryParse"/> are a matched pair and have to stay
/// one: a formatter that rounds more coarsely than the parser accepts produces a panel
/// that rejects the number it displayed a moment earlier, which is why they live
/// together and why <see cref="Precision"/> is stated once.
/// </para>
/// <para>
/// <b>They are not exact inverses, and cannot be.</b> A byte count divided by 2^20
/// terminates in decimal but can need up to twenty places (100 KiB is 0.09765625 MB, and
/// an odd byte count is far worse) so every readable precision loses something. What is
/// guaranteed instead are the two properties the panel actually rests on: anything
/// <see cref="Format"/> produces is accepted by <see cref="TryParse"/>, and formatting is
/// idempotent, so repeated edits cannot walk a threshold away from where the user put it.
/// The residual drift is dealt with by not re-reading a box nobody has touched: see
/// <see cref="IsUnchanged"/>.
/// </para>
/// </remarks>
internal static class SettingsThresholds
{
    /// <summary>
    /// How finely a size is displayed.
    /// </summary>
    /// <remarks>
    /// Four decimal places, not two, and the floor is why: the smallest legal threshold is
    /// 64 KiB, which is 0.0625 MB exactly. Rounding that to "0.06" would write back 62 914
    /// bytes, below the floor, so the panel would refuse the value it had just shown.
    /// </remarks>
    private const string Precision = "0.####";

    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>Formats a byte count as the megabyte figure a text box shows.</summary>
    public static string Format(long bytes) =>
        (bytes / BytesPerMegabyte).ToString(Precision, CultureInfo.CurrentCulture);

    /// <summary>
    /// Whether <paramref name="text"/> is still exactly what <see cref="Format"/> would
    /// have written for <paramref name="bytes"/>, that is, whether the box is untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what stops a threshold drifting because somebody edited a different
    /// setting. <see cref="Format"/> loses precision that <see cref="TryParse"/> cannot
    /// give back, so re-reading an untouched box would silently rewrite 102,400 bytes as
    /// 102,445 the first time the user changed the retention field beside it.
    /// </para>
    /// <para>
    /// An ordinal string comparison, deliberately, rather than parsing and comparing
    /// numbers: the question is whether the user has typed in this box, and the exact
    /// characters are the only honest evidence of that. It also means a value that came
    /// <em>from</em> the box compares equal to itself for free, because that is precisely
    /// the text the box holds.
    /// </para>
    /// </remarks>
    public static bool IsUnchanged(string text, long bytes) =>
        string.Equals(text, Format(bytes), StringComparison.Ordinal);

    /// <summary>
    /// Parses the three size fields, accepting them only as a valid set.
    /// </summary>
    /// <remarks>
    /// All or nothing, because <c>DocumentSizePolicy</c> throws on a set that does not
    /// ascend, so accepting one field at a time would mean every intermediate keystroke
    /// produced a policy that could not be constructed.
    /// </remarks>
    public static bool TryParse(
        string reducedText,
        string plainTextText,
        string ceilingText,
        out long reduced,
        out long plainText,
        out long ceiling)
    {
        reduced = 0;
        plainText = 0;
        ceiling = 0;

        return TryReadMegabytes(reducedText, out reduced)
            && TryReadMegabytes(plainTextText, out plainText)
            && TryReadMegabytes(ceilingText, out ceiling)
            && reduced >= EtchSettings.MinThresholdBytes
            && ceiling <= EtchSettings.MaxThresholdBytes
            && reduced < plainText
            && plainText < ceiling;
    }

    /// <summary>Parses a megabyte figure into bytes.</summary>
    /// <remarks>
    /// The styles are spelled out rather than taken from <c>NumberStyles.Float</c>, which
    /// includes <c>AllowExponent</c>: "1e9" in a box labelled MB is far more likely to be
    /// a typo than an intention. A leading sign <em>is</em> allowed, so that "-1" reads as
    /// out of range in the caller's check rather than as unparseable: the two deserve
    /// different messages.
    /// </remarks>
    private static bool TryReadMegabytes(string text, out long bytes)
    {
        bytes = 0;

        const NumberStyles Styles = NumberStyles.AllowLeadingSign
            | NumberStyles.AllowDecimalPoint
            | NumberStyles.AllowLeadingWhite
            | NumberStyles.AllowTrailingWhite;

        if (!double.TryParse(text, Styles, CultureInfo.CurrentCulture, out var megabytes)
            || !double.IsFinite(megabytes))
        {
            return false;
        }

        var exact = megabytes * BytesPerMegabyte;

        // Range-checked before the cast, not after. A double outside long's range converts,
        // in an unchecked context, to an unspecified value, long.MinValue in practice,
        // which would then sail through the ascending-order check above as a
        // plausible-looking negative threshold.
        if (exact is < 0 or > EtchSettings.MaxThresholdBytes)
        {
            return false;
        }

        bytes = (long)exact;
        return true;
    }
}
