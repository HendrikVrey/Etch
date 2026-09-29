namespace Etch.Persistence.Model;

/// <summary>
/// The contents of <c>update.json</c>: what the update check remembers between runs.
/// </summary>
/// <param name="LastCheckedUtc">When GitHub was last asked, or null if it never has been.</param>
/// <param name="SkippedVersion">The version the user chose to skip, or null.</param>
/// <remarks>
/// Whether to check at all is a preference and lives in <see cref="EtchSettings"/>; this is
/// bookkeeping. See <c>EtchPaths.UpdateFile</c> for why the two are separate files.
/// </remarks>
public sealed record UpdateState(DateTimeOffset? LastCheckedUtc, string? SkippedVersion)
{
    /// <summary>Nothing checked, nothing skipped.</summary>
    public static UpdateState Empty { get; } = new(null, null);

    /// <summary>
    /// Returns a copy with anything implausible dropped.
    /// </summary>
    /// <remarks>
    /// The skipped version is shown in no message and compared as ordinal text only, but it
    /// is bounded anyway, because it came from a file.
    /// </remarks>
    public UpdateState Sanitised() => this with
    {
        SkippedVersion = SkippedVersion is { Length: > 0 and <= 64 } ? SkippedVersion : null,
    };
}
