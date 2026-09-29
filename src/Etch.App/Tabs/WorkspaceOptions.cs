using Etch.Core.Documents;
using Etch.Persistence.Journal;
using Etch.Persistence.Storage;

namespace Etch.App.Tabs;

/// <summary>
/// The knobs a <see cref="Workspace"/> is built with.
/// </summary>
/// <remarks>
/// Grouped into one object so that the workspace constructor does not grow a
/// parameter per setting, and so that a test can vary one of them without restating
/// the rest. Of these, only <see cref="Retention"/> is a user setting.
/// </remarks>
internal sealed record WorkspaceOptions
{
    /// <summary>The shipped defaults.</summary>
    public static WorkspaceOptions Default { get; } = new();

    /// <summary>How long a closed tab is recoverable.</summary>
    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;

    /// <summary>Debounce and latency ceiling for auto-save.</summary>
    public JournalOptions Journal { get; init; } = JournalOptions.Default;

    /// <summary>Where the size-based degradation thresholds sit.</summary>
    /// <remarks>
    /// Not a user setting since 2026-09-29: the application always runs on
    /// <see cref="DocumentSizePolicy.Default"/>. It stays an option so tests can exercise
    /// the tiers with kilobytes rather than megabytes.
    /// </remarks>
    public DocumentSizePolicy SizePolicy { get; init; } = DocumentSizePolicy.Default;

    /// <summary>
    /// How many closed tabs stay on the reopen stack.
    /// </summary>
    /// <remarks>
    /// The stack is a convenience over the trash, not a second copy of it: the files
    /// are on disk for the whole retention window either way. Bounding it stops a
    /// long session from holding an unbounded list of identifiers, and twenty is far
    /// past the point where anyone is still pressing Ctrl+Shift+T deliberately.
    /// </remarks>
    public int ReopenHistoryDepth { get; init; } = 20;
}
