using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Lines;

/// <summary>
/// The shared body of the line transforms.
/// </summary>
/// <remarks>
/// All of them do the same three things — split, rework the list, join — and the split and
/// the join are exactly where the trailing-newline and CRLF mistakes live. None of them is
/// ever the suggested action: no detector can tell you that a buffer is a list, which is
/// the only thing that would make reordering or deleting lines an obvious move.
/// </remarks>
internal abstract class LineTransform : ITransform
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Text;

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Aliases { get; }

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var lines = TextLines.Split(input.Text, out var trailingNewLine);

        if (lines.Length == 0)
        {
            return TransformResult.Failed("There are no lines here.");
        }

        var reworked = Rework(lines, input.Options, cancellationToken);
        var message = Describe(lines, reworked);

        // Nothing changed: report it instead of writing an identical copy back. The editor
        // does not diff before replacing, so returning Ok here would cost an undo step and
        // the caret position to produce a byte-for-byte identical document — which is the
        // exact case TransformResult.Reported was added for.
        if (Unchanged(lines, reworked))
        {
            return TransformResult.Reported(message ?? $"{Name} changed nothing.");
        }

        return TransformResult.Ok(
            TextLines.Join(reworked, trailingNewLine, input.Options.NewLine),
            resultingFormat: null,
            message);
    }

    /// <summary>Whether a rework produced the same lines it was given.</summary>
    /// <remarks>
    /// The two parameters are deliberately different types rather than both being
    /// <c>IReadOnlyList</c>. <c>before</c> is always the array <c>TextLines.Split</c>
    /// returned, so taking it as one indexes it directly instead of through an interface;
    /// <c>after</c> is whatever a <c>Rework</c> override chose to build. The asymmetry also
    /// makes the arguments impossible to pass in the wrong order.
    /// </remarks>
    private static bool Unchanged(string[] before, IReadOnlyList<string> after)
    {
        if (before.Length != after.Count)
        {
            return false;
        }

        for (var i = 0; i < before.Length; i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Produces the new list of lines.
    /// </summary>
    /// <remarks>
    /// Must not modify <paramref name="lines"/> in place. <see cref="Describe"/> is handed
    /// both lists and compares them, so an implementation that reversed or rewrote the
    /// input array would be handing it the same object twice and reporting that nothing
    /// had changed.
    /// </remarks>
    protected abstract IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken);

    /// <summary>What to say afterwards, or null for the default message.</summary>
    /// <remarks>
    /// Overridden by everything whose effect can be invisible. "Applied" is a poor answer
    /// when the change is that some lines are gone, and a worse one when the answer is that
    /// there was nothing to do — someone who presses "remove blank lines" and is told
    /// "applied" has learnt nothing about why the document looks the same.
    /// </remarks>
    protected virtual string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after) => null;

    /// <summary>"1 line" / "3 lines".</summary>
    /// <remarks>
    /// Not called <c>Lines</c>: this namespace is <c>Etch.Core.Transforms.Lines</c>, and a
    /// member sharing a name with its own enclosing namespace is the shape of trouble that
    /// only announces itself in the error message of some unrelated later edit.
    /// </remarks>
    protected static string LineCount(int count) => count == 1 ? "1 line" : $"{count} lines";

    /// <summary>How many entries differ, position by position.</summary>
    /// <remarks>
    /// Only meaningful for the transforms that rewrite lines rather than add or remove
    /// them; those compare counts instead.
    /// </remarks>
    protected static int CountChanged(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var changed = 0;
        var shared = Math.Min(before.Count, after.Count);

        for (var i = 0; i < shared; i++)
        {
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return changed + Math.Abs(before.Count - after.Count);
    }
}

/// <summary>
/// Reverses the order of the lines.
/// </summary>
/// <remarks>
/// The log-file transform: newest entries are at the bottom of a file and at the top of
/// everything that reads one.
/// </remarks>
internal sealed class ReverseLines : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.reverseLines";

    /// <inheritdoc />
    public override string Name => "Reverse lines";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["reverse", "flip", "invert order", "backwards"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        // A copy rather than Array.Reverse in place: the base class compares the input list
        // against the output one, and reversing the array it was given would make those two
        // the same object.
        var reversed = new string[lines.Length];

        for (var i = 0; i < lines.Length; i++)
        {
            reversed[i] = lines[lines.Length - 1 - i];
        }

        return reversed;
    }
}

/// <summary>
/// Removes repeated lines, keeping the first of each.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is preserved and the survivor is the first occurrence</b>, which is what
/// separates this from sorting and deduplicating in one step. A list of error messages
/// deduplicated in place still reads in the order the errors happened; sorted first, it
/// does not.
/// </para>
/// <para>
/// Ordinal comparison, matching <c>SortLines</c>. Two lines differing only in case are two
/// lines — this is not the transform to decide otherwise, because the decision is
/// irreversible.
/// </para>
/// </remarks>
internal sealed class DedupeLines : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.dedupeLines";

    /// <inheritdoc />
    public override string Name => "Remove duplicate lines";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["dedupe", "deduplicate", "unique", "distinct", "uniq"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(lines.Length, StringComparer.Ordinal);
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (seen.Add(line))
            {
                kept.Add(line);
            }
        }

        return kept;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        before.Count == after.Count
            ? "Every line was already unique."
            : $"Removed {LineCount(before.Count - after.Count)}.";
}

/// <summary>
/// Removes lines that hold nothing but whitespace.
/// </summary>
/// <remarks>
/// Whitespace-only counts as blank, not just zero-length. A "blank" line with two spaces on
/// it is blank to the person looking at the screen, and a transform that left it behind
/// would look broken.
/// </remarks>
internal sealed class RemoveBlankLines : LineTransform
{
    /// <inheritdoc />
    public override string Id => "text.removeBlankLines";

    /// <inheritdoc />
    public override string Name => "Remove blank lines";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["blank", "empty lines", "compact", "squeeze"];

    /// <inheritdoc />
    protected override IReadOnlyList<string> Rework(
        string[] lines,
        TransformOptions options,
        CancellationToken cancellationToken)
    {
        var kept = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                kept.Add(line);
            }
        }

        return kept;
    }

    /// <inheritdoc />
    protected override string? Describe(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        before.Count == after.Count
            ? "There were no blank lines."
            : $"Removed {LineCount(before.Count - after.Count)}.";
}
