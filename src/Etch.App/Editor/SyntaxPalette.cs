using System.Windows.Media;

namespace Etch.App.Editor;

/// <summary>
/// The kind of thing a highlighting colour is colouring.
/// </summary>
/// <remarks>
/// AvalonEdit's twenty-one grammars between them name a hundred and sixteen colours, and
/// most of those names appear in exactly one file. Collapsing them onto a dozen roles is
/// what makes a single coherent palette possible; anything not on this list keeps the hue
/// its grammar author chose and is only made legible.
/// </remarks>
internal enum SyntaxRole
{
    /// <summary>Not one of the roles Etch has an opinion about.</summary>
    Unmapped = 0,

    /// <summary>Comments of every kind, including doc comments.</summary>
    Comment,

    /// <summary>String and character literals.</summary>
    String,

    /// <summary>Language keywords.</summary>
    Keyword,

    /// <summary>Numeric literals, and the boolean and null constants beside them.</summary>
    Number,

    /// <summary>Brackets, operators and separators.</summary>
    Punctuation,

    /// <summary>Type names and the keywords that name types.</summary>
    Type,

    /// <summary>Method and function names, at declaration and at call.</summary>
    Function,

    /// <summary>Preprocessor and directive lines.</summary>
    Preprocessor,

    /// <summary>Markup element names.</summary>
    Tag,

    /// <summary>Markup attribute names.</summary>
    Attribute,

    /// <summary>Something the grammar marked as wrong.</summary>
    Invalid,
}

/// <summary>
/// Etch's syntax colours, and the promise that every one of them is readable.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit's built-in grammars carry their own colours and every one of them was chosen
/// for a white page: comments are <c>Green</c>, strings are <c>Blue</c>, several are plain
/// <c>Black</c>. Over Etch's dark editor those are somewhere between muddy and invisible.
/// They also cannot be corrected in place — <c>HighlightingLoader</c> freezes every
/// <see cref="ICSharpCode.AvalonEdit.Highlighting.HighlightingColor"/> it produces, and the
/// definitions are process-wide singletons, so writing to them would be both impossible
/// and, if it were possible, a global side effect of opening one tab.
/// </para>
/// <para>
/// So the grammars are kept for what they are genuinely good at — knowing that <c>catch</c>
/// is a keyword and that <c>#</c> starts a directive — and the colours are replaced at
/// render time. Two rules, in order:
/// </para>
/// <list type="number">
/// <item>A colour whose name maps to a <see cref="SyntaxRole"/> is replaced outright, so a
/// comment is the same colour in C#, Python and CSS. Consistency across languages is the
/// entire benefit of having a palette rather than twenty-one of them.</item>
/// <item>Anything else keeps the hue its grammar author chose and is moved only as far as
/// it must be to clear <see cref="MinimumContrast"/> against the page. A grammar that
/// distinguishes six kinds of keyword by shade goes on distinguishing them; it just does so
/// legibly.</item>
/// </list>
/// <para>
/// <b>The one place this promise is weaker than it sounds.</b> The ratio is against the
/// editor's page, which is what text sits on nearly all of the time. Inside a selection it
/// sits on §24's derived fill instead, which is by construction somewhere between the page
/// and the ink — so contrast there is lower than the number below. That is the cost of
/// §24's decision to leave <c>SelectionForeground</c> null so that highlighting survives
/// being selected at all, and it is a better trade than flattening every colour to one.
/// </para>
/// </remarks>
internal static class SyntaxPalette
{
    /// <summary>
    /// Contrast every syntax colour must reach against the page.
    /// </summary>
    /// <remarks>
    /// WCAG 2.1 AA for body text. Source code <em>is</em> body text — it is read
    /// continuously for hours, which is a stronger case for the full ratio than most
    /// interface text has, not a weaker one. Applied to Etch's own choices below as well as
    /// to inherited ones, so the constants are a starting point rather than a promise the
    /// palette has to be trusted to keep.
    /// </remarks>
    internal const double MinimumContrast = 4.5;

