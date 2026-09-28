namespace Etch.Persistence.Model;

/// <summary>
/// Whether a buffer belongs to Etch or to the user.
/// </summary>
/// <remarks>
/// This is the single most consequential distinction in the persistence layer.
/// Etch's promise is that it never shows a save dialog, and the only way to keep
/// that promise safely is to be certain which files it is allowed to write to
/// without being asked. Getting it wrong in the <see cref="Scratch"/> direction
/// loses a scratch note; getting it wrong in the <see cref="File"/> direction
/// silently overwrites something the user cares about.
/// </remarks>
public enum BufferKind
{
    /// <summary>
    /// Owned by Etch, with no user-chosen path. Continuously journaled to the app's
    /// own data directory and never written anywhere else. This is the default and
    /// the overwhelmingly common case.
    /// </summary>
    Scratch = 0,

    /// <summary>
    /// Backed by a file on disk that the user opened. Edits are journaled to Etch's
    /// shadow copy, never written through to the original, that happens only on an
    /// explicit save.
    /// </summary>
    File = 1,
}
