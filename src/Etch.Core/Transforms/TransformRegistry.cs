using Etch.Core.Abstractions;
using Etch.Core.Transforms.Data;
using Etch.Core.Transforms.Encodings;
using Etch.Core.Transforms.Identity;
using Etch.Core.Transforms.Lines;
using Etch.Core.Transforms.Text;
using Etch.Core.Transforms.Time;

namespace Etch.Core.Transforms;

/// <summary>
/// Every transform Etch can perform.
/// </summary>
/// <remarks>
/// <para>
/// <b>A hand-written list, and it stays that way.</b> The obvious alternative —
/// scanning the assembly for implementations of <see cref="ITransform"/> — is the
/// single most reliable way to destroy a startup budget, and it would also make
/// <c>Etch.Core</c> depend on reflection that a trimmed or ahead-of-time build cannot
/// see through. The cost of this decision is one line per transform, paid by the
/// person adding it, once.
/// </para>
/// <para>
/// The instances are built the first time anything touches this class, which is when
/// the palette first opens or the first <c>Ctrl+Enter</c> is pressed — never during
/// startup. That is the plan's "construct nothing until needed", and it is why this is
/// a static field rather than something the application composes at launch.
/// </para>
/// </remarks>
public static class TransformRegistry
{
    private static readonly ITransform[] Transforms =
    [
        // Data
        new FormatJson(),
        new MinifyJson(),
        new SortJsonKeys(),
        new ValidateJson(),
        new EscapeJsonString(),
        new UnescapeJsonString(),

        // Encoding
        new DecodeBase64(),
        new EncodeBase64(),
        new EncodeBase64Url(),
        new DecodeUrl(),
        new EncodeUrl(),
        new DecodeHex(),
        new EncodeHtmlEntities(),
        new DecodeHtmlEntities(),

        // Identity
        new DecodeJwt(),
        new NewGuid(),
        new HashMd5(),
        new HashSha1(),
        new HashSha256(),
        new HashSha512(),

        // Time
        new EpochToIso(),
        new IsoToEpoch(),
        new ToUtc(),
        new ToLocalTime(),

        // Text — case
        new ToCamelCase(),
        new ToPascalCase(),
        new ToSnakeCase(),
        new ToKebabCase(),
        new ToConstantCase(),
        new ToTitleCase(),

        // Text — lines
        new SortLines(),
        new ReverseLines(),
        new DedupeLines(),
        new RemoveBlankLines(),
        new JoinLinesWithCommas(),
        new SplitOnCommas(),

        // Text — whitespace
        new TrimTrailingWhitespace(),
        new CollapseWhitespace(),
        new TabsToSpaces(),
        new SpacesToTabs(),
        new Indent(),
        new Dedent(),
    ];

    /// <summary>
    /// Built with <see cref="Enumerable.ToDictionary{TSource, TKey}(IEnumerable{TSource}, Func{TSource, TKey}, IEqualityComparer{TKey}?)"/>,
    /// which throws on a duplicate key rather than letting a later entry win.
    /// </summary>
    /// <remarks>
    /// The same argument as <c>KeyMap</c>'s dictionary: two transforms sharing an id is a
    /// mistake that must announce itself at the first touch of this class, not one that
    /// makes an entry in the list above silently unreachable. A static constructor throwing
    /// is loud, and the recency list and any future keybinding both key on these ids.
    /// </remarks>
    private static readonly Dictionary<string, ITransform> ById =
        Transforms.ToDictionary(static transform => transform.Id, StringComparer.Ordinal);

    /// <summary>Every registered transform.</summary>
    public static IReadOnlyList<ITransform> All => Transforms;

    /// <summary>Finds a transform by its stable id, or null.</summary>
    /// <remarks>
    /// Ordinal comparison. Ids are identifiers, not words, and a case-insensitive
    /// lookup would let two transforms differing only in case both appear to register.
    /// </remarks>
    public static ITransform? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var transform) ? transform : null;
}
