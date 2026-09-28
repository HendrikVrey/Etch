using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Decides what a buffer is.
/// </summary>
/// <remarks>
/// <para>
/// Two phases, cheap first, exactly as the plan's section 7 describes. Every detector
/// opens with character tests that rule it out in microseconds, and only the one or
/// two that survive pay for a real parse. The whole pass runs on a capped sample, off
/// the UI thread, on a debounce, never per keystroke.
/// </para>
/// <para>
/// Stateless and static. There is nothing to construct at startup, which is the point:
/// the plan's startup budget is spent before the first frame, and detection does not
/// run until somebody has typed something.
/// </para>
/// </remarks>
public static class FormatDetection
{
    /// <summary>
    /// How much of a buffer is examined, in characters.
    /// </summary>
    /// <remarks>
    /// The plan's number. A prefix is enough to identify a format, nothing here needs
    /// to see the end of a document to know what kind of document it is, and capping
    /// it is what keeps detection on a 10 MB log file as cheap as on a paragraph.
    /// </remarks>
    public const int SampleLimit = 64 * 1024;

    /// <summary>
    /// The detectors, in no particular order. Ties are broken by <see cref="Priority"/>.
    /// </summary>
    private static readonly IFormatDetector[] Detectors =
    [
        new JsonDetector(),
        new NdjsonDetector(),
        new JwtDetector(),
        new Base64Detector(),
        new Base64UrlDetector(),
        new HexDetector(),
        new UrlEncodedDetector(),
        new GuidDetector(),
        new UnixEpochDetector(),
        new Iso8601Detector(),
    ];

    /// <summary>
    /// Which format wins when two detectors are equally confident.
    /// </summary>
    /// <remarks>
    /// Most specific first, and every position here is a judgement worth stating:
    /// <list type="bullet">
    /// <item><b>JWT above base64url</b> (a token <em>is</em> three base64url segments,
    /// so both will fire and the more specific answer is the useful one.</item>
    /// <item><b>NDJSON above JSON</b>) the first line of an NDJSON file is a valid
    /// JSON document, so JSON will often fire weakly on one.</item>
    /// <item><b>GUID, epoch and ISO-8601 above the encodings</b>, all three are
    /// whole-buffer matches on a rigid shape, where the encodings are guessing from an
    /// alphabet. The three cannot collide with each other: an epoch is nothing but digits,
    /// a GUID carries its dashes in five uneven groups, and an ISO date carries them in
    /// three, so their relative order here is arbitrary and does not decide anything. It is
    /// the encodings below them that this position exists to outrank (before the uniform-
    /// group rule landed in <c>HexDetector</c>, every ISO date in the world read as
    /// hexadecimal.</item>
    /// <item><b>Hex above base64</b>) every hex string is also in the base64 alphabet.
    /// The reverse is not true, so hex is the narrower claim.</item>
    /// <item><b>Standard base64 above URL-safe</b>: the two alphabets differ in two
    /// characters, so anything using neither <c>+/</c> nor <c>-_</c> matches both
    /// detectors with the same confidence and this pair decides it. Standard wins
    /// because it is the overwhelmingly more common form; a string that really is
    /// URL-safe says so with a <c>-</c> or a <c>_</c>, and the standard detector rejects
    /// those outright, so the specific case still resolves the specific way.</item>
    /// </list>
    /// </remarks>
    private static readonly FormatId[] Priority =
    [
        FormatId.Jwt,
        FormatId.Ndjson,
        FormatId.Json,
        FormatId.Guid,
        FormatId.UnixEpoch,
        FormatId.Iso8601,
        FormatId.UrlEncoded,
        FormatId.Hex,
        FormatId.Base64,
        FormatId.Base64Url,
    ];

    /// <summary>Identifies the format of <paramref name="text"/>.</summary>
    /// <param name="text">The buffer, or the selection.</param>
    /// <returns>
    /// The best answer, or <see cref="DetectionResult.PlainText"/> when nothing fired.
    /// Never throws: every buffer is untrusted input and "I do not know" is a normal
    /// answer, not an exceptional one.
    /// </returns>
    public static DetectionResult Detect(string? text) =>
        Detect(text.AsSpan());

    /// <inheritdoc cref="Detect(string?)" />
    public static DetectionResult Detect(ReadOnlySpan<char> text) =>
        DetectSample(text.Length > SampleLimit ? text[..SampleLimit] : text, text.Length);

    /// <summary>
    /// Identifies a format from a prefix that has already been read.
    /// </summary>
    /// <param name="sample">
    /// The first <see cref="SampleLimit"/> characters of the buffer, or all of it when
    /// it is shorter.
    /// </param>
    /// <param name="totalLength">
    /// The length of the whole buffer, which is what tells this apart from a short
    /// document that happens to end where the sample does.
    /// </param>
    /// <remarks>
    /// The overload an editor actually wants. Materialising a 10 MB document into a
    /// string so that 64 KB of it can be examined is an allocation the size of the
    /// buffer on every debounce; a bounded read out of the document's own snapshot
    /// costs the sample and nothing else.
    /// </remarks>
    public static DetectionResult DetectSample(ReadOnlySpan<char> sample, int totalLength)
    {
        if (Sniff.IsBlank(sample))
        {
            return DetectionResult.PlainText;
        }

        // Strictly greater. A buffer of exactly SampleLimit characters was not cut, and
        // treating it as though it were would downgrade a perfectly valid document from
        // Certain to Likely and label the chip "(sampled)" for nothing.
        var wasSampled = totalLength > sample.Length;

        // Leading whitespace is trimmed for everyone, because every detector would
        // otherwise begin by skipping it. Trailing whitespace is only trimmed when the
        // buffer is whole, on a sample the end is a cut point, not an end.
        var trimmed = wasSampled ? sample.TrimStart() : sample.Trim();

        var isComplete = !wasSampled;

        var best = DetectionResult.PlainText;
        var bestPriority = int.MaxValue;

        foreach (var detector in Detectors)
        {
            var confidence = Confidence(detector, trimmed, isComplete);

            if (confidence == DetectionConfidence.None)
            {
                continue;
            }

            var priority = PriorityOf(detector.Format);

            if (confidence < best.Confidence || (confidence == best.Confidence && priority >= bestPriority))
            {
                continue;
            }

            best = new DetectionResult(detector.Format, confidence, wasSampled);
            bestPriority = priority;
        }

        return best;
    }

    /// <summary>
    /// Runs one detector, treating a crash in it as "not my format".
    /// </summary>
    /// <remarks>
    /// A detector is a small pure function over a span, so this should never fire, but
    /// it runs against arbitrary pasted bytes on a background thread, and an unhandled
    /// exception there takes the process down with it. One bad detector must cost the
    /// format chip, not the editor and everything unsaved in it.
    /// </remarks>
    private static DetectionConfidence Confidence(
        IFormatDetector detector,
        ReadOnlySpan<char> sample,
        bool isComplete)
    {
        try
        {
            return detector.Detect(sample, isComplete);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return DetectionConfidence.None;
        }
    }

    private static int PriorityOf(FormatId format)
    {
        for (var i = 0; i < Priority.Length; i++)
        {
            if (Priority[i] == format)
            {
                return i;
            }
        }

        return Priority.Length;
    }
}
