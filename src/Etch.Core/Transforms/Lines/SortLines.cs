using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Lines;

/// <summary>
/// Sorts the lines of the text.
/// </summary>
/// <remarks>
/// <para>
/// Ordinal, and that is a decision rather than a default. Culture-aware sorting puts
/// <c>_id</c> in a different place depending on the machine's locale, which is exactly
/// what someone sorting two files to diff them does not want. It also matches what
/// every command-line <c>sort</c> a developer has used does.
/// </para>
/// <para>
/// Available for any buffer, because "sort these lines" is a question about a list and
/// no detector can tell you whether something is a list. It is reachable by searching
/// for it and never suggested: <c>Ctrl+Enter</c> on a JSON document should not
/// scramble it.
/// </para>
/// </remarks>
internal sealed class SortLines : ITransform
{
    /// <inheritdoc />
    public string Id => "text.sortLines";

    /// <inheritdoc />
    public string Name => "Sort lines";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Text;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["order lines", "alphabetise lines", "sort"];

    /// <inheritdoc />
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        // TextLines owns the two details every line transform gets wrong on its own: a
        // trailing newline is not an empty last line, and a CRLF buffer's CR must come off
        // before the text is compared and go back on after. Sorting was where both were
        // first worked out; they live there now because eleven transforms need them.
        var lines = TextLines.Split(input.Text, out var trailingNewLine);

        if (lines.Length == 0)
        {
            return TransformResult.Failed("There is nothing here to sort.");
        }

        if (lines.Length == 1)
        {
            return TransformResult.Failed("There is only one line to sort.");
        }

        Array.Sort(lines, StringComparer.Ordinal);

        return TransformResult.Ok(TextLines.Join(lines, trailingNewLine, input.Options.NewLine));
    }
}
