using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Recognises newline-delimited JSON: one complete JSON value per line.
/// </summary>
/// <remarks>
/// <para>
/// The format log pipelines and bulk-import files actually arrive in, and the one most
/// likely to be mistaken for broken JSON — which is precisely why it earns a detector.
/// Without one, a 200-line NDJSON dump is a JSON document with a syntax error on line
/// two, and every transform Etch offers for it is the wrong one.
/// </para>
/// <para>
/// Two lines minimum. A single <c>{...}</c> on its own line is a JSON document, and
/// calling it NDJSON would be technically true and practically useless.
/// </para>
/// </remarks>
internal sealed class NdjsonDetector : IFormatDetector
{
    /// <summary>
    /// How many lines are examined before the answer is taken as settled.
    /// </summary>
    /// <remarks>
    /// Detection is a guess with a 15 ms budget, and twenty consecutive well-formed
    /// JSON lines is not a conclusion the twenty-first is going to overturn.
    /// </remarks>
    private const int MaxLinesExamined = 20;

    /// <summary>Below this, a stray brace-per-line file is more likely than a real one.</summary>
    private const int MinimumLines = 2;

    /// <inheritdoc />
    public FormatId Format => FormatId.Ndjson;

    /// <inheritdoc />
    public DetectionConfidence Detect(ReadOnlySpan<char> sample, bool isComplete)
    {
        if (Sniff.FirstMeaningful(sample) is not ('{' or '['))
        {
            return DetectionConfidence.None;
        }

        return Utf8Scratch.Use(sample, isComplete, static (utf8, complete) => Scan(utf8, complete));
    }

    private static DetectionConfidence Scan(ReadOnlySpan<byte> utf8, bool isComplete)
    {
        var examined = 0;
        var truncatedTail = false;
        var stoppedEarly = false;

        while (!utf8.IsEmpty)
        {
            // Tracked rather than inferred from the count. A file of exactly
            // MaxLinesExamined lines was read in full, and reporting it as merely Likely
            // because the counter reached its limit would be wrong about the one case the
            // limit was never meant to cover.
            if (examined == MaxLinesExamined)
            {
                stoppedEarly = true;
                break;
            }

            var breakAt = utf8.IndexOf((byte)'\n');

            // No newline left. For a whole buffer this is the final line; for a prefix
            // it is a line the sample cut in half, which must not count against the
            // file — the bytes that would have completed it were simply never read.
            if (breakAt < 0)
            {
                if (!isComplete)
                {
                    truncatedTail = true;
                    break;
                }

                breakAt = utf8.Length;
            }

            var line = Trim(utf8[..breakAt]);

            utf8 = breakAt == utf8.Length ? [] : utf8[(breakAt + 1)..];

            // Blank lines between records are common in hand-edited files and are not
            // evidence either way.
            if (line.IsEmpty)
            {
                continue;
            }

            if (line[0] is not ((byte)'{' or (byte)'[') || !JsonDetector.Parses(line, isFinalBlock: true))
            {
                return DetectionConfidence.None;
            }

            examined++;
        }

        if (examined < MinimumLines)
        {
            return DetectionConfidence.None;
        }

        // Certain needs the whole file to have been seen and every line of it to have
        // parsed. A sample that stopped early, or a tail cut mid-record, is Likely.
        return isComplete && !truncatedTail && !stoppedEarly
            ? DetectionConfidence.Certain
            : DetectionConfidence.Likely;
    }

    /// <summary>Trims ASCII whitespace, including the carriage return of a CRLF pair.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> line)
    {
        var start = 0;
        var end = line.Length;

        while (start < end && IsSpace(line[start]))
        {
            start++;
        }

        while (end > start && IsSpace(line[end - 1]))
        {
            end--;
        }

        return line[start..end];

        static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }
}
