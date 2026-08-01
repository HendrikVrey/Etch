using System.Buffers;
using System.Globalization;
using System.Text;

namespace Etch.Core.Text;

/// <summary>
/// Breaks an identifier or a phrase into the words the case transforms recombine.
/// </summary>
/// <remarks>
/// <para>
/// Every case conversion is the same two steps — work out where the words are, then write
/// them back joined differently — and the first step is the only hard one. Doing it once
/// means <c>camelCase</c> and <c>snake_case</c> cannot disagree about where
/// <c>parseHTTPResponse2</c> divides, which they certainly would if each rewrote the other
/// directly.
/// </para>
/// <para>Three rules, in order of how often they matter:</para>
/// <list type="bullet">
/// <item><b>Any character that is not a letter, a digit or a combining mark is a separator
/// and is dropped.</b> That covers <c>_</c>, <c>-</c>, <c>.</c>, spaces and everything else
/// in one rule, which is what lets a single splitter serve identifiers and prose alike.</item>
/// <item><b>An upper-case letter after a lower-case one or a digit starts a word.</b> The
/// camel-hump rule.</item>
/// <item><b>The last upper-case letter of a run belongs to the word that follows it.</b>
/// Without this, <c>HTTPResponse</c> is one word and turns into <c>httpresponse</c>; with
/// it, the acronym stays whole and the result is <c>http_response</c>.</item>
/// </list>
/// <para>
/// Digits attach to the word in progress rather than starting one, so <c>utf8Encoder</c> is
/// <c>utf8</c> and <c>Encoder</c>. Splitting there would turn every version-numbered
/// identifier into three words and is not what anyone means.
/// </para>
/// <para>
/// <b>This walks runes, not chars, and that is not pedantry.</b> <c>char.IsLetterOrDigit</c>
/// answers for a single UTF-16 code unit, so it says <em>false</em> for both halves of every
/// surrogate pair — every emoji, every CJK extension ideograph — and false for every
/// combining mark, which is all of NFD text and therefore most text that has been near
/// macOS. A splitter built on it silently <em>deletes</em> those characters, because
/// anything it classes as a separator is dropped rather than kept. Losing an accent or an
/// emoji out of a buffer as a side effect of "snake_case" is exactly the kind of quiet
/// damage this tool must not do.
/// </para>
/// </remarks>
internal static class WordSplitter
{
    /// <summary>Splits <paramref name="text"/> into words, dropping separators.</summary>
    /// <returns>
    /// The words, in order. Empty when there was nothing alphanumeric — which callers
    /// treat as "leave this line exactly as it was" rather than as an empty result.
    /// </returns>
    public static List<string> Split(ReadOnlySpan<char> text)
    {
        var words = new List<string>();
        var start = -1;
        var index = 0;

        while (index < text.Length)
        {
            var length = RuneLength(text, index, out var rune, out var valid);

            if (!valid || !IsWordCharacter(rune))
            {
                Close(text, ref start, index, words);
                index += length;
                continue;
            }

            if (start < 0)
            {
                start = index;
                index += length;
                continue;
            }

            if (StartsNewWord(text, index, rune))
            {
                words.Add(text[start..index].ToString());
                start = index;
            }

            index += length;
        }

        Close(text, ref start, text.Length, words);

        return words;
    }

    /// <summary>Decodes one rune, reporting how many code units it occupied.</summary>
    /// <remarks>
    /// An invalid sequence — a lone surrogate — is reported as one unit and not a word
    /// character, so it acts as a separator. That is the only sensible reading: it is not a
    /// letter, and it cannot be part of one.
    /// </remarks>
    private static int RuneLength(ReadOnlySpan<char> text, int index, out Rune rune, out bool valid)
    {
        valid = Rune.DecodeFromUtf16(text[index..], out rune, out var consumed) == OperationStatus.Done;

        return Math.Max(consumed, 1);
    }

    private static bool IsWordCharacter(Rune rune) =>
        Rune.IsLetterOrDigit(rune) || IsMark(rune);

    private static bool IsMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark
        or UnicodeCategory.EnclosingMark;

    /// <summary>True when the rune at <paramref name="index"/> begins a new word.</summary>
    private static bool StartsNewWord(ReadOnlySpan<char> text, int index, Rune rune)
    {
        if (!Rune.IsUpper(rune))
        {
            return false;
        }

        // The previous rune, which for the camel-hump rule is all that matters. Read back a
        // code unit at a time rather than kept in a variable, so the two callers cannot get
        // out of step over a surrogate pair.
        var previous = PreviousRune(text, index);

        if (!Rune.IsUpper(previous))
        {
            // The camel hump: lower-then-upper, or digit-then-upper as in "utf8Encoder".
            // A mark before an upper-case letter is part of the previous word, not a break.
            return !IsMark(previous);
        }

        // Inside a run of capitals. It only breaks at the last one, and only when a
        // lower-case letter follows — "HTTPResponse" divides before the R, while "HTTP" on
        // its own stays whole.
        var next = index + (rune.Utf16SequenceLength);

        return next < text.Length
            && Rune.DecodeFromUtf16(text[next..], out var following, out _) == OperationStatus.Done
            && Rune.IsLower(following);
    }

    private static Rune PreviousRune(ReadOnlySpan<char> text, int index)
    {
        return Rune.DecodeLastFromUtf16(text[..index], out var rune, out _) == OperationStatus.Done
            ? rune
            : default;
    }

    private static void Close(ReadOnlySpan<char> text, ref int start, int end, List<string> words)
    {
        if (start >= 0)
        {
            words.Add(text[start..end].ToString());
            start = -1;
        }
    }
}
