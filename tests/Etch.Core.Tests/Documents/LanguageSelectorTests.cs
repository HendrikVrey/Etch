using Etch.Core.Abstractions;
using Etch.Core.Documents;
using Xunit;

namespace Etch.Core.Tests.Documents;

/// <summary>
/// Which language a buffer is highlighted as.
/// </summary>
/// <remarks>
/// The interesting cases are all about precedence and about paths that are not really
/// paths, because those are the ones where a plausible implementation quietly gets it
/// wrong and the only symptom is the wrong colours.
/// </remarks>
public class LanguageSelectorTests
{
    [Theory]
    [InlineData("notes.json", SyntaxLanguage.Json)]
    [InlineData("Program.cs", SyntaxLanguage.CSharp)]
    [InlineData("build.csproj", SyntaxLanguage.Xml)]
    [InlineData("MainWindow.xaml", SyntaxLanguage.Xml)]
    [InlineData("index.HTML", SyntaxLanguage.Html)]
    [InlineData("readme.md", SyntaxLanguage.Markdown)]
    [InlineData("query.sql", SyntaxLanguage.Sql)]
    [InlineData("app.ts", SyntaxLanguage.JavaScript)]
    public void An_extension_names_the_language(string path, SyntaxLanguage expected)
    {
        Assert.Equal(expected, LanguageSelector.FromPath(path));
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".env")]
    public void A_dotfile_has_no_extension(string path)
    {
        // ".gitignore" is a file called .gitignore, not a gitignore file. Reading the text
        // after the last dot as an extension would make every dotfile claim one.
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.FromPath(path));
    }

    [Theory]
    [InlineData(@"C:\v1.0\README")]
    [InlineData("/home/hendrik/site.com/LICENCE")]
    public void A_dot_in_a_directory_is_not_an_extension(string path)
    {
        // The last dot in the whole string is in a directory name here. Scanning back from
        // the end without trimming the directory first would read "0\README" as one.
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.FromPath(path));
    }

    [Theory]
    [InlineData(@"C:\logs\app.config", SyntaxLanguage.Xml)]
    [InlineData("/etc/thing/data.json", SyntaxLanguage.Json)]
    public void A_full_path_is_read_from_its_file_name(string path, SyntaxLanguage expected)
    {
        Assert.Equal(expected, LanguageSelector.FromPath(path));
    }

    [Fact]
    public void Only_the_last_extension_counts()
    {
        // archive.tar.gz has extension .gz, which Etch does not highlight. Matching on any
        // dot-separated segment would make this a .tar file, and .json.bak a JSON file.
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.FromPath("archive.tar.gz"));
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.FromPath("settings.json.bak"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_tab_with_no_file_has_no_language_from_its_path(string? path)
    {
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.FromPath(path));
    }

    [Theory]
    [InlineData(FormatId.Json, SyntaxLanguage.Json)]
    [InlineData(FormatId.Ndjson, SyntaxLanguage.Json)]
    public void Detection_answers_for_a_scratch_tab(FormatId format, SyntaxLanguage expected)
    {
        Assert.Equal(expected, LanguageSelector.Select(filePath: null, () => format));
    }

    [Theory]
    [InlineData(FormatId.Base64)]
    [InlineData(FormatId.Hex)]
    [InlineData(FormatId.Guid)]
    [InlineData(FormatId.Jwt)]
    [InlineData(FormatId.UnixEpoch)]
    [InlineData(FormatId.UrlEncoded)]
    [InlineData(FormatId.PlainText)]
    public void A_format_that_is_not_a_language_gets_no_grammar(FormatId format)
    {
        // These are things to transform, not things to colour. Mapping them onto some
        // grammar would produce highlighting that is confidently wrong, which is worse
        // than none: the status bar already says what they are.
        Assert.Equal(SyntaxLanguage.None, LanguageSelector.Select(filePath: null, () => format));
    }

    [Fact]
    public void The_extension_outranks_the_detection()
    {
        // A .json file holding something that does not parse yet is still being written as
        // JSON. Letting the inference win would strip its colours mid-keystroke and put
        // them back when the braces balanced, which is worse than being briefly wrong.
        Assert.Equal(SyntaxLanguage.Json, LanguageSelector.Select("half-typed.json", () => FormatId.PlainText));
        Assert.Equal(SyntaxLanguage.CSharp, LanguageSelector.Select("Program.cs", () => FormatId.Json));
    }

    [Fact]
    public void Detection_is_consulted_only_when_the_path_says_nothing()
    {
        // A file with an extension Etch does not know still falls back, because the
        // alternative is that naming a file .txt turns its highlighting off.
        Assert.Equal(SyntaxLanguage.Json, LanguageSelector.Select("payload.txt", () => FormatId.Json));
    }

    [Fact]
    public void Detection_is_not_run_at_all_when_the_extension_answers()
    {
        // The reason the parameter is a callback and not a value. Detection reads the
        // buffer, and a 10 MB log opened by a name that settles the question should not pay
        // for a scan whose result is thrown away.
        var ran = false;

        var language = LanguageSelector.Select(
            "Program.cs",
            () =>
            {
                ran = true;
                return FormatId.Json;
            });

        Assert.Equal(SyntaxLanguage.CSharp, language);
        Assert.False(ran);
    }

    [Fact]
    public void A_missing_detector_is_rejected_rather_than_assumed()
    {
        Assert.Throws<ArgumentNullException>(() => LanguageSelector.Select("x.unknown", null!));
    }
}