    /// <summary>
    /// The seed colour for each role, before the legibility floor is applied.
    /// </summary>
    /// <remarks>
    /// Hues rather than final values. Each is passed through
    /// <see cref="EditorColours.Legible"/> against the actual page, so what ships is
    /// whichever nearby colour clears the ratio — which means these can be chosen for how
    /// they look and read beside each other, and the contrast requirement is met by
    /// construction instead of by inspection.
    /// </remarks>
    private static readonly (SyntaxRole Role, Color Dark, Color Light)[] Seeds =
    [
        (SyntaxRole.Comment, Rgb(0x6A, 0x99, 0x55), Rgb(0x2E, 0x7D, 0x32)),
        (SyntaxRole.String, Rgb(0xCE, 0x91, 0x78), Rgb(0xA3, 0x15, 0x15)),
        (SyntaxRole.Keyword, Rgb(0x56, 0x9C, 0xD6), Rgb(0x00, 0x00, 0xC0)),
        (SyntaxRole.Number, Rgb(0xB5, 0xCE, 0xA8), Rgb(0x09, 0x86, 0x58)),
        (SyntaxRole.Punctuation, Rgb(0xC8, 0xC8, 0xC8), Rgb(0x3C, 0x3C, 0x3C)),
        (SyntaxRole.Type, Rgb(0x4E, 0xC9, 0xB0), Rgb(0x1E, 0x6F, 0x7F)),
        (SyntaxRole.Function, Rgb(0xDC, 0xDC, 0xAA), Rgb(0x79, 0x5E, 0x26)),
        (SyntaxRole.Preprocessor, Rgb(0xC5, 0x86, 0xC0), Rgb(0xAF, 0x00, 0xDB)),
        // Lighter than Keyword rather than equal to it. The obvious choice for a markup tag
        // is the same blue keywords get — that is what the editors these hues come from do,
        // because no language shows both at once. Etch does: HTML embeds JavaScript, and
        // ASP/XHTML embeds four things. Two roles resolving to one colour was caught by the
        // distinctness test rather than by looking at it, which is the point of having one.
        (SyntaxRole.Tag, Rgb(0x7A, 0xB8, 0xF5), Rgb(0x80, 0x00, 0x00)),
        (SyntaxRole.Attribute, Rgb(0x9C, 0xDC, 0xFE), Rgb(0x1F, 0x5C, 0x99)),
        (SyntaxRole.Invalid, Rgb(0xF4, 0x87, 0x71), Rgb(0xC0, 0x25, 0x25)),
    ];

    /// <summary>
    /// Colour names that map to a role outright.
    /// </summary>
    /// <remarks>
    /// Taken from the grammars rather than guessed at: these are the names that actually
    /// occur, read out of the <c>.xshd</c> resources. Names appearing in several grammars
    /// are here; the long tail that appears once is left to the fallback, which is the
    /// right place for it — <c>JavaScriptGlobalFunctions</c> is a distinction one grammar
    /// wanted to draw and Etch has no view on.
    /// </remarks>
    private static readonly Dictionary<string, SyntaxRole> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Comment"] = SyntaxRole.Comment,
        ["DocComment"] = SyntaxRole.Comment,
        ["CommentTags"] = SyntaxRole.Comment,
        ["JavaDocTags"] = SyntaxRole.Comment,
        ["KnownDocTags"] = SyntaxRole.Comment,
        ["BlockQuote"] = SyntaxRole.Comment,

        ["String"] = SyntaxRole.String,
        ["XmlString"] = SyntaxRole.String,
        ["Char"] = SyntaxRole.String,
        ["Character"] = SyntaxRole.String,
        ["Regex"] = SyntaxRole.String,
        ["StringInterpolation"] = SyntaxRole.String,
        ["Value"] = SyntaxRole.String,
        ["DateLiteral"] = SyntaxRole.String,

        ["Digits"] = SyntaxRole.Number,
        ["NumberLiteral"] = SyntaxRole.Number,
        ["Number"] = SyntaxRole.Number,
        ["Literals"] = SyntaxRole.Number,
        ["Constants"] = SyntaxRole.Number,
        ["BooleanConstants"] = SyntaxRole.Number,
        ["TrueFalse"] = SyntaxRole.Number,
        ["Bool"] = SyntaxRole.Number,
        ["Null"] = SyntaxRole.Number,
        ["NullOrValueKeywords"] = SyntaxRole.Number,

        ["Punctuation"] = SyntaxRole.Punctuation,
        ["XmlPunctuation"] = SyntaxRole.Punctuation,
        ["Operators"] = SyntaxRole.Punctuation,
        ["CurlyBraces"] = SyntaxRole.Punctuation,
        ["Colon"] = SyntaxRole.Punctuation,
        ["Slash"] = SyntaxRole.Punctuation,
        ["Assignment"] = SyntaxRole.Punctuation,

        ["ValueTypes"] = SyntaxRole.Type,
        ["ReferenceTypes"] = SyntaxRole.Type,
        ["ValueTypeKeywords"] = SyntaxRole.Type,
        ["ReferenceTypeKeywords"] = SyntaxRole.Type,
        ["TypeKeywords"] = SyntaxRole.Type,
        ["DataTypes"] = SyntaxRole.Type,
        ["OtherTypes"] = SyntaxRole.Type,
        ["Class"] = SyntaxRole.Type,
        ["Void"] = SyntaxRole.Type,

