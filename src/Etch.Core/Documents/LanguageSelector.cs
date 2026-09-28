using Etch.Core.Abstractions;

namespace Etch.Core.Documents;

/// <summary>
/// Decides which language a buffer should be highlighted as.
/// </summary>
/// <remarks>
/// <para>
/// Two sources of evidence, and the order between them is the whole design. A file
/// extension is a <em>declaration</em>: the user named the file, and a <c>.json</c> file
/// holding a fragment that does not parse is still a JSON file being edited. Format
/// detection is an <em>inference</em> from the first 64 KB, and it is right most of the
/// time. So the extension wins wherever there is one, and detection answers for scratch
/// tabs, which is the case that matters, because pasting into a scratch tab is what Etch
/// is for.
/// </para>
/// <para>
/// Detection is only consulted for formats that <em>are</em> languages. Base64, hex, a
/// GUID and a Unix timestamp are things Etch can transform, not things it can colour, and
/// mapping them onto a grammar would produce highlighting that is confidently wrong. They
/// return <see cref="SyntaxLanguage.None"/> and the status bar goes on describing them.
/// </para>
/// </remarks>
public static class LanguageSelector
{
    /// <summary>
    /// Extension to language, lower-cased and including the leading dot.
    /// </summary>
    /// <remarks>
    /// Etch's own table rather than AvalonEdit's <c>GetDefinitionByExtension</c>, and not
    /// merely to keep this project pure. AvalonEdit registers <c>.md</c> twice (once for
    /// <c>MarkDown</c> and again for <c>MarkDownWithFontSize</c>, which scales heading text)
    /// and the second registration overwrites the first, so asking it by extension gets
    /// the variant that changes font sizes inside a fixed-width editor. Choosing the
    /// grammar by name is the only way to get the one that is wanted.
    /// </remarks>
    private static readonly Dictionary<string, SyntaxLanguage> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = SyntaxLanguage.Json,
        [".jsonc"] = SyntaxLanguage.Json,
        [".ndjson"] = SyntaxLanguage.Json,
        [".jsonl"] = SyntaxLanguage.Json,
        [".webmanifest"] = SyntaxLanguage.Json,

        [".xml"] = SyntaxLanguage.Xml,
        [".xsl"] = SyntaxLanguage.Xml,
        [".xslt"] = SyntaxLanguage.Xml,
        [".xsd"] = SyntaxLanguage.Xml,
        [".config"] = SyntaxLanguage.Xml,
        [".csproj"] = SyntaxLanguage.Xml,
        [".vbproj"] = SyntaxLanguage.Xml,
        [".props"] = SyntaxLanguage.Xml,
        [".targets"] = SyntaxLanguage.Xml,
        [".xaml"] = SyntaxLanguage.Xml,
        [".svg"] = SyntaxLanguage.Xml,
        [".nuspec"] = SyntaxLanguage.Xml,
        [".resx"] = SyntaxLanguage.Xml,
        [".manifest"] = SyntaxLanguage.Xml,
        [".xshd"] = SyntaxLanguage.Xml,
        [".plist"] = SyntaxLanguage.Xml,

        [".htm"] = SyntaxLanguage.Html,
        [".html"] = SyntaxLanguage.Html,
        [".cshtml"] = SyntaxLanguage.Html,

        [".css"] = SyntaxLanguage.Css,
        [".scss"] = SyntaxLanguage.Css,
        [".less"] = SyntaxLanguage.Css,

        [".js"] = SyntaxLanguage.JavaScript,
        [".mjs"] = SyntaxLanguage.JavaScript,
        [".cjs"] = SyntaxLanguage.JavaScript,
        [".jsx"] = SyntaxLanguage.JavaScript,

        // No TypeScript grammar ships with AvalonEdit. JavaScript's is a strict subset of
        // the syntax, so keywords, strings and comments all colour correctly and only the
        // type annotations go unhighlighted: visibly better than plain text, and honest
        // about it here rather than surprising later.
        [".ts"] = SyntaxLanguage.JavaScript,
        [".tsx"] = SyntaxLanguage.JavaScript,

        [".cs"] = SyntaxLanguage.CSharp,
        [".csx"] = SyntaxLanguage.CSharp,

