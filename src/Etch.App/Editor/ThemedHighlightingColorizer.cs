using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace Etch.App.Editor;

/// <summary>
/// Draws a grammar's highlighting in Etch's colours instead of its own.
/// </summary>
/// <remarks>
/// <para>
/// The interception point is <see cref="ApplyColorToElement"/>, which every highlighted
/// run passes through on its way to the visual line. Substituting there rather than
/// editing the definition is what keeps this local: the shared, frozen, process-wide
/// grammar objects are only ever read, so two editors could use different palettes at once
/// and neither would be able to tell.
/// </para>
/// <para>
/// <b>Only the foreground is replaced.</b> Bold, italic and the rest are the grammar's
/// judgement about emphasis, not about colour, and they read the same in either theme. A
/// grammar colour with no foreground at all — <c>Punctuation</c> in the C# definition is
/// one — is passed straight through untouched rather than being given one, because
/// inventing a colour where the author deliberately left the text alone would be a change
/// to the grammar rather than to the palette.
/// </para>
/// </remarks>
internal sealed class ThemedHighlightingColorizer : HighlightingColorizer
{
    /// <summary>
    /// Translations already worked out, keyed by the grammar's colour object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApplyColorToElement"/> runs once per highlighted run per redraw, so this
    /// is on the path of every keystroke and every scroll. The colours arriving are a small
    /// fixed set of shared instances — a grammar has a few dozen and hands out the same
    /// objects for ever — so a cache here turns a bisection search per run into a dictionary
    /// probe.
    /// </para>
    /// <para>
    /// Keyed by <em>reference</em> deliberately.
    /// <see cref="HighlightingColor.GetHashCode"/> hashes the brushes and the font
    /// properties, which is both slower and, more to the point, would merge two colours that
    /// happen to agree today into one entry — harmless now, and exactly the sort of
    /// aliasing that becomes a puzzle later. The instances are stable, so reference identity
    /// is the cheaper and the more honest key.
    /// </para>
    /// </remarks>
    private readonly Dictionary<HighlightingColor, HighlightingColor> _translated =
        new(ReferenceEqualityComparer.Instance);

    private bool _dark;

    /// <param name="definition">The grammar to highlight with.</param>
    /// <param name="dark">Whether the dark theme is in use.</param>
    internal ThemedHighlightingColorizer(IHighlightingDefinition definition, bool dark)
        : base(definition)
    {
        _dark = dark;
    }

    /// <summary>
    /// Switches the palette to the other theme.
    /// </summary>
    /// <remarks>
    /// The cache is cleared rather than rebuilt: it is rebuilt lazily by the very next
    /// redraw, and a theme change already costs a full repaint. Returns whether anything
    /// changed so the caller can skip a redraw it does not need — WPF-UI raises its theme
    /// event for accent changes too, which do not affect any of this.
    /// </remarks>
    /// <param name="dark">Whether the dark theme is now in use.</param>
    /// <returns>True when the palette actually changed.</returns>
    internal bool Retheme(bool dark)
    {
        if (_dark == dark)
        {
            return false;
        }

        _dark = dark;
        _translated.Clear();

        return true;
    }

    /// <inheritdoc/>
    protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
    {
        ArgumentNullException.ThrowIfNull(color);

        base.ApplyColorToElement(element, Translate(color));
    }

    private HighlightingColor Translate(HighlightingColor color)
    {
        if (_translated.TryGetValue(color, out var cached))
        {
            return cached;
        }

        var translated = Build(color) ?? color;

        _translated[color] = translated;

        return translated;
    }

    /// <summary>
    /// The recoloured equivalent, or null when the original should be used as it is.
    /// </summary>
    /// <remarks>
    /// The clone is frozen before it is handed out. <c>HighlightingColor</c> is freezable
    /// precisely so that shared instances cannot be edited from under a renderer, and a
    /// cached one that is reachable from every redraw is exactly the case that protects.
    /// </remarks>
    private HighlightingColor? Build(HighlightingColor color)
    {
        if (color.Foreground is not { } foreground)
        {
            // Nothing to recolour. Weight and style still apply, so the grammar's emphasis
            // survives — this is a colour that only ever meant "make this bold".
            return null;
        }

        var role = SyntaxPalette.RoleOf(color.Name);

        Color? replacement;

        if (role != SyntaxRole.Unmapped)
        {
            replacement = SyntaxPalette.ForRole(role, _dark);
        }
        else
        {
            // A null context is the supported case — HighlightingBrush.GetColor documents
            // it as "context can be null!" — so this is not a gamble. A null *answer*
            // still is a real possibility: only SimpleHighlightingBrush is guaranteed to
            // resolve without a text view, and a colour that will not say what it is
            // cannot be made legible. Left alone, which at worst leaves one run in the
            // grammar's own colour.
            replacement = foreground.GetColor(null) is { } original
                ? SyntaxPalette.Rescue(original, _dark)
                : null;
        }

        if (replacement is not { } chosen)
        {
            return null;
        }

        var clone = color.Clone();

        clone.Foreground = new SimpleHighlightingBrush(chosen);
        clone.Freeze();

        return clone;
    }
}
