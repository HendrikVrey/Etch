namespace Etch.Core.Documents;

/// <summary>
/// The concrete answer to "what may this document do?", derived from its size.
/// The UI reads these flags rather than re-deriving thresholds, so there is exactly
/// one place where the degradation rules live.
/// </summary>
/// <param name="Tier">The tier this document fell into.</param>
/// <param name="CanOpen">False only for documents past the hard ceiling.</param>
/// <param name="SyntaxHighlighting">Whether highlighting may be applied.</param>
/// <param name="Folding">Whether folding may be enabled.</param>
/// <param name="DetectOnEdit">
/// Whether format detection re-runs as the user types. When false, detection runs
/// once at load time only.
/// </param>
/// <param name="Journaling">
/// Whether the buffer is continuously written to disk. Disabled for very large
/// buffers, where the disk thrash costs more than the safety is worth.
/// </param>
/// <param name="Notice">
/// A short, user-facing explanation for any degradation, or null when nothing was
/// taken away. Shown quietly in the status bar — never as a dialog.
/// </param>
public readonly record struct DocumentCapabilities(
    DocumentTier Tier,
    bool CanOpen,
    bool SyntaxHighlighting,
    bool Folding,
    bool DetectOnEdit,
    bool Journaling,
    string? Notice)
{
    /// <summary>True when any capability was withheld because of size.</summary>
    /// <remarks>
    /// True for <see cref="None"/> as well, which is worth knowing before relying on it:
    /// that value carries <see cref="DocumentTier.PlainText"/> because there is no tier
    /// meaning "no document", so it reports itself as degraded. Nothing reads this for a
    /// buffer that is absent, and adding a tier for the absent case would put a member on
    /// an enum that <see cref="DocumentSizePolicy.Evaluate"/> can never return — a wider
    /// lie than this one.
    /// </remarks>
    public bool IsDegraded => Tier != DocumentTier.Full;

    /// <summary>
    /// Everything off, for when there is no document at all.
    /// </summary>
    /// <remarks>
    /// Not a tier and not reachable from <see cref="DocumentSizePolicy.Evaluate"/> — it
    /// describes the absence of a buffer rather than a large one. It exists so that the
    /// editor's "no tab bound" path can say what it means to the features that read these
    /// flags, instead of leaving the last real document's capabilities in force over an
    /// empty view. <see cref="Notice"/> is null because there is nothing to explain to
    /// anyone: no document was degraded, there is simply nothing open.
    /// </remarks>
    public static DocumentCapabilities None { get; } = new(
        DocumentTier.PlainText,
        CanOpen: false,
        SyntaxHighlighting: false,
        Folding: false,
        DetectOnEdit: false,
        Journaling: false,
        Notice: null);
}
