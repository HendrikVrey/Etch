using System.Buffers;
using System.Text;
using Etch.Core.Abstractions;

namespace Etch.Core.Detection;

/// <summary>
/// Transcodes a character sample to UTF-8 in pooled memory.
/// </summary>
/// <remarks>
/// <para>
/// <c>Utf8JsonReader</c> works in bytes and detection works in characters, so this
/// conversion is unavoidable for the JSON-shaped detectors — but it happens on every
/// debounce, so it does not get to allocate. One place owns the rent-and-return
/// discipline rather than three detectors each remembering to write a <c>finally</c>.
/// </para>
/// <para>
/// An unpaired surrogate at a sample's cut point transcodes to the replacement
/// character. That is harmless here: it is one more character inside a string literal
/// the reader was going to skip over anyway.
/// </para>
/// </remarks>
internal static class Utf8Scratch
{
    /// <summary>
    /// A scan over transcoded bytes.
    /// </summary>
    /// <remarks>
    /// A named delegate rather than a <c>Func</c> because <see cref="ReadOnlySpan{T}"/>
    /// is a ref struct and cannot be a generic type argument.
    /// </remarks>
    public delegate DetectionConfidence Scan(ReadOnlySpan<byte> utf8, bool isComplete);

    /// <summary>Runs <paramref name="scan"/> over the UTF-8 form of <paramref name="sample"/>.</summary>
    /// <remarks>
    /// Callers pass a <c>static</c> lambda so the delegate is cached rather than
    /// allocated per call — which is the whole point of pooling the buffer.
    /// </remarks>
    public static DetectionConfidence Use(ReadOnlySpan<char> sample, bool isComplete, Scan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);

        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(sample.Length));

        try
        {
            var written = Encoding.UTF8.GetBytes(sample, buffer);

            return scan(buffer.AsSpan(0, written), isComplete);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
