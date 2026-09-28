using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Identity;

/// <summary>
/// Produces a fresh GUID.
/// </summary>
/// <remarks>
/// <para>
/// The one transform that is not a function of its input, which makes it the one that has
/// to be careful about what it does to that input. Three behaviours, and the rule is that
/// none of them throws anything away:
/// </para>
/// <list type="bullet">
/// <item><b>With a selection</b>, the selection becomes the GUID. This is the useful case:
/// highlight the placeholder in a config file, replace it.</item>
/// <item><b>On an empty tab</b>, the tab becomes the GUID. The normal starting point, and
/// the reason <see cref="NeedsInput"/> exists.</item>
/// <item><b>On a tab with content and no selection</b>: the GUID is <em>appended</em> on
/// its own line. Replacing a buffer full of notes with 36 characters would be technically
/// consistent with every other transform and completely indefensible; one Ctrl+Z is not
/// much comfort when the alternative was never to have done it.</item>
/// </list>
/// <para>
/// Lower case, dashed, unbraced: the "D" format, which is what .NET, PostgreSQL and every
/// JSON API print, and what <c>GuidDetector</c> recognises so that the result is
/// immediately detected as what it is.
/// </para>
/// </remarks>
internal sealed class NewGuid : ITransform
{
    /// <inheritdoc />
    public string Id => "guid.new";

    /// <inheritdoc />
    public string Name => "New GUID";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Identity;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["uuid", "generate guid", "new id", "random id"];

    /// <inheritdoc />
    public bool NeedsInput => false;

    /// <inheritdoc />
    /// <remarks>
    /// Never suggested. A buffer that already holds a GUID is the last place another one
    /// belongs, and that is exactly the buffer the detector would fire on.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        // Version 4, from the platform's cryptographic RNG. Guid.NewGuid is not documented
        // as cryptographically secure and should not be relied on for secrets, but a
        // scratchpad's identifiers are not secrets, and the alternative would be inventing
        // a byte layout by hand.
        var value = Guid.NewGuid().ToString("D");

        if (input.WasSelection || input.Text.Length == 0)
        {
            return TransformResult.Ok(value, FormatId.Guid);
        }

        // Only one separator, even when the buffer already ends in a newline: appending a
        // blank line every time this is pressed turns a list of ten identifiers into
        // nineteen lines.
        //
        // The rest of the buffer is concatenated, not normalised. The only ending this
        // transform introduces is the separator, which already is the document's own, and
        // rewriting every other line's ending as a side effect of generating an identifier
        // would turn a one-line addition into a whole-file diff.
        var separator = EndsWithNewLine(input.Text) ? string.Empty : input.Options.NewLine;

        return TransformResult.Ok(
            string.Concat(input.Text, separator, value),
            resultingFormat: null,
            message: $"Appended {value}.");
    }

    private static bool EndsWithNewLine(string text) => text.Length > 0 && text[^1] is '\n' or '\r';
}
