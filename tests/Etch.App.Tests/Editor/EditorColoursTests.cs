using System.Windows.Media;
using Etch.App.Editor;
using Xunit;

namespace Etch.App.Tests.Editor;

/// <summary>
/// The selection has to be readable whatever accent colour Windows is set to.
/// </summary>
/// <remarks>
/// <para>
/// This is the test the whole derivation exists for. A hand-picked selection colour can
/// only ever be checked by looking at it, on one machine, with one accent, and the defect
/// it replaced (AvalonEdit's default, legible over an opaque background and not over Mica)
/// is exactly the kind that survives that check. Sweeping the accent space turns "it looks
/// fine" into a property.
/// </para>
/// <para>
/// The thresholds asserted here are stated independently of the code under test, on
/// purpose: they are WCAG's numbers, not <see cref="EditorColours"/>'s. Only the
/// <em>inputs</em> come from the implementation, because a test that invented its own idea
/// of the theme's background would stop describing the application the moment the two
/// drifted apart.
/// </para>
/// </remarks>
public class EditorColoursTests
{
    /// <summary>Body-text contrast, WCAG 2.1 AA.</summary>
    private const double TextOnSelection = 4.5;

    /// <summary>The selection has to be distinguishable from the page it sits on.</summary>
    private const double SelectionOnSurface = 2.0;

    /// <summary>Non-text contrast for the selection's outline, WCAG 2.1 AA.</summary>
    private const double BorderOnSurface = 3.0;

