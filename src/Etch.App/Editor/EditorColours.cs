using System.Windows;
using System.Windows.Media;

namespace Etch.App.Editor;

/// <summary>
/// The brushes AvalonEdit uses to draw a selection and the current line.
/// </summary>
/// <remarks>
/// <see cref="SelectionForeground"/> and <see cref="SelectionBorder"/> are nullable and
/// the current-line pair is not, and that asymmetry is deliberate. AvalonEdit's
/// <c>SelectionColorizer</c> documents and handles a null foreground — it leaves the
/// text's own colour alone — and <c>SelectionLayer</c> passes a null border straight to
/// <c>DrawGeometry</c>, which accepts one. Nothing upstream says what a null current-line
/// brush or pen does, so where Etch wants no current-line decoration it passes a
/// transparent brush rather than betting on the unverified case.
/// </remarks>
internal sealed record EditorColourScheme(
    Brush SelectionBackground,
    Pen? SelectionBorder,
    Brush? SelectionForeground,
    Brush CurrentLineBackground,
    Pen CurrentLineBorder);

/// <summary>
/// Derives selection and current-line colours that stay legible in either theme.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit's defaults do not survive this window. Its selection is the system
/// highlight colour at 70% opacity with the selected text forced to <c>HighlightText</c>,
/// which over an opaque editor background is fine — but Etch's editor is transparent over
/// Mica, so the fill composites against a blurred wallpaper nobody chose for its contrast.
/// In dark mode the result sinks into the page; in light mode white on 70% blue lands
/// around 2.9:1, below any readable threshold. The current-line default is worse: a
/// translucent cyan wash inside a green border, which in a Fluent window reads as a
/// rendering fault rather than a feature.
/// </para>
/// <para>
/// So the colours are derived rather than picked — from the user's accent and the theme,
/// against two stated thresholds. Deriving them is the point: it lets a test assert the
/// promise for every accent a user could possibly choose, where a hand-picked pair would
/// only ever be an opinion about one.
/// </para>
/// </remarks>
internal static class EditorColours
{
    /// <summary>Contrast the text must reach against the selection it sits on.</summary>
    /// <remarks>WCAG 2.1 AA for body text. This is the one that cannot be traded away.</remarks>
    private const double TextOnSelection = 4.5;

    /// <summary>Contrast the selection must reach against the page around it.</summary>
    /// <remarks>
    /// Deliberately below the 3:1 that WCAG asks of a UI component: the selection is not
    /// the only thing marking itself out — it also carries an accent outline, which is
    /// held to 3:1 separately. Asking 3:1 of the fill as well squeezes the feasible band
    /// to roughly four percentage points of luminance, which is a tighter constraint than
    /// the problem deserves and leaves nothing for the accent's own hue to show through.
    /// </remarks>
    private const double SelectionOnSurface = 2.0;

    /// <summary>Contrast the selection's outline must reach against the page.</summary>
    private const double BorderOnSurface = 3.0;

    /// <summary>The constant WCAG adds to both luminances before dividing.</summary>
    private const double Offset = 0.05;

    /// <summary>Ink alpha for the current line's fill and its border.</summary>
    /// <remarks>
    /// Translucent, unlike the selection, and for the opposite reason. The selection is a
    /// deliberate block and wants to be independent of whatever Mica is showing; the
    /// current line is a hint and must tint the backdrop rather than paint over it, or it
    /// becomes a flat stripe that visibly does not belong to the surface around it.
    /// </remarks>
    private const byte CurrentLineFillAlpha = 0x14;

    /// <inheritdoc cref="CurrentLineFillAlpha"/>
    private const byte CurrentLineBorderAlpha = 0x2E;

