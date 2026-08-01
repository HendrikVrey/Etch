namespace Etch.Core.Text;

/// <summary>
/// Counts of each newline convention found in a sample.
/// </summary>
/// <param name="Crlf">Number of CRLF pairs.</param>
/// <param name="Lf">Number of bare LFs (an LF that is part of a CRLF is not counted here).</param>
/// <param name="Cr">Number of bare CRs (a CR that is part of a CRLF is not counted here).</param>
public readonly record struct LineEndingCounts(int Crlf, int Lf, int Cr)
{
    /// <summary>Total line breaks of any kind.</summary>
    public int Total => Crlf + Lf + Cr;

    /// <summary>True when more than one convention appears — worth surfacing, it bites people.</summary>
    public bool IsMixed => (Crlf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) > 1;

    /// <summary>
    /// The most common convention. Ties resolve CRLF &gt; LF &gt; CR, which matches
    /// what a Windows editor should default to when the evidence is ambiguous.
    /// </summary>
    public LineEndingStyle Dominant
    {
        get
        {
            if (Total == 0)
            {
                return LineEndingStyle.None;
            }

            if (Crlf >= Lf && Crlf >= Cr)
            {
                return LineEndingStyle.Crlf;
            }

            return Lf >= Cr ? LineEndingStyle.Lf : LineEndingStyle.Cr;
        }
    }
}

/// <summary>Detects newline conventions without allocating or scanning the whole buffer.</summary>
public static class LineEndings
{
    /// <summary>
    /// How much of a buffer to inspect by default. Newline style is uniform in
    /// practice, so a bounded sample answers the question at constant cost no
    /// matter how large the document is.
    /// </summary>
    public const int DefaultSampleLength = 64 * 1024;

    /// <summary>Counts line endings across the whole span provided.</summary>
    public static LineEndingCounts Count(ReadOnlySpan<char> text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++; // consume the LF so it is not double-counted
                    }
                    else
                    {
                        cr++;
                    }

                    break;

                case '\n':
                    lf++;
                    break;
            }
        }

        return new LineEndingCounts(crlf, lf, cr);
    }

    /// <summary>
    /// Detects the dominant style from a bounded prefix of the text.
    /// </summary>
    /// <param name="text">The buffer, or any prefix of it.</param>
    /// <param name="sampleLength">
    /// Maximum number of characters to inspect. Clamped to the length of <paramref name="text"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="sampleLength"/> is not positive.</exception>
    public static LineEndingStyle Detect(ReadOnlySpan<char> text, int sampleLength = DefaultSampleLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleLength);

        var take = Math.Min(sampleLength, text.Length);

        // Do not split a CRLF across the sample boundary: if the last sampled char
        // is a CR and a LF follows just outside the window, include it.
        if (take > 0 && take < text.Length && text[take - 1] == '\r' && text[take] == '\n')
        {
            take++;
        }

        return Count(text[..take]).Dominant;
    }

    /// <summary>The literal characters for a style, or an empty string for <see cref="LineEndingStyle.None"/>.</summary>
    public static string ToLiteral(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Crlf => "\r\n",
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.Cr => "\r",
        LineEndingStyle.None => string.Empty,
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown line ending style."),
    };

    /// <summary>
    /// Rewrites every line ending in <paramref name="text"/> as <paramref name="newLine"/>.
    /// </summary>
    /// <remarks>
    /// Transforms need this because the libraries they are built on choose their own
    /// newline. Writing LF into a CRLF buffer produces a file with both conventions in
    /// it, which is invisible on screen, loud in a diff, and blamed on the editor.
    /// </remarks>
    /// <param name="text">The text to rewrite.</param>
    /// <param name="newLine">The ending to emit. Must be CRLF, LF or CR.</param>
    public static string Normalise(string text, string newLine)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (newLine is not ("\r\n" or "\n" or "\r"))
        {
            throw new ArgumentException("Only CRLF, LF and CR are line endings.", nameof(newLine));
        }

        var counts = Count(text);

        // Already uniform in the requested style. The common case by far, and worth not
        // allocating a second copy of a possibly very large buffer for.
        if (counts.Total == 0
            || (newLine == "\r\n" && counts.Lf == 0 && counts.Cr == 0)
            || (newLine == "\n" && counts.Crlf == 0 && counts.Cr == 0)
            || (newLine == "\r" && counts.Crlf == 0 && counts.Lf == 0))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + counts.Total);

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];

            if (character == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(newLine);
                continue;
            }

            if (character == '\n')
            {
                builder.Append(newLine);
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>The short label shown in the status bar.</summary>
    public static string ToDisplayName(LineEndingStyle style) => style switch
    {
        LineEndingStyle.Crlf => "CRLF",
        LineEndingStyle.Lf => "LF",
        LineEndingStyle.Cr => "CR",
        LineEndingStyle.None => "—",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unknown line ending style."),
    };
}