        [".c"] = SyntaxLanguage.Cpp,
        [".h"] = SyntaxLanguage.Cpp,
        [".cc"] = SyntaxLanguage.Cpp,
        [".cpp"] = SyntaxLanguage.Cpp,
        [".hpp"] = SyntaxLanguage.Cpp,
        [".cxx"] = SyntaxLanguage.Cpp,

        [".java"] = SyntaxLanguage.Java,

        [".py"] = SyntaxLanguage.Python,
        [".pyw"] = SyntaxLanguage.Python,

        [".ps1"] = SyntaxLanguage.PowerShell,
        [".psm1"] = SyntaxLanguage.PowerShell,
        [".psd1"] = SyntaxLanguage.PowerShell,

        [".md"] = SyntaxLanguage.Markdown,
        [".markdown"] = SyntaxLanguage.Markdown,

        [".sql"] = SyntaxLanguage.Sql,

        [".php"] = SyntaxLanguage.Php,

        [".vb"] = SyntaxLanguage.VisualBasic,

        [".patch"] = SyntaxLanguage.Patch,
        [".diff"] = SyntaxLanguage.Patch,
    };

    /// <summary>
    /// Picks a language from what the file is called and what the buffer looks like.
    /// </summary>
    /// <param name="filePath">The file's path, or null for a scratch tab.</param>
    /// <param name="detect">
    /// Runs format detection. Called only when the path has not already answered, which is
    /// the whole reason it is a callback: detection reads the buffer, and a file opened by
    /// a name that settles the question should not pay for a scan whose result is discarded.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="detect"/> is null.</exception>
    /// <remarks>
    /// The single statement of the precedence rule. It is worth one indirection to keep it
    /// that way: written out at the call site as well, the two drifted apart within a day,
    /// the UI asked detection only for a tab with no file at all, while this asked whenever
    /// the extension said nothing, so a file called <c>payload.txt</c> full of JSON was
    /// highlighted by the tested rule and not by the shipped one.
    /// </remarks>
    public static SyntaxLanguage Select(string? filePath, Func<FormatId> detect)
    {
        ArgumentNullException.ThrowIfNull(detect);

        var named = FromPath(filePath);

        // A recognised extension is a statement of intent and outranks the inference, even
        // when the two disagree: a half-typed .json file that does not parse yet is still
        // being written as JSON, and losing its colours mid-keystroke would be worse than
        // useless.
        return named != SyntaxLanguage.None ? named : FromFormat(detect());
    }

    /// <summary>The language a path's extension names, or <see cref="SyntaxLanguage.None"/>.</summary>
    /// <remarks>
    /// <para>
    /// <c>Path.GetExtension</c> is deliberately not used. It throws on nothing in modern
    /// .NET, but it is documented in terms of the last dot and this needs to be right about
    /// paths that are hostile rather than merely unusual: a file genuinely called
    /// <c>.gitignore</c> has no extension in the sense meant here, and one called
    /// <c>archive.tar.gz</c> has <c>.gz</c>. Both fall out of scanning back to the last dot
    /// and refusing an empty stem.
    /// </para>
    /// <para>
    /// No file-system access of any kind, which is what keeps this in <c>Etch.Core</c>.
    /// </para>
    /// </remarks>
    public static SyntaxLanguage FromPath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return SyntaxLanguage.None;
        }

        var name = filePath.AsSpan();

        // Trim the directory here rather than via Path.GetFileName, so that a separator
        // appearing after the last dot, "C:\v1.0\README", cannot be read as an extension.
        var separator = name.LastIndexOfAny('\\', '/');

        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        var dot = name.LastIndexOf('.');

        // No dot at all, or a leading dot with nothing before it: a dotfile, not an
        // extension. ".gitignore" is a file called .gitignore, not a gitignore file.
        if (dot <= 0)
        {
            return SyntaxLanguage.None;
        }

        return ByExtension.GetValueOrDefault(name[dot..].ToString(), SyntaxLanguage.None);
    }

    /// <summary>The language a detected format implies, for buffers with no file name.</summary>
    public static SyntaxLanguage FromFormat(FormatId detected) => detected switch
    {
        FormatId.Json or FormatId.Ndjson => SyntaxLanguage.Json,

        // Everything else Etch detects is data, not a language. Base64, hex, a GUID, a
        // JWT, a percent-encoded string and a timestamp have no grammar to colour: they
        // have transforms, which is a different offer and one the palette already makes.
        _ => SyntaxLanguage.None,
    };
}
