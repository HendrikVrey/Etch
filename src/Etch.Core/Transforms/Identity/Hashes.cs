using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Etch.Core.Abstractions;

namespace Etch.Core.Transforms.Identity;

/// <summary>
/// The shared body of every hash transform.
/// </summary>
/// <remarks>
/// <para>
/// <b>UTF-8, unconditionally, and it is worth being loud about.</b> A hash is a function of
/// bytes, and text does not become bytes until an encoding says so — so "the SHA-256 of
/// this string" is only a well-defined question once the encoding is fixed. UTF-8 is what
/// every other tool the answer will be compared against uses. The alternative, following
/// the document's own encoding, would mean the same visible text hashed to two different
/// values in two tabs, which is the single most confusing thing a hashing tool can do.
/// </para>
/// <para>
/// Lower-case hex output, again because that is what the tools this will be checked
/// against print: <c>sha256sum</c>, <c>git</c>, <c>openssl</c> and every language's
/// standard library.
/// </para>
/// </remarks>
internal abstract class HashTransform : ITransform
{
    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public TransformCategory Category => TransformCategory.Identity;

    /// <inheritdoc />
    public abstract IReadOnlyList<string> Aliases { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Never suggested. Hashing is something a person decides to do; no property of a
    /// buffer implies it, and <c>Ctrl+Enter</c> replacing a document with 64 hex characters
    /// would be the most destructive thing in the catalogue.
    /// </remarks>
    public bool IsAvailable(in DetectionResult detection) => false;

    /// <inheritdoc />
    public TransformResult Apply(in TransformInput input, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(input.Text);

        return TransformResult.Ok(Convert.ToHexStringLower(ComputeHash(bytes)), FormatId.Hex);
    }

    /// <summary>Hashes the encoded bytes.</summary>
    protected abstract byte[] ComputeHash(byte[] bytes);
}

/// <summary>
/// MD5. Kept for checking file manifests and cache keys, not for security.
/// </summary>
/// <remarks>
/// The name says <em>legacy</em> where the palette can see it, which is the whole reason
/// this is not just another row in a list. MD5 has been collision-broken since 2004 and
/// anyone reaching for it to protect something needs to be told so at the moment they reach
/// — not in documentation they will not read. It stays in the catalogue because verifying
/// an MD5 someone else published is a real and blameless task.
/// </remarks>
internal sealed class HashMd5 : HashTransform
{
    /// <inheritdoc />
    public override string Id => "hash.md5";

    /// <inheritdoc />
    public override string Name => "MD5 (legacy - not for security)";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["md5", "hash", "checksum", "digest"];

    /// <inheritdoc />
    [SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification =
            "Deliberate, and the analyser is reading the intent backwards. Etch is not " +
            "protecting anything with MD5; it is showing the user the MD5 of text they " +
            "already have, because the world is full of MD5 checksums that need comparing. " +
            "Refusing to compute one would not make those go away, it would just mean " +
            "reaching for a website to paste the text into. The transform is named " +
            "\"MD5 (legacy - not for security)\" in the palette so the caveat travels with it.")]
    protected override byte[] ComputeHash(byte[] bytes) => MD5.HashData(bytes);
}

/// <summary>SHA-1. Same caveat as MD5, and the same reason for keeping it.</summary>
/// <remarks>
/// Collision-broken in practice since SHAttered in 2017. Still what a git object id is, and
/// that is the request this exists to serve.
/// </remarks>
internal sealed class HashSha1 : HashTransform
{
    /// <inheritdoc />
    public override string Id => "hash.sha1";

    /// <inheritdoc />
    public override string Name => "SHA-1 (legacy - not for security)";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["sha1", "hash", "checksum", "digest", "git"];

    /// <inheritdoc />
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification =
            "Same reasoning as CA5351 on MD5 above, with one addition that makes it stronger: " +
            "a git object id is a SHA-1, so this is the transform someone reaches for when " +
            "checking one. Displayed, never trusted, and labelled legacy in its own name.")]
    protected override byte[] ComputeHash(byte[] bytes) => SHA1.HashData(bytes);
}

/// <summary>SHA-256. The default answer when someone says "hash this".</summary>
internal sealed class HashSha256 : HashTransform
{
    /// <inheritdoc />
    public override string Id => "hash.sha256";

    /// <inheritdoc />
    public override string Name => "SHA-256";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["sha256", "sha-256", "hash", "checksum", "digest"];

    /// <inheritdoc />
    protected override byte[] ComputeHash(byte[] bytes) => SHA256.HashData(bytes);
}

/// <summary>SHA-512.</summary>
internal sealed class HashSha512 : HashTransform
{
    /// <inheritdoc />
    public override string Id => "hash.sha512";

    /// <inheritdoc />
    public override string Name => "SHA-512";

    /// <inheritdoc />
    public override IReadOnlyList<string> Aliases { get; } = ["sha512", "sha-512", "hash", "checksum", "digest"];

    /// <inheritdoc />
    protected override byte[] ComputeHash(byte[] bytes) => SHA512.HashData(bytes);
}
