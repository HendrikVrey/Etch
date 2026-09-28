using System.Buffers;

namespace Etch.Core.Text;

/// <summary>
/// Base64 and URL-safe base64, over text that may not be tidy.
/// </summary>
/// <remarks>
/// <para>
/// The framework's <c>Convert</c> methods are the right primitives but the wrong
/// front door for a scratchpad. Base64 arrives wrapped at 76 characters from a PEM
/// file, unpadded from a JWT, and with a trailing newline from a shell pipeline,
/// none of which is a reason to tell someone their input is invalid.
/// </para>
/// <para>
/// URL-safe input is translated into the standard alphabet and re-padded rather than
/// decoded by a separate routine, because the two alphabets differ in exactly two
/// characters and a second decoder would be a second place for a bug to live.
/// </para>
/// </remarks>
public static class Base64Text
{
    /// <summary>
    /// Above this, the scratch buffer is heap-allocated rather than stack-allocated.
    /// </summary>
    private const int StackThreshold = 512;

    /// <summary>
    /// Decodes base64 or base64url text, ignoring whitespace and supplying missing padding.
    /// </summary>
    /// <param name="text">The encoded text.</param>
    /// <param name="bytes">The decoded bytes, or null when the text was not valid.</param>
    /// <returns>True when the text decoded.</returns>
    public static bool TryDecode(ReadOnlySpan<char> text, out byte[]? bytes)
    {
        bytes = null;

        // Worst case one character per input character, plus up to three pad characters.
        var capacity = text.Length + 3;
        char[]? rented = null;

        // Explicitly typed, not var: a conditional whose branches are a stackalloc and
        // an array has no natural common type, and only the target type makes it one.
        Span<char> scratch = capacity <= StackThreshold
            ? stackalloc char[StackThreshold]
            : (rented = ArrayPool<char>.Shared.Rent(capacity));

        try
        {
            var length = Canonicalise(text, scratch);

            if (length < 0)
            {
                return false;
            }

            var decoded = new byte[(length / 4) * 3];

            if (!Convert.TryFromBase64Chars(scratch[..length], decoded, out var written))
            {
                return false;
            }

            bytes = written == decoded.Length ? decoded : decoded[..written];
            return true;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Encodes bytes as standard base64.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>
    /// Encodes bytes as URL-safe base64, unpadded.
    /// </summary>
    /// <remarks>
    /// Unpadded because that is what the specification that made this alphabet popular
    /// (JSON Web Tokens, RFC 7515) requires. A padded base64url value is accepted
    /// everywhere but produced almost nowhere.
    /// </remarks>
    public static string EncodeUrl(ReadOnlySpan<byte> bytes)
    {
        var standard = Convert.ToBase64String(bytes);
        var length = standard.Length;

        while (length > 0 && standard[length - 1] == '=')
        {
            length--;
        }

        return string.Create(length, standard, static (destination, source) =>
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = source[i] switch
                {
                    '+' => '-',
                    '/' => '_',
                    var other => other,
                };
            }
        });
    }

    /// <summary>
    /// Copies <paramref name="text"/> into <paramref name="destination"/> as padded,
    /// standard-alphabet base64.
    /// </summary>
    /// <returns>
    /// The number of characters written, or -1 when the input held a character that is
    /// in neither alphabet, mixed the two, or has a length no amount of padding can fix.
    /// </returns>
    /// <remarks>
    /// Mixing the alphabets is rejected rather than tolerated. A string holding both
    /// <c>+</c> and <c>-</c> is not base64 that needs helping; it is something else
    /// entirely, and decoding it would hand back bytes that mean nothing.
    /// </remarks>
    private static int Canonicalise(ReadOnlySpan<char> text, Span<char> destination)
    {
        var written = 0;
        var padding = 0;
        var sawStandard = false;
        var sawUrlSafe = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            // Padding is only legal at the end, and once it starts nothing else may
            // follow. Enforced rather than trusted to Convert, which is stricter about
            // some placements than others.
            if (character == '=')
            {
                padding++;
                continue;
            }

            if (padding > 0)
            {
                return -1;
            }

            var mapped = character;

            switch (character)
            {
                case '+' or '/':
                    sawStandard = true;
                    break;

                case '-':
                    sawUrlSafe = true;
                    mapped = '+';
                    break;

                case '_':
                    sawUrlSafe = true;
                    mapped = '/';
                    break;

                default:
                    if (!char.IsAsciiLetterOrDigit(character))
                    {
                        return -1;
                    }

                    break;
            }

            if (sawStandard && sawUrlSafe)
            {
                return -1;
            }

            if (written == destination.Length)
            {
                return -1;
            }

            destination[written++] = mapped;
        }

        if (written == 0)
        {
            return -1;
        }

        // A base64 group is four characters. One left over cannot be completed by any
        // amount of padding: it encodes six bits, and no whole byte is six bits.
        var remainder = written % 4;

        if (remainder == 1)
        {
            return -1;
        }

        if (remainder == 0)
        {
            return written;
        }

        var needed = 4 - remainder;

        if (written + needed > destination.Length)
        {
            return -1;
        }

        destination.Slice(written, needed).Fill('=');

        return written + needed;
    }
}
