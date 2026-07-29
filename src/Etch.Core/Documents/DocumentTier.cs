namespace Etch.Core.Documents;

/// <summary>
/// How much editor machinery a document is allowed to switch on, chosen by size.
/// AvalonEdit is a code editor, not a log viewer; these tiers are where we admit
/// that out loud instead of degrading unpredictably.
/// </summary>
public enum DocumentTier
{
    /// <summary>Everything on: highlighting, folding, detection re-runs while editing.</summary>
    Full,

    /// <summary>Folding off and detection runs once on load rather than on every edit.</summary>
    Reduced,

    /// <summary>Plain text only: no highlighting, no folding, and the buffer is not journaled.</summary>
    PlainText,

    /// <summary>Too large to open in the editor at all.</summary>
    Rejected,
}
