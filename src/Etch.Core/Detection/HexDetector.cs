using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises hexadecimal bytes.
/// </summary>
/// <remarks>
/// <para>
/// Separators are allowed because the hex people actually paste has them: a hash is
/// bare, a MAC address uses colons or dashes, a certificate fingerprint uses colons,
/// and a hex dump uses spaces. Rejecting the punctuation would mean recognising only
/// the least interesting of the four.
/// </para>
/// <para>
/// <b>But separated groups must all be the same width.</b> Without that rule this
/// detector fires, with full confidence, on <c>2026-07-31</c>, because every decimal
/// digit is a hex digit and <c>-</c> is a separator. Every ISO date in the world would
/// be offered "Hex to text" as its <c>Ctrl+Enter</c> action, and that key does not ask
/// first: it would silently rewrite the date as four bytes of control characters.
/// Uniform groups keep <c>de:ad:be:ef</c> and <c>48 65 6c</c> and cost nothing real,
/// because nothing emits hex in ragged groups.
/// </para>
/// </remarks>
internal sealed class HexDetector : IFormatDetector
{
    /// <summary>Four bytes. Below that, a hex-looking word is likelier than a hex value.</summary>
    private const int MinimumDigits = 8;

    /// <inheritdoc />
    public FormatId Format => FormatId.Hex;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        var digits = 0;
        var groups = 0;
        var groupWidth = 0;
        var currentGroup = 0;
        var separated = false;
        var ragged = false;

        foreach (var character in sample)
        {
            if (Sniff.IsHexDigit(character))
            {
                digits++;
                currentGroup++;
                continue;
            }

            if (!char.IsWhiteSpace(character) && character is not (':' or '-'))
            {
                return DetectionConfidence.None;
            }

            separated = true;
            ragged |= !CloseGroup(ref currentGroup, ref groups, ref groupWidth);
        }

        ragged |= !CloseGroup(ref currentGroup, ref groups, ref groupWidth);

        if (digits < MinimumDigits)
        {
            return DetectionConfidence.None;
        }

        // Ragged groups are the date case, and there is no reading of them that is
        // usefully hexadecimal.
        if (separated && (ragged || groupWidth % 2 != 0))
        {
            return DetectionConfidence.None;
        }

        // An odd number of digits is not a whole number of bytes. Worth reporting weakly
        // rather than not at all: it is what a hex string looks like while it is still
        // being typed, and the transform says what is wrong if it is run.
        if (digits % 2 != 0)
        {
            return isComplete ? DetectionConfidence.Weak : DetectionConfidence.Likely;
        }

        // A sample cut mid-value cannot claim its digit count means anything, and text
        // that happens to be a hex-only English word ("deadbeef") is why this stops short
        // of Certain when there is nothing but the alphabet to go on.
        if (!isComplete)
        {
            return DetectionConfidence.Likely;
        }

        return separated || digits >= 32
            ? DetectionConfidence.Certain
            : DetectionConfidence.Likely;
    }

    /// <summary>
    /// Ends the run of digits in progress and checks it against the established width.
    /// </summary>
    /// <returns>False when this group's width differs from the ones before it.</returns>
    /// <remarks>
    /// Runs of separators produce empty groups (<c>"de: ad"</c>, a trailing newline)
    /// and those are punctuation rather than evidence, so they are skipped.
    /// </remarks>
    private static bool CloseGroup(ref int currentGroup, ref int groups, ref int groupWidth)
    {
        if (currentGroup == 0)
        {
            return true;
        }

        var width = currentGroup;

        currentGroup = 0;
        groups++;

        if (groups == 1)
        {
            groupWidth = width;
            return true;
        }

        return width == groupWidth;
    }
}
