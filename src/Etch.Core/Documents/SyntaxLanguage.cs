namespace Etch.Core.Documents;

/// <summary>
/// A language Etch can highlight.
/// </summary>
/// <remarks>
/// <para>
/// A closed enum rather than a string, because every consumer switches on it: the
/// highlighting grammar, the folding strategy and the status bar all need to agree, and
/// an open string would let them disagree silently. Adding a language means adding a
/// member here and answering all three questions at once, which is the point.
/// </para>
/// <para>
/// The set is drawn from the grammars AvalonEdit already ships, since <c>Etch.Core</c>
/// takes no package reference and therefore cannot see them. That is a slightly odd
/// coupling to state out loud — an enum in the pure layer whose membership is decided by
/// a library it cannot reference — but the alternative is worse: a language named here
/// with no grammar behind it produces a document that reports itself as C# and renders as
/// plain text, and nothing in the type system would catch it.
/// </para>
/// </remarks>
public enum SyntaxLanguage
{
    /// <summary>No highlighting. The honest default, not a failure.</summary>
    None = 0,

    /// <summary>JSON, including newline-delimited JSON.</summary>
    Json = 1,

    /// <summary>XML and the many things that are XML underneath, XAML included.</summary>
    Xml = 2,

    /// <summary>HTML.</summary>
    Html = 3,

    /// <summary>CSS.</summary>
    Css = 4,

    /// <summary>JavaScript, and TypeScript for want of a grammar of its own.</summary>
    JavaScript = 5,

    /// <summary>C#.</summary>
    CSharp = 6,

    /// <summary>C, C++ and their headers.</summary>
    Cpp = 7,

    /// <summary>Java.</summary>
    Java = 8,

    /// <summary>Python.</summary>
    Python = 9,

    /// <summary>PowerShell.</summary>
    PowerShell = 10,

    /// <summary>Markdown.</summary>
    Markdown = 11,

    /// <summary>SQL, using the T-SQL grammar.</summary>
    Sql = 12,

    /// <summary>PHP.</summary>
    Php = 13,

    /// <summary>Visual Basic.</summary>
    VisualBasic = 14,

    /// <summary>A unified diff.</summary>
    Patch = 15,
}
