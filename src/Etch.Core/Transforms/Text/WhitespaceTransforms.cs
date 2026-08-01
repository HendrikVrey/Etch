using System.Text;
using Etch.Core.Abstractions;
using Etch.Core.Transforms.Lines;

namespace Etch.Core.Transforms.Text;

/// <summary>
/// Removes whitespace from the end of every line.
/// </summary>
/// <remarks>
/// The transform that exists because trailing whitespace is invisible on screen and loud in
/// a diff. Blank lines that consist only of spaces become genuinely empty, which is the same
/// change and the one people mean.
/// </remarks>
internal sealed class TrimTrailingWhitespace : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.trimTrailing";

    /// <inheritdoc />
    public override string Name => "Trim trailing whitespace";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["trim", "strip trailing", "whitespace", "clean up"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var trimmed = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trimmed[i] = lines[i].TrimEnd();
        }

        return trimmed;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = CountChanged(before, after);

        return changed == 0 ? "There was no trailing whitespace." : $"Trimmed {LineCount(changed)}.";
    }
}

/// <summary>
/// Reduces every run of whitespace inside a line to a single space.
/// </summary>
/// <remarks>
/// <para>
/// <b>Within a line, never across lines.</b> Collapsing the whole buffer into one paragraph
/// is a different operation with a different name — "join lines" — and quietly doing it here
/// would make this transform unusable on anything structured.
/// </para>
/// <para>
/// Leading and trailing whitespace goes entirely rather than collapsing to one space:
/// indentation is not a word separator, and a document left with exactly one leading space
/// on every line looks like a bug.
/// </para>
/// </remarks>
internal sealed class CollapseWhitespace : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.collapseWhitespace";

    /// <inheritdoc />
    public override string Name => "Collapse whitespace";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["collapse", "single space", "squeeze spaces", "normalise spaces", "normalize spaces"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var collapsed = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            collapsed[i] = Collapse(lines[i]);
        }

        return collapsed;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = CountChanged(before, after);

        return changed == 0 ? "There was nothing to collapse." : $"Collapsed {LineCount(changed)}.";
    }

    private static string Collapse(string line)
    {
        var builder = new StringBuilder(line.Length);
        var pendingSpace = false;

        foreach (var character in line)
        {
            if (char.IsWhiteSpace(character))
            {
                // Recorded rather than written, so a run at the end of the line is never
                // emitted at all — which is what makes the trailing case fall out of the
                // same loop instead of needing a TrimEnd afterwards. The guard on Length
                // does the same for a run at the start.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Converts leading tabs to spaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Leading whitespace only, and the same is true of its inverse.</b> A tab in the middle
/// of a line is almost always a column separator — pasted TSV, a Markdown table, aligned
/// constants — and rewriting those as spaces destroys the alignment the tabs existed to
/// create. Restricting both transforms to indentation is also what makes them genuine
/// inverses of one another.
/// </para>
/// <para>
/// Tabs are expanded to the <em>next tab stop</em> rather than to a fixed number of spaces.
/// Those are the same thing only when the indentation is tabs alone; on a line that mixes
/// them — which is the line that motivated running this in the first place — a fixed
/// substitution moves the text and tab-stop expansion leaves it exactly where it appeared.
/// </para>
/// </remarks>
internal sealed class TabsToSpaces : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.tabsToSpaces";

    /// <inheritdoc />
    public override string Name => "Tabs to spaces";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["untabify", "detab", "spaces", "expand tabs"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var converted = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            converted[i] = ConvertLine(lines[i], options.IndentSize);
        }

        return converted;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = CountChanged(before, after);

        return changed == 0 ? "There are no leading tabs." : $"Converted {LineCount(changed)}.";
    }

    private static string ConvertLine(string line, int indentSize)
    {
        // A line that is nothing but whitespace is left exactly as it is, for the reason
        // Indent gives: turning "\t\t" into eight spaces puts trailing whitespace on a line
        // with nothing on it, invisible here and a diff comment later.
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var leading = Indentation.Length(line);

        if (line.AsSpan(0, leading).IndexOf('\t') < 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + indentSize);
        var column = 0;

        for (var i = 0; i < leading; i++)
        {
            if (line[i] == '\t')
            {
                var width = indentSize - (column % indentSize);

                builder.Append(' ', width);
                column += width;
            }
            else
            {
                builder.Append(' ');
                column++;
            }
        }

        return builder.Append(line, leading, line.Length - leading).ToString();
    }
}

/// <summary>
/// Converts leading spaces to tabs.
/// </summary>
/// <remarks>
/// Whole indents only: a run of <c>IndentSize</c> spaces becomes one tab and any remainder
/// stays as spaces. That is what keeps continuation lines aligned — code indented two levels
/// and then aligned three further characters for a wrapped argument keeps those three
/// characters as spaces, which is the only way the alignment survives a different tab width.
/// </remarks>
internal sealed class SpacesToTabs : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.spacesToTabs";

    /// <inheritdoc />
    public override string Name => "Spaces to tabs";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["tabify", "entab", "tabs", "collapse indent"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var converted = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            converted[i] = ConvertLine(lines[i], options.IndentSize);
        }

        return converted;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = CountChanged(before, after);

        return changed == 0 ? "There is nothing to convert." : $"Converted {LineCount(changed)}.";
    }

    private static string ConvertLine(string line, int indentSize)
    {
        // Same guard as the inverse: whitespace-only lines are not indentation.
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var leading = Indentation.Length(line);

        if (leading == 0)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var spaces = 0;

        for (var i = 0; i < leading; i++)
        {
            if (line[i] == '\t')
            {
                // A tab ends whatever partial run of spaces preceded it. Those spaces
                // cannot be folded into it — they are before it, not part of it.
                builder.Append(' ', spaces).Append('\t');
                spaces = 0;
                continue;
            }

            spaces++;

            if (spaces == indentSize)
            {
                builder.Append('\t');
                spaces = 0;
            }
        }

        return builder.Append(' ', spaces).Append(line, leading, line.Length - leading).ToString();
    }
}

