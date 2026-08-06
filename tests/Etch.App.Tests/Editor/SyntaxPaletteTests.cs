using System.Windows.Media;
using Etch.App.Editor;
using Xunit;

namespace Etch.App.Tests.Editor;

/// <summary>
/// Every syntax colour has to be readable in both themes.
/// </summary>
/// <remarks>
/// <para>
/// The same argument as <see cref="EditorColoursTests"/>, applied to a bigger surface. The
/// defect being guarded against is the one AvalonEdit ships with: colours chosen against a
/// white page, which look considered until the editor turns dark. A palette that is only
/// ever checked by looking at it will acquire that defect again the first time a role is
/// added and nobody thinks to relaunch in dark mode.
/// </para>
/// <para>
/// The ratio asserted here is WCAG's, stated independently of the implementation, and the
/// page comes from <see cref="EditorColours.Surfaces"/> because a test that invented its
/// own idea of the background would stop describing Etch the moment the two drifted apart.
/// </para>
/// </remarks>
public class SyntaxPaletteTests
{
    /// <summary>Body-text contrast, WCAG 2.1 AA. Source code is read for hours.</summary>
    private const double BodyText = 4.5;

    private static readonly SyntaxRole[] Roles = Enum.GetValues<SyntaxRole>();

    private static Color Page(bool dark)
    {
        var (surface, _) = EditorColours.Surfaces(dark);

        return Color.FromRgb(surface.R, surface.G, surface.B);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_role_is_readable_against_its_page(bool dark)
    {
        var page = Page(dark);

        foreach (var role in Roles)
        {
            if (role == SyntaxRole.Unmapped)
            {
                // Has no colour of its own by definition — it is the signal to keep the
                // grammar's, which Rescue is responsible for and is covered below.
                Assert.Null(SyntaxPalette.ForRole(role, dark));
                continue;
            }

            var colour = SyntaxPalette.ForRole(role, dark);

            Assert.NotNull(colour);

            Assert.True(
                EditorColours.Contrast(colour.Value, page) >= BodyText,
                $"{role} in {(dark ? "dark" : "light")} reaches only " +
                $"{EditorColours.Contrast(colour.Value, page):0.00}:1 against the page.");
        }
    }

    /// <summary>
    /// How far apart two roles must be to count as different colours.
    /// </summary>
    /// <remarks>
    /// Well below the palette's actual minimum, which is around 58. The number is a
    /// tripwire for a collision, not a design constraint — asserting the real separation
    /// would turn every deliberate adjustment into a failing test, which is how a test
    /// stops being read and starts being edited until it passes.
    /// </remarks>
    private const double MinimumSeparation = 40;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Roles_are_told_apart_from_one_another(bool dark)
    {
        // A palette where every colour clears the contrast floor but three of them are the
        // same colour would pass the test above and be useless. Distinctness is the other
        // half of the promise, and it is the half a legibility floor can quietly destroy by
        // dragging two nearby hues onto the same value.
        //
        // This is not hypothetical: Tag and Keyword were the same blue in dark mode when
        // this was first written, borrowed from editors where no document shows both at
        // once. HTML embeds JavaScript, so Etch does.
        var resolved = Roles
            .Where(static role => role != SyntaxRole.Unmapped)
            .Select(role => (Role: role, Colour: SyntaxPalette.ForRole(role, dark)!.Value))
            .ToList();

        foreach (var (role, colour) in resolved)
        {
            foreach (var (otherRole, otherColour) in resolved)
            {
                if (role == otherRole)
                {
                    continue;
                }

                Assert.True(
                    Separation(colour, otherColour) >= MinimumSeparation,
                    $"{role} and {otherRole} are only {Separation(colour, otherColour):0} apart " +
                    $"in {(dark ? "dark" : "light")} ({colour} against {otherColour}).");
            }
        }
    }

    /// <summary>
    /// Roughly how different two colours look.
    /// </summary>
    /// <remarks>
    /// The "redmean" weighting rather than a plain distance through RGB, because a plain
    /// one badly overstates how different two blues are and understates two greens — which
    /// would let the exact collision this guards against slip through between two hues the
    /// eye cannot separate. Approximate on purpose: the assertion is that two colours are
    /// not the same, and that does not need a colour-appearance model.
    /// </remarks>
    private static double Separation(Color first, Color second)
    {
        var meanRed = (first.R + second.R) / 2.0;

        double red = first.R - second.R;
        double green = first.G - second.G;
        double blue = first.B - second.B;

        return Math.Sqrt(
            ((2 + (meanRed / 256)) * red * red)
            + (4 * green * green)
            + ((2 + ((255 - meanRed) / 256)) * blue * blue));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_inherited_colour_is_dragged_up_to_the_floor(bool dark)
    {
        var page = Page(dark);

        // The colours the built-in grammars actually name, and the reason this class
        // exists: Green comments and Blue strings over a #202020 page, Black over anything.
        Color[] inherited = [Colors.Green, Colors.Blue, Colors.Black, Colors.White, Colors.Navy, Colors.DarkBlue];

        foreach (var original in inherited)
        {
            var rescued = SyntaxPalette.Rescue(original, dark);

            Assert.True(
                EditorColours.Contrast(rescued, page) >= BodyText,
                $"{original} rescued to {rescued} reaches only " +
                $"{EditorColours.Contrast(rescued, page):0.00}:1.");
        }
    }

    [Fact]
    public void A_colour_that_already_reads_is_left_exactly_as_its_author_wrote_it()
    {
        // The outcome to want. A grammar that happened to pick something readable should
        // come through untouched, or every definition ends up wearing Etch's opinion of it.
        Assert.Equal(Colors.White, SyntaxPalette.Rescue(Colors.White, dark: true));
        Assert.Equal(Colors.Black, SyntaxPalette.Rescue(Colors.Black, dark: false));
    }

    /// <summary>
    /// A colour name the grammars actually use resolves to the role Etch means by it.
    /// </summary>
    /// <remarks>
    /// A <c>[Fact]</c> holding a table, not a <c>[Theory]</c> with one row per case, and the
    /// reason is a language rule rather than a preference: <see cref="SyntaxRole"/> is
    /// <c>internal</c>, and <c>InternalsVisibleTo</c> grants <em>access</em> without granting
    /// <em>accessibility</em> — so a public method may use the type inside its body but may
    /// not name it in its signature. xUnit requires test methods to be public, which makes
    /// <c>[Theory] … (string, SyntaxRole)</c> a CS0051 rather than a design choice.
    /// </remarks>
    [Fact]
    public void A_named_colour_maps_to_its_role()
    {
        (string Name, SyntaxRole Expected)[] cases =
        [
            ("Comment", SyntaxRole.Comment),
            ("String", SyntaxRole.String),
            ("Digits", SyntaxRole.Number),
            ("NumberLiteral", SyntaxRole.Number),
            ("Punctuation", SyntaxRole.Punctuation),
            ("MethodCall", SyntaxRole.Function),
            ("HtmlTag", SyntaxRole.Tag),
            ("Attributes", SyntaxRole.Attribute),
        ];

        foreach (var (name, expected) in cases)
        {
            Assert.Equal(expected, SyntaxPalette.RoleOf(name));
        }
    }

    [Theory]
    [InlineData("Keywords")]
    [InlineData("GotoKeywords")]
    [InlineData("ExceptionKeywords")]
    [InlineData("ContextKeywords")]
    [InlineData("SelectionStatements")]
    [InlineData("IterationStatements")]
    [InlineData("Modifiers")]
    [InlineData("AccessModifiers")]
    public void Anything_named_after_keywords_is_a_keyword(string name)
    {
        // Twenty-odd of the hundred and sixteen names the built-in grammars use are some
        // flavour of keyword. The suffix rule is a claim about how these files are named,
        // which is more durable than an inventory of them — and these are real names, read
        // out of the shipped .xshd resources rather than invented here.
        Assert.Equal(SyntaxRole.Keyword, SyntaxPalette.RoleOf(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("JavaScriptGlobalFunctions")]
    [InlineData("XmlDoc")]
    public void An_unrecognised_or_missing_name_is_unmapped(string? name)
    {
        // Null is the common case, not an edge one: most rules in these grammars specify a
        // colour inline rather than referring to a named one, so the name really is absent.
        Assert.Equal(SyntaxRole.Unmapped, SyntaxPalette.RoleOf(name));
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case()
    {
        Assert.Equal(SyntaxRole.Comment, SyntaxPalette.RoleOf("comment"));
        Assert.Equal(SyntaxRole.Keyword, SyntaxPalette.RoleOf("gotoKEYWORDS"));
    }

    [Fact]
    public void The_two_themes_do_not_share_a_palette()
    {
        // Not a style preference. If a role resolved to the same colour in both themes it
        // would be because the legibility floor had dragged it to the same place from two
        // directions, which means one of the two seeds was never doing anything.
        foreach (var role in Roles)
        {
            if (role == SyntaxRole.Unmapped)
            {
                continue;
            }

            Assert.NotEqual(SyntaxPalette.ForRole(role, dark: true), SyntaxPalette.ForRole(role, dark: false));
        }
    }
}
