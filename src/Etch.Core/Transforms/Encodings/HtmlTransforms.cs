using System.Buffers;
using System.Net;
using System.Text;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Encodings;

/// <summary>
/// Escapes the five characters that mean something to an HTML or XML parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately narrower than <see cref="WebUtility.HtmlEncode(string)"/>.</b> The
/// framework's encoder also rewrites every character above U+009F as a numeric entity,
/// which is correct for emitting into a document of unknown encoding and wrong for a
/// scratchpad: it turns a paragraph of German or a code sample full of arrows into a wall
/// of <c>&amp;#233;</c>, and the person who pressed this wanted their angle brackets
/// escaped, not their language. Every document Etch produces is UTF-16 in memory and UTF-8
/// on disk, where those characters need no escaping at all.
/// </para>
/// <para>
/// The five are the standard set: <c>&amp;</c> first, so that the ampersands introduced by
/// the other four are not escaped a second time.
/// </para>
/// </remarks>
internal sealed class EncodeHtmlEntities : ITransform
{
    /// <summary>The five characters worth escaping.</summary>
    private static readonly SearchValues<char> Markup = SearchValues.Create("&<>\"'");

    /// <inheritdoc />
    public string Id => "html.encodeEntities";

    /// <inheritdoc />
    public string Name => "HTML entity encode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["escape html", "entities", "html escape", "xml escape"];

    /// <inheritdoc />
    /// <remarks>Never suggested: escaping is sought, not implied by the buffer.</remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var text = input.Text;

        // A scan before an allocation, and a report rather than a rewrite. Returning Ok with
        // the input unchanged would cost an undo step and the caret position to produce a
        // byte-identical document; saying nothing needed escaping is both cheaper and more
        // informative than an "applied" that changed nothing.
        if (text.AsSpan().IndexOfAny(Markup) < 0)
        {
            return TransformResult.Reported("There is nothing here that needs escaping.");
        }

        var builder = new StringBuilder(text.Length + (text.Length / 8) + 8);

        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;

                case '<':
                    builder.Append("&lt;");
                    break;

                case '>':
                    builder.Append("&gt;");
                    break;

                case '"':
                    builder.Append("&quot;");
                    break;

                // &#39; rather than &apos;. The named form is XML, and HTML 4 does not
                // define it — Internet Explorer's refusal to render it is why every
                // encoder in the world emits the numeric form instead.
                case '\'':
                    builder.Append("&#39;");
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        return TransformResult.Ok(builder.ToString());
    }
}

/// <summary>
/// Turns HTML entities back into the characters they stand for.
/// </summary>
/// <remarks>
/// <para>
/// Decoding delegates to <see cref="WebUtility.HtmlDecode(string)"/> where encoding does
/// not, and the asymmetry is the point. Encoding has to make a judgement about how much to
/// escape; decoding has only one right answer, and that answer includes the whole named
/// entity table — <c>&amp;nbsp;</c>, <c>&amp;mdash;</c>, <c>&amp;hellip;</c> and some two
/// thousand others that arrive in text scraped off a page. Reimplementing that table would
/// be several hundred lines that could only be worse than the framework's.
/// </para>
/// <para>
/// The pairing still round-trips: decoding what this file encoded returns the original,
/// because the five entities it emits are all in the table.
/// </para>
/// </remarks>
internal sealed class DecodeHtmlEntities : ITransform
{
    /// <inheritdoc />
    public string Id => "html.decodeEntities";

    /// <inheritdoc />
    public string Name => "HTML entity decode";

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Encoding;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = ["unescape html", "entities", "html unescape", "nbsp"];

    /// <inheritdoc />
    /// <remarks>
    /// Not suggested, because nothing detects "text containing entities". A detector for it
    /// would have to fire on any buffer holding an ampersand and a semicolon, which is most
    /// prose and every query string.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var decoded = WebUtility.HtmlDecode(input.Text);

        // Reported rather than silently returning the input. Pressing "decode" on text with
        // no entities in it means the user expected some, and an editor that flashes
        // "applied" while changing nothing has answered a different question than the one
        // that was asked.
        return string.Equals(decoded, input.Text, StringComparison.Ordinal)
            ? TransformResult.Reported("There are no HTML entities here.")
            : TransformResult.Ok(decoded);
    }
}
