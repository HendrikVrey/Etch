using System.Text.Json.Serialization;

namespace Etch.Persistence.Model;

/// <summary>
/// The contents of <c>settings.json</c>: every choice the user has made.
/// </summary>
/// <param name="Version">
/// Schema version. Bumped when the shape changes incompatibly; see
/// <see cref="CurrentVersion"/> for how a version Etch does not understand is handled.
/// </param>
/// <param name="TrashRetentionDays">
/// How long a closed tab stays recoverable, in days. Zero deletes on close, which is a
/// privacy affordance rather than an edge case: the README offers it as one.
/// </param>
/// <param name="Ligatures">
/// Whether the editor font may form ligatures. Cascadia Mono has them and some people
/// cannot read <c>!=</c> as <c>≠</c>.
/// </param>
/// <param name="ReducedThresholdBytes">Size above which folding is switched off.</param>
/// <param name="PlainTextThresholdBytes">Size above which highlighting and auto-save are switched off.</param>
/// <param name="HardCeilingBytes">Size above which a document is refused.</param>
/// <param name="CheckForUpdates">
/// Whether Etch may ask GitHub once a day for a new version. Null until the user has been
/// asked, which is what makes Etch ask: the check is opt-in, so no answer is never taken
/// as a yes. Optional in the constructor so a file written before it existed still reads.
/// </param>
/// <remarks>
/// <para>
/// <b>Primitives, not the policy types they configure.</b> Storing a
/// <c>RetentionPolicy</c> or a <c>DocumentSizePolicy</c> here would tie the file format
/// to two classes that exist to be constructed with validation, and a validating
/// constructor is exactly what must not run against untrusted bytes during a
/// deserialisation. The mapping onto those types happens in the application layer,
/// against values <see cref="Sanitised"/> has already checked.
/// </para>
/// <para>
/// <b>File associations are deliberately not here.</b> They live in the registry, and
/// the registry is where Windows itself, the Default Apps UI and every other
/// application can change them, so a copy in this file would be a second statement of
/// the same fact, free to drift, with nothing to say which of the two was true. The
/// settings UI reads the registry.
/// </para>
/// <para>
/// Nothing here is a secret and nothing here is a path. That matters because this file
/// survives "wipe all scratch data", wiping someone's preferences because they wiped
/// their buffers would be a surprise, so it must never be somewhere a secret could
/// come to rest.
/// </para>
/// </remarks>
public sealed record EtchSettings(
    int Version,
    int TrashRetentionDays,
    bool Ligatures,
    long ReducedThresholdBytes,
    long PlainTextThresholdBytes,
    long HardCeilingBytes,
    bool? CheckForUpdates = null)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The largest retention window that can be chosen, in days.</summary>
    /// <remarks>
    /// A year is far past any plausible use, and it is also what stops an overflow:
    /// retention becomes a <see cref="TimeSpan"/> added to a <see cref="DateTimeOffset"/>,
    /// and <c>int.MaxValue</c> days is representable as neither.
    /// </remarks>
    public const int MaxRetentionDays = 365;

    /// <summary>The smallest threshold that may be configured, in bytes.</summary>
    /// <remarks>
    /// 64 KiB. Below this the reduced tier would swallow ordinary source files, and an
    /// editor that turns folding off for a 2 KB file reads as broken rather than careful.
    /// </remarks>
    public const long MinThresholdBytes = 64L * 1024L;

    /// <summary>The largest threshold that may be configured, in bytes.</summary>
    /// <remarks>
    /// 4 GiB. The ceiling exists so a hand-edited file cannot ask Etch to load a document
    /// that cannot exist: .NET's maximum object size stops well short of this, and the
    /// loader would fail far less clearly than this refusal does.
    /// </remarks>
    public const long MaxThresholdBytes = 4L * 1024L * 1024L * 1024L;

    /// <summary>The shipped defaults. These match <c>DocumentSizePolicy.Default</c>.</summary>
    public static EtchSettings Default { get; } = new(
        CurrentVersion,
        TrashRetentionDays: 7,
        Ligatures: true,
        ReducedThresholdBytes: 2L * 1024 * 1024,
        PlainTextThresholdBytes: 10L * 1024 * 1024,
        HardCeilingBytes: 100L * 1024 * 1024,
        CheckForUpdates: null);

    /// <summary>
    /// True when this file was written by a build newer than this one.
    /// </summary>
    /// <remarks>
    /// Forward compatibility is not attempted, for the reason the session index does not
    /// attempt it: an older build that rewrote a newer file would silently drop every
    /// field it did not know about.
    /// </remarks>
    [JsonIgnore]
    public bool IsFromFutureVersion => Version > CurrentVersion;

    /// <summary>
    /// Returns a copy with every value forced into its legal range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clamping rather than rejecting, and that is the whole design of this type. The
    /// file is hand-editable, and one bad number must not cost the user every other
    /// preference they set, so an out-of-range value falls back to the shipped default
    /// for that field alone and the rest are kept.
    /// </para>
    /// <para>
    /// The three thresholds are treated as one group because they are only meaningful as
    /// one: <c>DocumentSizePolicy</c> requires them strictly ascending and throws
    /// otherwise. A set that does not ascend is replaced wholesale rather than repaired,
    /// because there is no honest way to guess which of the three the user meant, and
    /// repairing two of them to satisfy the third would produce a policy nobody chose.
    /// </para>
    /// </remarks>
    public EtchSettings Sanitised()
    {
        var retention = TrashRetentionDays is >= 0 and <= MaxRetentionDays
            ? TrashRetentionDays
            : Default.TrashRetentionDays;

        var thresholdsAreUsable = ReducedThresholdBytes >= MinThresholdBytes
            && HardCeilingBytes <= MaxThresholdBytes
            && ReducedThresholdBytes < PlainTextThresholdBytes
            && PlainTextThresholdBytes < HardCeilingBytes;

        return this with
        {
            Version = CurrentVersion,
            TrashRetentionDays = retention,
            ReducedThresholdBytes = thresholdsAreUsable ? ReducedThresholdBytes : Default.ReducedThresholdBytes,
            PlainTextThresholdBytes = thresholdsAreUsable ? PlainTextThresholdBytes : Default.PlainTextThresholdBytes,
            HardCeilingBytes = thresholdsAreUsable ? HardCeilingBytes : Default.HardCeilingBytes,
        };
    }
}