    /// <summary>The accent Windows 11 ships with, for the tests that only need one.</summary>
    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_stays_readable_on_the_selection(bool dark)
    {
        var (surface, text) = EditorColours.Surfaces(dark);
        var ink = Flatten(text, surface);

        foreach (var accent in Accents())
        {
            var fill = Fill(EditorColours.Build(dark, accent));
            var contrast = EditorColours.Contrast(ink, fill);

            Assert.True(
                contrast >= TextOnSelection,
                $"{Describe(accent)} in {Theme(dark)} put the text at {contrast:0.00}:1 on its selection.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_selection_stands_out_from_the_page(bool dark)
    {
        var (surface, _) = EditorColours.Surfaces(dark);

        foreach (var accent in Accents())
        {
            var fill = Fill(EditorColours.Build(dark, accent));
            var contrast = EditorColours.Contrast(surface, fill);

            Assert.True(
                contrast >= SelectionOnSurface,
                $"{Describe(accent)} in {Theme(dark)} left the selection at {contrast:0.00}:1 against the page.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_selection_outline_stands_out_from_the_page(bool dark)
    {
        var (surface, _) = EditorColours.Surfaces(dark);

        foreach (var accent in Accents())
        {
            var pen = EditorColours.Build(dark, accent).SelectionBorder;
            Assert.NotNull(pen);

            var outline = Assert.IsType<SolidColorBrush>(pen.Brush).Color;
            var contrast = EditorColours.Contrast(surface, outline);

            Assert.True(
                contrast >= BorderOnSurface,
                $"{Describe(accent)} in {Theme(dark)} left the outline at {contrast:0.00}:1 against the page.");
        }
    }

    /// <summary>
    /// An accent that is already legible is used exactly as the user chose it.
    /// </summary>
    /// <remarks>
    /// The outline is the one place the accent survives unaltered, and it should: a
    /// derivation that quietly replaces a perfectly good accent with a computed
    /// approximation of it is a worse answer than not deriving anything.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_legible_accent_is_left_alone(bool dark)
    {
        var pen = EditorColours.Build(dark, DefaultAccent).SelectionBorder;
        Assert.NotNull(pen);

        Assert.Equal(DefaultAccent, Assert.IsType<SolidColorBrush>(pen.Brush).Color);
    }

    /// <summary>
    /// The fill is opaque, and that is the fix rather than an implementation detail.
    /// </summary>
    /// <remarks>
    /// Etch's editor is transparent over Mica, so a translucent selection composites
    /// against a blurred wallpaper, which is why the default one was hard to see, and why
    /// every contrast figure above would otherwise be a claim about a colour that never
    /// reaches the screen.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_selection_does_not_let_the_wallpaper_through(bool dark)
    {
        Assert.Equal(0xFF, Fill(EditorColours.Build(dark, DefaultAccent)).A);
    }

    /// <summary>The current line is the opposite case: it must tint Mica, not paint over it.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_current_line_is_a_tint_rather_than_a_band(bool dark)
    {
        var scheme = EditorColours.Build(dark, DefaultAccent);

        var fill = Assert.IsType<SolidColorBrush>(scheme.CurrentLineBackground);
        var border = Assert.IsType<SolidColorBrush>(scheme.CurrentLineBorder.Brush);

        Assert.InRange(fill.Color.A, (byte)1, (byte)0x40);
        Assert.InRange(border.Color.A, (byte)1, (byte)0x40);
        Assert.True(border.Color.A > fill.Color.A, "The current line's border should read stronger than its fill.");
    }

    /// <summary>
    /// Selected text keeps its own colour.
    /// </summary>
    /// <remarks>
    /// Asserted rather than left as a comment, because it is a decision whose consequence
    /// is dated to a later milestone. AvalonEdit's colorizer forces every selected run to a
    /// single brush when this is set, which would flatten syntax highlighting inside a
    /// selection the moment M3 turns highlighting on, and nothing else in the codebase
    /// would fail if somebody set it.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Selected_text_is_not_recoloured(bool dark)
    {
        Assert.Null(EditorColours.Build(dark, DefaultAccent).SelectionForeground);
    }

    /// <summary>High contrast defers to the system pair, and turns the current line off.</summary>
    [Fact]
    public void High_contrast_uses_the_system_selection_colours()
    {
        var scheme = EditorColours.HighContrast();

        Assert.Same(System.Windows.SystemColors.HighlightBrush, scheme.SelectionBackground);
        Assert.Same(System.Windows.SystemColors.HighlightTextBrush, scheme.SelectionForeground);
        Assert.Null(scheme.SelectionBorder);

        Assert.Equal(0x00, Assert.IsType<SolidColorBrush>(scheme.CurrentLineBackground).Color.A);
    }

    /// <summary>A brush the text view repaints with should not be tracking changes.</summary>
    [Fact]
    public void Everything_handed_to_the_editor_is_frozen()
    {
        var scheme = EditorColours.Build(dark: true, DefaultAccent);
        var pen = scheme.SelectionBorder;
        Assert.NotNull(pen);

        Assert.True(scheme.SelectionBackground.IsFrozen);
        Assert.True(pen.IsFrozen);
        Assert.True(scheme.CurrentLineBackground.IsFrozen);
        Assert.True(scheme.CurrentLineBorder.IsFrozen);
    }

    /// <summary>
    /// Every corner and edge of the accent cube, plus the middle of each face.
    /// </summary>
    /// <remarks>
    /// Windows lets the accent be any colour at all, including the ones that break a naive
    /// derivation: pure black, pure white, saturated primaries, and mid grey, which has no
    /// hue to preserve and sits near the luminance of neither theme's background.
    /// </remarks>
    private static IEnumerable<Color> Accents()
    {
        byte[] steps = [0x00, 0x40, 0x80, 0xC0, 0xFF];

        foreach (var red in steps)
        {
            foreach (var green in steps)
            {
                foreach (var blue in steps)
                {
                    yield return Color.FromRgb(red, green, blue);
                }
            }
        }
    }

    private static Color Fill(EditorColourScheme scheme) =>
        Assert.IsType<SolidColorBrush>(scheme.SelectionBackground).Color;

    /// <summary>
    /// Flattens the theme's foreground onto its background.
    /// </summary>
    /// <remarks>
    /// WPF-UI's light-theme text carries alpha, and a luminance taken from an unflattened
    /// colour means nothing. Flattening against the page understates how dark the ink ends
    /// up once it is drawn on the selection, so the assertions above take the conservative
    /// reading of the contrast rather than the flattering one.
    /// </remarks>
    private static Color Flatten(Color text, Color page)
    {
        if (text.A == 0xFF)
        {
            return text;
        }

        var alpha = text.A / 255.0;

        return Color.FromRgb(
            Mix(text.R, page.R, alpha),
            Mix(text.G, page.G, alpha),
            Mix(text.B, page.B, alpha));
    }

    private static byte Mix(byte over, byte under, double alpha) =>
        (byte)Math.Round((over * alpha) + (under * (1 - alpha)));

    private static string Describe(Color accent) => $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}";

    private static string Theme(bool dark) => dark ? "dark" : "light";
}
