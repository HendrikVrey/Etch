using System.Text;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Lines;

/// <summary>
/// Collapses the lines into one comma-separated line.
/// </summary>
/// <remarks>
/// <para>
/// <b>The delimiter is a comma, and that is a limitation rather than a decision.</b> The
/// plan asks for "join/split by delimiter", which needs somewhere for the user to type the
/// delimiter — and the palette has a query box, not an argument box. Inventing one is a
/// feature in its own right and it is the same feature that regex find-and-replace needs,
/// so both wait for it together. Until then, the comma is what people paste a column of
/// values into an <c>IN (…)</c> clause with, which is the request this actually serves.
/// </para>
/// <para>
/// <c>", "</c> with the space, because the output is meant to be read. Splitting is
/// tolerant about the space, so the pair still round-trips.
/// </para>
/// </remarks>
internal sealed class JoinLinesWithCommas : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.joinCommas";

    /// <inheritdoc />
    public override string Name => "Join lines with commas";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["join", "one line", "comma separate", "csv", "implode", "in clause"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(lines[i]);
        }

        return [builder.ToString()];
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        before.Count == 1 ? "There is only one line to join." : $"Joined {LineCount(before.Count)}.";
}

/// <summary>
/// Breaks a comma-separated line into one line per value.
/// </summary>
/// <remarks>
/// <para>
/// The inverse, and the more useful of the two: it is what turns a log line's list of ids
/// into something the line transforms can then sort, deduplicate and count.
/// </para>
/// <para>
/// <b>Not a CSV parser, and it does not pretend to be.</b> Quoting and embedded commas are
/// real CSV, and real CSV needs a real parser with a real detector behind it — that is a
/// v1.1 item in the plan. This splits on commas. Text where that is the wrong answer is
/// text where the user can see it is the wrong answer immediately, which is the honest
/// failure mode for a transform this simple.
/// </para>
/// <para>
/// Whitespace around each value is trimmed, so the space in <c>", "</c> does not become the
/// first character of every line. Empty values are kept: <c>a,,b</c> is three values and
/// silently dropping the middle one would lose the fact that it was there.
/// </para>
/// </remarks>
internal sealed class SplitOnCommas : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.splitCommas";

    /// <inheritdoc />
    public override string Name => "Split on commas into lines";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["split", "explode", "one per line", "comma", "unjoin"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var produced = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A line with no comma passes through untouched rather than being trimmed. The
            // trim exists to undo the space in ", " — applying it to a line this transform
            // did not split would silently strip the indentation off a block while the
            // status bar said "there were no commas to split on".
            if (!line.Contains(',', StringComparison.Ordinal))
            {
                produced.Add(line);
                continue;
            }

            // Every line is split, not just the first. Two comma-separated lines produce
            // one flat list, which is what someone who pasted two rows expects, and a
            // single line is the same code path with nothing extra.
            foreach (var part in line.Split(','))
            {
                produced.Add(part.Trim());
            }
        }

        return produced;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        before.Count == after.Count
            ? "There were no commas to split on."
            : $"Split into {LineCount(after.Count)}.";
}
