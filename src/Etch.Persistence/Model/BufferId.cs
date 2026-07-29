using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using Etch.Persistence.Serialization;

namespace Etch.Persistence.Model;

/// <summary>
/// Identifies one buffer, and doubles as the name of the file that stores it.
/// </summary>
/// <remarks>
/// <para>
/// A wrapper rather than a bare <see cref="Guid"/> because this type is the
/// security boundary for every path Etch builds under its data directory. The only
/// way to obtain one is <see cref="New"/> or <see cref="TryParseFileName"/>, and
/// <see cref="FileName"/> is a 32-character hex string by construction — it cannot
/// contain a separator, a drive letter, a <c>..</c> segment, or a reserved device
/// name. Path traversal is therefore not something the storage layer has to defend
/// against; it is unrepresentable.
/// </para>
/// <para>
/// Version 7 GUIDs are time-ordered, so directory enumeration comes back in
/// roughly creation order and the trash can be reasoned about chronologically
/// without stat-ing every file.
/// </para>
/// </remarks>
[JsonConverter(typeof(BufferIdJsonConverter))]
public readonly record struct BufferId
{
    /// <summary>The canonical file extension for a stored buffer.</summary>
    public const string Extension = ".txt";

    private BufferId(Guid value) => Value = value;

    /// <summary>The underlying identifier.</summary>
    public Guid Value { get; }

    /// <summary>True for the uninitialised <c>default</c> value, which never names a file.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Mints a new, time-ordered identifier.</summary>
    public static BufferId New() => new(Guid.CreateVersion7());

    /// <summary>Wraps an existing GUID, for rehydrating a persisted record.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <see cref="Guid.Empty"/>.</exception>
    public static BufferId FromGuid(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An empty GUID does not identify a buffer.", nameof(value));
        }

        return new BufferId(value);
    }

    /// <summary>
    /// The file name this buffer is stored under, including the extension.
    /// </summary>
    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string FileName => IsEmpty
        ? throw new InvalidOperationException("An empty BufferId has no file name.")
        : string.Create(CultureInfo.InvariantCulture, $"{Value:N}{Extension}");

    /// <summary>
    /// Recovers an identifier from a file name produced by <see cref="FileName"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately strict. This is what runs over the contents of the buffers and
    /// trash directories, and a file Etch did not write is a file Etch must not
    /// read, move, or delete — the retention sweep would otherwise be a delete
    /// primitive pointed at whatever happened to be in the folder. Anything that
    /// does not parse exactly is ignored rather than repaired.
    /// </remarks>
    public static bool TryParseFileName(ReadOnlySpan<char> fileName, out BufferId id)
    {
        id = default;

        if (!fileName.EndsWith(Extension, StringComparison.Ordinal))
        {
            return false;
        }

        var stem = fileName[..^Extension.Length];

        // "N" only: exactly 32 hex digits, no braces, no hyphens.
        if (!Guid.TryParseExact(stem, "N", out var value) || value == Guid.Empty)
        {
            return false;
        }

        var candidate = new BufferId(value);

        // Guid.TryParseExact trims leading and trailing whitespace before parsing,
        // and accepts uppercase hex. Both are legal in an NTFS file name, so without
        // this check " <hex>.txt" and "<HEX>.txt" would parse to an id whose FileName
        // is a *different* string. Every caller then acts on the reconstructed
        // canonical path rather than the one it enumerated — which means the trash
        // sweep would delete a file it never looked at while leaving the one it did.
        // Requiring the round trip to be byte-identical is what makes
        // "enumerate, parse, then operate on the canonical name" safe.
        if (!fileName.SequenceEqual(candidate.FileName))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    /// <inheritdoc cref="TryParseFileName(ReadOnlySpan{char}, out BufferId)" />
    public static bool TryParseFileName([NotNullWhen(true)] string? fileName, out BufferId id)
    {
        if (fileName is null)
        {
            id = default;
            return false;
        }

        return TryParseFileName(fileName.AsSpan(), out id);
    }

    /// <inheritdoc />
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}