        ["MethodCall"] = SyntaxRole.Function,
        ["MethodName"] = SyntaxRole.Function,
        ["FunctionCall"] = SyntaxRole.Function,
        ["FunctionKeywords"] = SyntaxRole.Function,
        ["Command"] = SyntaxRole.Function,

        ["Preprocessor"] = SyntaxRole.Preprocessor,
        ["EntityReference"] = SyntaxRole.Preprocessor,
        ["Entities"] = SyntaxRole.Preprocessor,

        ["HtmlTag"] = SyntaxRole.Tag,
        ["Tags"] = SyntaxRole.Tag,
        ["ScriptTag"] = SyntaxRole.Tag,
        ["JavaScriptTag"] = SyntaxRole.Tag,
        ["JScriptTag"] = SyntaxRole.Tag,
        ["VBScriptTag"] = SyntaxRole.Tag,
        ["ASPSectionStartEndTags"] = SyntaxRole.Tag,

        ["Attributes"] = SyntaxRole.Attribute,
        ["Property"] = SyntaxRole.Attribute,
        ["Selector"] = SyntaxRole.Attribute,
        ["FieldName"] = SyntaxRole.Attribute,
        ["Variable"] = SyntaxRole.Attribute,

        ["UnknownScriptTag"] = SyntaxRole.Invalid,
        ["UnknownAttribute"] = SyntaxRole.Invalid,
        ["RemovedText"] = SyntaxRole.Invalid,
    };

    /// <summary>
    /// Names ending in one of these are keywords, whatever else they say.
    /// </summary>
    /// <remarks>
    /// Twenty-odd of the hundred and sixteen names are some flavour of keyword —
    /// <c>GotoKeywords</c>, <c>ExceptionKeywords</c>, <c>AccessKeywords</c>,
    /// <c>ControlStatements</c>, <c>IterationStatements</c> — and listing each one would be
    /// a table that goes stale the moment a grammar is added. The suffixes are a rule about
    /// how these files are named, which is more durable than an inventory of them.
    /// </remarks>
    private static readonly string[] KeywordSuffixes =
        ["Keywords", "Keyword", "Statements", "Modifiers", "Visibility", "ControlFlow"];

    /// <summary>The role a grammar's colour name maps to.</summary>
    /// <param name="name">
    /// The colour's name, which is null for the many rules that specify a colour inline
    /// rather than referring to a named one.
    /// </param>
    internal static SyntaxRole RoleOf(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return SyntaxRole.Unmapped;
        }

        if (ByName.TryGetValue(name, out var role))
        {
            return role;
        }

        foreach (var suffix in KeywordSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return SyntaxRole.Keyword;
            }
        }

        return SyntaxRole.Unmapped;
    }

    /// <summary>
    /// The colour to draw <paramref name="role"/> in, or null to keep what the grammar said.
    /// </summary>
    /// <param name="role">The role, from <see cref="RoleOf"/>.</param>
    /// <param name="dark">Whether the dark theme is in use.</param>
    internal static Color? ForRole(SyntaxRole role, bool dark)
    {
        if (role == SyntaxRole.Unmapped)
        {
            return null;
        }

        var page = Page(dark);

        foreach (var seed in Seeds)
        {
            if (seed.Role == role)
            {
                return EditorColours.Legible(dark ? seed.Dark : seed.Light, page, MinimumContrast);
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps a grammar's own colour but drags it up to the readability floor.
    /// </summary>
    /// <remarks>
    /// The fallback for the long tail of names. <c>Legible</c> returns the colour untouched
    /// when it already clears the ratio, so a grammar that happens to have picked something
    /// readable is left exactly as its author wrote it — which is the outcome to want.
    /// </remarks>
    /// <param name="original">The colour the grammar asked for.</param>
    /// <param name="dark">Whether the dark theme is in use.</param>
    internal static Color Rescue(Color original, bool dark) =>
        EditorColours.Legible(original, Page(dark), MinimumContrast);

    /// <summary>
    /// The opaque page every ratio here is measured against.
    /// </summary>
    /// <remarks>
    /// The same surfaces §24 uses, and for the same reason: the editor is transparent over
    /// Mica, so there is no literal page colour to read — these are the application
    /// backgrounds WPF-UI tints Mica towards, and they are the closest honest stand-in for
    /// a contrast calculation. Alpha is dropped because a page is by definition what
    /// everything else composites onto.
    /// </remarks>
    private static Color Page(bool dark)
    {
        var (surface, _) = EditorColours.Surfaces(dark);

        return Color.FromRgb(surface.R, surface.G, surface.B);
    }

    private static Color Rgb(byte red, byte green, byte blue) => Color.FromRgb(red, green, blue);
}
