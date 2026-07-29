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
    public bool IsDegraded => Tier != DocumentTier.Full;
}