    /// <summary>
    /// The colour behind the editor's text, and the colour of the text itself.
    /// </summary>
    /// <remarks>
    /// Constants rather than a resource lookup, for two reasons. The surface the selection
    /// actually composites against is Mica, which is no resource at all — these are the
    /// application background colours WPF-UI tints it towards, and they are the closest
    /// honest stand-in. And reading <c>TextFillColorPrimaryBrush</c> live would make the
    /// result depend on whether the theme dictionaries had been re-merged yet at the
    /// moment of the call, which is exactly the kind of ordering question this code should
    /// not have. They are inputs to a contrast calculation, not to rendering.
    /// </remarks>
    internal static (Color Surface, Color Text) Surfaces(bool dark) => dark
        ? (Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0xFF, 0xFF, 0xFF))
        : (Color.FromRgb(0xF3, 0xF3, 0xF3), Color.FromArgb(0xE4, 0x00, 0x00, 0x00));

    /// <summary>Builds the scheme for a theme and a system accent colour.</summary>
    internal static EditorColourScheme Build(bool dark, Color accent)
    {
        var (surface, text) = Surfaces(dark);
        return Build(surface, text, accent);
    }

    /// <summary>
    /// Hands the whole question to the system, because in high contrast it is the system's.
    /// </summary>
    /// <remarks>
    /// A high-contrast theme is a declaration by the user about what they can see, and
    /// <c>Highlight</c> paired with <c>HighlightText</c> is the only selection pairing it
    /// guarantees — so this is the one case where the selected text's own colour is
    /// overridden. The current line is turned off rather than reinvented: extra washes and
    /// outlines are precisely the decoration a high-contrast theme exists to remove.
    /// </remarks>
    internal static EditorColourScheme HighContrast() => new(
        SelectionBackground: SystemColors.HighlightBrush,
        SelectionBorder: null,
        SelectionForeground: SystemColors.HighlightTextBrush,
        CurrentLineBackground: Brushes.Transparent,
        CurrentLineBorder: FrozenPen(Colors.Transparent));

    private static EditorColourScheme Build(Color surface, Color text, Color accent)
    {
        // The page is opaque by definition — it is what everything else composites onto.
        var page = Color.FromRgb(surface.R, surface.G, surface.B);

        // WPF-UI's light-theme foreground carries alpha, so the ink has to be flattened
        // before its luminance means anything. Flattening it against the page rather than
        // against the selection understates how dark it ends up on the selection, which
        // errs towards more contrast than the calculation claims, not less.
        var ink = Composite(text, page);

        var fill = AtLuminance(accent, SelectionTarget(Luminance(page), Luminance(ink)));

        return new EditorColourScheme(
            SelectionBackground: FrozenBrush(fill),

            // A crisp accent edge is what keeps the selection readable over an unusually
            // light or dark patch of wallpaper, and it is the only part of the selection
            // whose contrast does not depend on the fill's compromise between two bounds.
            SelectionBorder: FrozenPen(Legible(accent, page, BorderOnSurface)),

            // Null on purpose. AvalonEdit forces selected text to a single colour, which
            // would flatten syntax highlighting the moment M3 turns it on; the fill above
            // is built to be readable against the text's own colour instead.
            SelectionForeground: null,

            CurrentLineBackground: FrozenBrush(WithAlpha(ink, CurrentLineFillAlpha)),
            CurrentLineBorder: FrozenPen(WithAlpha(ink, CurrentLineBorderAlpha)));
    }

    /// <summary>
    /// Picks the luminance the selection should land on.
    /// </summary>
    /// <remarks>
    /// Two requirements pull in opposite directions: the fill has to be far enough from
    /// the text to read, and far enough from the page to be seen. Both are ratios against
    /// fixed colours, so each turns into a bound on the fill's luminance, and any value
    /// between them satisfies both at once. The midpoint is taken rather than either edge
    /// so that rounding to a byte cannot push the result out of the band.
    /// </remarks>
    private static double SelectionTarget(double surface, double text)
    {
        double low, high;

        if (text <= surface)
        {
            // Light theme: dark ink on a bright page. The fill must be bright enough for
            // the ink to read on, and dim enough to be told apart from the page.
            low = ((text + Offset) * TextOnSelection) - Offset;
            high = ((surface + Offset) / SelectionOnSurface) - Offset;
        }
        else
        {
            low = ((surface + Offset) * SelectionOnSurface) - Offset;
            high = ((text + Offset) / TextOnSelection) - Offset;
        }

        // A theme whose own text barely contrasts with its own background leaves no band.
        // Etch's constants are nowhere near that, but the arithmetic should say what it
        // would do rather than silently return a midpoint of a band that does not exist:
        // readability wins, and the bound it imposes is the answer.
        return low > high
            ? Math.Clamp(text <= surface ? low : high, 0, 1)
            : Math.Clamp((low + high) / 2, 0, 1);
    }

    /// <summary>Leaves a colour alone if it already contrasts, and moves it if it does not.</summary>
    /// <remarks>
    /// <para>
    /// The accent as the user chose it, whenever it is legible as-is — a derived colour
    /// that quietly replaces a perfectly good accent is a worse answer than no derivation.
    /// </para>
    /// <para>
    /// When it does have to move, the search tests the ratio against the <em>rounded</em>
    /// colour and carries the last blend known to clear it. Solving for the luminance the
    /// ratio implies and rounding to bytes afterwards lands on whichever side of the
    /// boundary the eighth bit falls — a coin toss against a requirement, and it came up
    /// tails for about a fifth of the accent space. Carrying a known-good bound makes the
    /// answer satisfy the ratio by construction rather than by luck.
    /// </para>
    /// </remarks>
    private static Color Legible(Color colour, Color page, double minimum)
    {
        if (Contrast(colour, page) >= minimum)
        {
            return colour;
        }

        // Whichever of black and white is further from the page. Not "brighter if the page
        // is dark": a mid-grey page is closer to white than 3:1 in one direction while
        // being ten times that in the other, so the direction has to be measured, not
        // assumed. Whichever wins clears at least 4.58:1 against any page there is — that
        // is the ratio at the crossover — so it is always a valid answer to fall back on.
        var anchor = Contrast(Colors.White, page) >= Contrast(Colors.Black, page)
            ? Colors.White
            : Colors.Black;

        var best = anchor;
        double low = 0, high = 1;

        for (var step = 0; step < 20; step++)
        {
            var middle = (low + high) / 2;
            var candidate = Blend(colour, anchor, middle);

            if (Contrast(candidate, page) >= minimum)
            {
                // Clears it, so keep searching for a blend that keeps more of the accent.
                best = candidate;
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return best;
    }

    /// <summary>Tints or shades a colour until it reaches a target relative luminance.</summary>
    /// <remarks>
    /// Bisection rather than an analytic solve. Luminance rises monotonically along a
    /// straight sRGB blend towards white and falls towards black, so twenty halvings land
    /// well inside the rounding error of a byte, and no special case is needed for a
    /// colour that is already grey, black or white. The blend runs in sRGB rather than
    /// linear light because what is being adjusted is the accent's appearance, not its
    /// energy — mixing towards white in linear space washes the hue out much faster than
    /// anyone expects a tint to.
    /// </remarks>
    private static Color AtLuminance(Color seed, double target)
    {
        var towardsWhite = Luminance(seed) < target;
        var anchor = towardsWhite ? Colors.White : Colors.Black;

        double low = 0, high = 1;

        for (var step = 0; step < 20; step++)
        {
            var middle = (low + high) / 2;
            var luminance = Luminance(Blend(seed, anchor, middle));

            if (towardsWhite ? luminance > target : luminance < target)
            {
                high = middle;
            }
            else
            {
                low = middle;
            }
        }

        return Blend(seed, anchor, (low + high) / 2);
    }

    /// <summary>The WCAG contrast ratio between two opaque colours.</summary>
    internal static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);

        return (Math.Max(a, b) + Offset) / (Math.Min(a, b) + Offset);
    }

    /// <summary>WCAG relative luminance. Alpha is ignored; flatten the colour first.</summary>
    internal static double Luminance(Color colour) =>
        (0.2126 * Linear(colour.R)) + (0.7152 * Linear(colour.G)) + (0.0722 * Linear(colour.B));

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;

        return value <= 0.03928
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    /// <summary>Flattens a colour with alpha onto an opaque one.</summary>
    private static Color Composite(Color over, Color under)
    {
        var alpha = over.A / 255.0;

        return Color.FromRgb(
            Mix(over.R, under.R, alpha),
            Mix(over.G, under.G, alpha),
            Mix(over.B, under.B, alpha));
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        Mix(to.R, from.R, amount),
        Mix(to.G, from.G, amount),
        Mix(to.B, from.B, amount));

    private static byte Mix(byte foreground, byte background, double weight) =>
        (byte)Math.Clamp(Math.Round((foreground * weight) + (background * (1 - weight))), 0, 255);

    private static Color WithAlpha(Color colour, byte alpha) =>
        Color.FromArgb(alpha, colour.R, colour.G, colour.B);

    /// <summary>
    /// Brushes and pens are frozen before they leave here.
    /// </summary>
    /// <remarks>
    /// A frozen <see cref="Freezable"/> skips change tracking and can be rendered without
    /// taking a lock, which matters for two objects the text view touches on every
    /// repaint. A pen can only be frozen once its brush is, hence the order.
    /// </remarks>
    private static SolidColorBrush FrozenBrush(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();

        return brush;
    }

    private static Pen FrozenPen(Color colour)
    {
        var pen = new Pen(FrozenBrush(colour), 1);
        pen.Freeze();

        return pen;
    }
}
