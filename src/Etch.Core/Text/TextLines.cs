namespace Etch.Core.Text;

/// <summary>
/// Splitting text into lines and putting it back, the way every line transform needs.
/// </summary>
/// <remarks>
/// <para>
/// Two details, both of which every line transform got wrong at least once before this
/// existed:
/// </para>
/// <list type="bullet">
/// <item><b>A trailing newline is not an empty last line.</b> Splitting <c>"a\nb\n"</c> on
/// LF produces three elements and the third is not a line, it is the far side of the last
/// break. Sorting it puts a blank at the top; deduplicating it deletes the file's final
/// newline. Both are the sort of change that turns up in somebody's next commit rather than
/// on their screen.</item>
/// <item><b>CR is stripped on the way in and restored on the way out.</b> Splitting on LF
/// alone leaves a CR at the end of every line of a CRLF buffer, where it silently becomes
/// part of the text: <c>"b\r"</c> sorts before <c>"b"</c> and is not equal to it. The join
/// writes whichever ending the document asked for, so the buffer keeps its convention.</item>
/// </list>
/// <para>
/// Splitting on LF rather than on all three conventions is deliberate. A lone CR as a line
/// ending is a classic-Mac artefact that no longer arrives in practice, and treating a CR
/// as a break would split single-line text that merely contains one.
/// </para>
/// </remarks>
internal static class TextLines
{
    /// <summary>Splits <paramref name="text"/> into lines, with CR removed.</summary>
    /// <param name="text">The text to split.</param>
    /// <param name="trailingNewLine">
    /// True when the text ended with a line break, so that <see cref="Join"/> can put it
    /// back. The returned array never contains it.
    /// </param>
    public static string[] Split(string text, out bool trailingNewLine)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            trailingNewLine = false;
            return [];
        }

        var parts = text.Split('\n');

        trailingNewLine = parts[^1].Length == 0;

        var count = trailingNewLine ? parts.Length - 1 : parts.Length;
        var lines = new string[count];

        for (var i = 0; i < count; i++)
        {
            var part = parts[i];

            // Exactly one CR, not TrimEnd('\r'). Join only ever restores one ending per
            // line, so a greedy trim destroys every CR beyond the first and cannot put it
            // back, and "a\r\r\n" is a real thing that text-mode translation layers, MSYS
            // pipes and serial captures produce. One character of somebody's buffer would
            // disappear on every line transform, silently.
            lines[i] = part.Length > 0 && part[^1] == '\r' ? part[..^1] : part;
        }

        return lines;
    }

    /// <summary>Joins lines back together with <paramref name="newLine"/>.</summary>
    /// <param name="lines">The lines, without their endings.</param>
    /// <param name="trailingNewLine">Whether to end the result with a line break.</param>
    /// <param name="newLine">The ending to write. Must be CRLF, LF or CR.</param>
    public static string Join(IReadOnlyList<string> lines, bool trailingNewLine, string newLine)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (newLine is not ("\r\n" or "\n" or "\r"))
        {
            throw new ArgumentException("Only CRLF, LF and CR are line endings.", nameof(newLine));
        }

        if (lines.Count == 0)
        {
            return trailingNewLine ? newLine : string.Empty;
        }

        // Sized up front. These run over whole documents, and a StringBuilder that doubles
        // its way to 10 MB copies most of the document several times on the way.
        var capacity = newLine.Length * lines.Count;

        foreach (var line in lines)
        {
            capacity += line.Length;
        }

        var builder = new System.Text.StringBuilder(capacity);

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(newLine);
            }

            builder.Append(lines[i]);
        }

        if (trailingNewLine)
        {
            builder.Append(newLine);
        }

        return builder.ToString();
    }
}