/// <summary>
/// Adds one level of indentation to every line.
/// </summary>
/// <remarks>
/// <para>
/// The line's own indentation decides the character: a line already starting with a tab gets
/// another tab, everything else gets <c>IndentSize</c> spaces. Guessing from the editor
/// setting alone would put spaces in front of a tab-indented file and produce exactly the
/// mixed indentation the other two transforms exist to clean up.
/// </para>
/// <para>
/// Blank lines are left alone. Indenting them adds trailing whitespace to a line that has
/// nothing on it, which is invisible here and turns up as a diff comment later.
/// </para>
/// </remarks>
internal sealed class Indent : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.indent";

    /// <inheritdoc />
    public override string Name => "Indent";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["indent", "shift right", "add indent", "nest"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var indented = new string[lines.Length];
        var spaces = new string(' ', options.IndentSize);

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[i];

            indented[i] = string.IsNullOrWhiteSpace(line)
                ? line
                : string.Concat(line[0] == '\t' ? "\t" : spaces, line);
        }

        return indented;
    }
}

/// <summary>
/// Removes one level of indentation from every line.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of <see cref="Indent"/>, and asymmetric where it has to be: it removes one
/// leading tab, or up to <c>IndentSize</c> leading spaces. <em>Up to</em>, because a line
/// indented by three spaces in a four-space document should end up at the margin rather than
/// being refused — a dedent that skips the lines that do not fit its arithmetic leaves the
/// block more crooked than it found it.
/// </para>
/// <para>
/// A line already at the margin is left alone rather than reported as an error. Dedenting a
/// block where the first line is at column zero is a normal thing to do.
/// </para>
/// </remarks>
internal sealed class Dedent : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.dedent";

    /// <inheritdoc />
    public override string Name => "Dedent";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["dedent", "outdent", "shift left", "unindent", "remove indent"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var dedented = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dedented[i] = Remove(lines[i], options.IndentSize);
        }

        return dedented;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = CountChanged(before, after);

        return changed == 0 ? "Nothing was indented." : $"Dedented {LineCount(changed)}.";
    }

    private static string Remove(string line, int indentSize)
    {
        // Whitespace-only lines are left alone rather than partially unindented: they carry
        // no content whose position could be wrong, and shortening them only churns the diff.
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        if (line[0] == '\t')
        {
            return line[1..];
        }

        var removed = 0;

        while (removed < indentSize && removed < line.Length && line[removed] == ' ')
        {
            removed++;
        }

        return removed == 0 ? line : line[removed..];
    }
}

/// <summary>Where a line's indentation ends.</summary>
internal static class Indentation
{
    /// <summary>The number of leading space and tab characters.</summary>
    /// <remarks>
    /// <para>
    /// Only space and tab, not <c>char.IsWhiteSpace</c>. A non-breaking space at the start of
    /// a line is content that arrived from a web page, and converting it to indentation —
    /// which is what the tab/space transforms would then do to it — changes what the text
    /// says.
    /// </para>
    /// <para>
    /// This is deliberately narrower than the rest of the file, which is not an
    /// inconsistency to be tidied away. "Trim trailing whitespace" and "collapse whitespace"
    /// are asked to remove <em>whitespace</em>, and a trailing U+00A0 is whitespace by any
    /// reading a user would give the word; indentation is a stricter idea than whitespace,
    /// and only these two transforms are making claims about it.
    /// </para>
    /// </remarks>
    public static int Length(string line)
    {
        var i = 0;

        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        return i;
    }
}
