using System.Buffers;
using System.Globalization;
using System.Text;
using Etch.Core.Abstractions;
using Etch.Core.Text;

namespace Etch.Core.Transforms.Text;

/// <summary>
/// The shared body of every case conversion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Line by line, not buffer at a time.</b> A case conversion applied to a whole document
/// as one string would run the words of the last line into the first word of the next, and
/// a column of identifiers, which is what people paste when they convert a list of
/// database columns to properties, is the case this has to get right. One line in, one
/// line out.
/// </para>
/// <para>
/// <b>A line with nothing alphanumeric in it comes back untouched.</b> Blank lines,
/// separator rules and lines of punctuation survive a conversion unchanged, so the shape of
/// the document does not quietly collapse.
/// </para>
/// <para>
/// <b>Invariant casing, always.</b> Culture-aware upper-casing turns a Turkish <c>i</c>
/// into <c>İ</c>, which is correct for Turkish prose and catastrophic for an identifier:
/// the resulting name does not compile and the reason is invisible. Identifiers are not
/// words in a language.
/// </para>
/// </remarks>
internal abstract class CaseTransform : ITransform
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Text;

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Aliases { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Never suggested. No detector can tell that a buffer is an identifier rather than a
    /// sentence, and <c>Ctrl+Enter</c> re-casing a paragraph of prose would be a keystroke
    /// people learn not to press.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var lines = TextLines.Split(input.Text, out var trailingNewLine);

        for (var i = 0; i < lines.Length; i++)
        {
            // Checked per line rather than per document: the cost is one comparison, and a
            // 200 000-line paste is exactly where abandoning the work has to actually stop.
            cancellationToken.ThrowIfCancellationRequested();

            lines[i] = ConvertLine(lines[i]);
        }

        return TransformResult.Ok(TextLines.Join(lines, trailingNewLine, input.Options.NewLine));
    }

    /// <summary>
    /// Rewrites one line.
    /// </summary>
    /// <remarks>
    /// Split into words and recombined, which is right for the five identifier conventions
    /// and wrong for Title Case, so Title Case overrides this rather than implementing
    /// <see cref="Combine"/>. A line with nothing alphanumeric in it comes back untouched.
    /// </remarks>
    protected virtual string ConvertLine(string line)
    {
        var words = WordSplitter.Split(line);

        return words.Count > 0 ? Combine(words) : line;
    }

    /// <summary>Writes the words of one line back in this transform's convention.</summary>
    protected abstract string Combine(List<string> words);

    /// <summary>Lower-cases a word and upper-cases its first letter.</summary>
    /// <remarks>
    /// The first <em>rune</em>, not the first char. Since <see cref="WordSplitter"/> started
    /// keeping astral characters, a word can begin with a surrogate pair, and
    /// <c>word[1..]</c> after <c>char.ToUpperInvariant(word[0])</c> would cut one in half and
    /// produce two lone surrogates where a letter used to be.
    /// </remarks>
    protected static string Capitalise(string word)
    {
        if (word.Length == 0)
        {
            return word;
        }

        var length = Rune.DecodeFromUtf16(word, out var first, out var consumed) == OperationStatus.Done
            ? consumed
            : 1;

        return string.Concat(
            Rune.ToUpperInvariant(first).ToString(),
            word[length..].ToLowerInvariant());
    }

    /// <summary>Joins words with a separator, having applied <paramref name="fold"/> to each.</summary>
    protected static string Join(List<string> words, char separator, Func<string, string> fold)
    {
        var builder = new StringBuilder(Capacity(words) + words.Count);

        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(fold(words[i]));
        }

        return builder.ToString();
    }

    private static int Capacity(List<string> words)
    {
        var total = 0;

        foreach (var word in words)
        {
            total += word.Length;
        }

        return total;
    }
}

/// <summary>camelCase.</summary>
internal sealed class ToCamelCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.camel";

    /// <inheritdoc />
    public override string Name => "camelCase";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["camel", "camel case", "lower camel"];

    /// <inheritdoc />
    protected override string Combine(List<string> words)
    {
        var builder = new StringBuilder(words[0].ToLowerInvariant());

        for (var i = 1; i < words.Count; i++)
        {
            builder.Append(Capitalise(words[i]));
        }

        return builder.ToString();
    }
}

/// <summary>PascalCase.</summary>
internal sealed class ToPascalCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.pascal";

    /// <inheritdoc />
    public override string Name => "PascalCase";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["pascal", "pascal case", "upper camel", "class name"];

    /// <inheritdoc />
    protected override string Combine(List<string> words)
    {
        var builder = new StringBuilder();

        foreach (var word in words)
        {
            builder.Append(Capitalise(word));
        }

        return builder.ToString();
    }
}

