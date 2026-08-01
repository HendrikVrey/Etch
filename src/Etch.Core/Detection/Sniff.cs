namespace Etch.Core.Detection;

/// <summary>
/// The cheap character tests every detector's first phase is built from.
/// </summary>
/// <remarks>
/// Phase one of detection exists to rule things out for almost nothing, so everything
/// here is a character comparison. No regular expressions: a regex over a 64 KB sample,
/// run once per detector on a 150 ms debounce, is the difference between detection you
/// never notice and detection you feel while typing.
/// </remarks>
internal static class Sniff
{
    /// <summary>True when the span holds nothing but whitespace.</summary>
    public static bool IsBlank(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first non-whitespace character, or <c>'\0'</c> when there is none.</summary>
    public static char FirstMeaningful(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                return character;
            }
        }

        return '\0';
    }

    /// <summary>True for <c>0-9 a-f A-F</c>.</summary>
    /// <remarks>
    /// The arithmetic form rather than <c>char.IsAsciiHexDigit</c> because this runs per
    /// character over the whole sample; <c>| 0x20</c> folds the case in one operation
    /// instead of branching on two ranges.
    /// </remarks>
    public static bool IsHexDigit(char character) =>
        (uint)(character - '0') <= 9
        || (uint)((character | 0x20) - 'a') <= 5;

    /// <summary>True for the URL-safe base64 alphabet, excluding padding.</summary>
    public static bool IsBase64UrlSymbol(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