/// <summary>snake_case.</summary>
internal sealed class ToSnakeCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.snake";

    /// <inheritdoc />
    public override string Name => "snake_case";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["snake", "snake case", "underscore", "column name"];

    /// <inheritdoc />
    protected override string Combine(List<string> words) =>
        Join(words, '_', static word => word.ToLowerInvariant());
}

/// <summary>kebab-case.</summary>
internal sealed class ToKebabCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.kebab";

    /// <inheritdoc />
    public override string Name => "kebab-case";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["kebab", "kebab case", "dashed", "slug", "css"];

    /// <inheritdoc />
    protected override string Combine(List<string> words) =>
        Join(words, '-', static word => word.ToLowerInvariant());
}

/// <summary>CONSTANT_CASE.</summary>
internal sealed class ToConstantCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.constant";

    /// <inheritdoc />
    public override string Name => "CONSTANT_CASE";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } =
        ["constant", "const case", "screaming snake", "upper case", "env var"];

    /// <inheritdoc />
    protected override string Combine(List<string> words) =>
        Join(words, '_', static word => word.ToUpperInvariant());
}

/// <summary>
/// Title Case.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one case transform that does not go through <see cref="WordSplitter"/>.</b> The
/// others are converting identifiers, where dropping <c>_</c> and <c>-</c> is the entire
/// point. This one is aimed at prose, its aliases are "capitalise" and "heading", and
/// running prose through a splitter that discards every separator turns
/// <c>Don't stop. It's fine!</c> into <c>Don T Stop It S Fine</c>: the apostrophes and the
/// full stops are simply gone. Title Case must not move or delete a single character; it
/// only changes which ones are capitals.
/// </para>
/// <para>
/// Every word is capitalised, including the short ones. Real title case leaves articles and
/// prepositions in lower case, "The Wind in the Willows", but which words those are
/// depends on the style guide and on the language, and a transform that guessed would be
/// wrong in a way that is tedious to undo.
/// </para>
/// <para>
/// The cost of working on characters rather than words is that a camel hump is not a word
/// boundary here: <c>userAccountId</c> becomes <c>Useraccountid</c> rather than
/// <c>User Account Id</c>. That is the right trade. Reading humps would mean going back
/// through the splitter and losing the punctuation again, this is the transform aimed at
/// prose, and the five conventions above exist precisely for identifiers. It also means
/// <c>SHOUTED TEXT</c> comes back as <c>Shouted Text</c>, which is what people reach for
/// this to do.
/// </para>
/// </remarks>
internal sealed class ToTitleCase : CaseTransform
{
    /// <inheritdoc />
    public override string Id => "case.title";

    /// <inheritdoc />
    public override string Name => "Title Case";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["title", "capitalise", "capitalize", "heading"];

    /// <inheritdoc />
    /// <remarks>
    /// Not reached. <see cref="ConvertLine"/> is overridden and never splits into words; this
    /// stays as the sensible answer if a future caller ever does reach it.
    /// </remarks>
    protected override string Combine(List<string> words) => Join(words, ' ', Capitalise);

    /// <inheritdoc />
    protected override string ConvertLine(string line)
    {
        var builder = new StringBuilder(line.Length);
        var atWordStart = true;

        var index = 0;

        while (index < line.Length)
        {
            var status = Rune.DecodeFromUtf16(line.AsSpan(index), out var rune, out var consumed);

            if (status != OperationStatus.Done)
            {
                // A lone surrogate. Copied through untouched: it is not a letter, and it is
                // not this transform's business to repair the buffer.
                builder.Append(line[index]);
                index++;
                atWordStart = true;
                continue;
            }

            if (Rune.IsLetter(rune))
            {
                builder.Append((atWordStart ? Rune.ToUpperInvariant(rune) : Rune.ToLowerInvariant(rune)).ToString());
                atWordStart = false;
            }
            else
            {
                builder.Append(line.AsSpan(index, consumed));

                // Digits, apostrophes and combining marks continue the word; everything else
                // starts a new one. The apostrophe is the one that matters: without it
                // "Don't stop" comes back as "Don'T Stop", which is the first thing anybody
                // would type to test this. A hyphen deliberately does start a word, because
                // "well-known" is "Well-Known" in every style guide there is.
                atWordStart = !Rune.IsDigit(rune) && !IsWordJoiner(rune);
            }

            index += consumed;
        }

        return builder.ToString();
    }

    /// <summary>Characters that sit inside a word rather than between two.</summary>
    /// <remarks>
    /// Both apostrophes, because the typographic one is what a word processor and every
    /// phone keyboard produce, and a combining mark belongs to the letter before it.
    /// </remarks>
    private static bool IsWordJoiner(Rune rune) =>
        rune.Value is '\'' or '’'
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
