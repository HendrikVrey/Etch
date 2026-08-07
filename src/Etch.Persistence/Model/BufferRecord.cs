using System.Text.Json.Serialization;

namespace Etch.Persistence.Model;

/// <summary>
/// Everything about one tab except its text, which lives in its own file.
/// </summary>
/// <remarks>
/// Text is kept out of the session index on purpose. The index is rewritten on
/// every tab switch and reorder; folding megabytes of buffer content into it would
/// make those writes proportional to how much is open, and a torn write would take
/// every tab with it instead of one.
/// </remarks>
public sealed record BufferRecord
{
    /// <summary>
    /// Ceiling on a stored file path.
    /// </summary>
    /// <remarks>
    /// Long-path awareness makes <c>MAX_PATH</c> the wrong number, but an unbounded
    /// string from an untrusted file is worse. This is generous enough for any real
    /// path and small enough that a hostile index cannot be used to allocate.
    /// </remarks>
    private const int MaxFilePathLength = 4096;

    /// <summary>Creates a record, enforcing the kind/path invariant.</summary>
    /// <param name="id">Identifies the buffer, and names its file.</param>
    /// <param name="kind">Whether Etch owns this buffer or the user does.</param>
    /// <param name="title">The tab caption. For a file buffer this defaults to the file name.</param>
    /// <param name="filePath">
    /// The user's file for a <see cref="BufferKind.File"/> buffer; null for scratch.
    /// </param>
    /// <param name="caretOffset">Character offset of the caret, restored on load.</param>
    /// <param name="firstVisibleLine">Scroll position, as a one-based line number.</param>
    /// <param name="isPinned">Pinned tabs are restored first and are not swept by any cleanup.</param>
    /// <param name="formatOverride">
    /// A format the user pinned manually, overriding detection. Null means "trust
    /// detection". Stored as a string rather than an enum so a session written by a
    /// later version that knows more formats survives a round trip through this one.
    /// </param>
    /// <param name="lastModifiedUtc">When the text last changed.</param>
    /// <exception cref="ArgumentException">
    /// The identifier is empty, the title is blank, the path does not match the kind,
    /// or the path is not one this application is willing to treat as a save target.
    /// </exception>
    [JsonConstructor]
    public BufferRecord(
        BufferId id,
        BufferKind kind,
        string title,
        string? filePath,
        int caretOffset,
        int firstVisibleLine,
        bool isPinned,
        string? formatOverride,
        DateTimeOffset lastModifiedUtc)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A buffer record needs a real identifier.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (kind == BufferKind.File)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("A file buffer must carry the path it came from.", nameof(filePath));
            }

            filePath = ValidateFilePath(filePath);
        }
        else if (filePath is not null)
        {
            throw new ArgumentException(
                "A scratch buffer must not carry a file path - it is what stops an implicit write-through.",
                nameof(filePath));
        }

        // Negative offsets come from a hand-edited or truncated session file. Clamp
        // rather than throw: a nonsensical caret position is not a reason to refuse
        // to restore someone's text.
        Id = id;
        Kind = kind;
        Title = title;
        FilePath = filePath;
        CaretOffset = Math.Max(0, caretOffset);
        FirstVisibleLine = Math.Max(1, firstVisibleLine);
        IsPinned = isPinned;
        FormatOverride = string.IsNullOrWhiteSpace(formatOverride) ? null : formatOverride;
        LastModifiedUtc = lastModifiedUtc;
    }

    /// <summary>Identifies the buffer, and names its file.</summary>
    public BufferId Id { get; }

    /// <summary>Whether Etch owns this buffer or the user does.</summary>
    public BufferKind Kind { get; }

    /// <summary>The tab caption.</summary>
    public string Title { get; }

    /// <summary>The user's file, or null for a scratch buffer.</summary>
    public string? FilePath { get; }

    /// <summary>Character offset of the caret.</summary>
    public int CaretOffset { get; }

    /// <summary>Scroll position, as a one-based line number.</summary>
    public int FirstVisibleLine { get; }

    /// <summary>Whether the tab is pinned.</summary>
    public bool IsPinned { get; }

    /// <summary>A manually pinned format, or null to trust detection.</summary>
    public string? FormatOverride { get; }

    /// <summary>When the text last changed.</summary>
    public DateTimeOffset LastModifiedUtc { get; }

    /// <summary>
    /// Checks that a path is one Etch is willing to remember as a save target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>session.json</c> is the untrusted-input boundary, and this is the only field
    /// in it that names a location outside Etch's own directory. On an explicit save
    /// it becomes a write target, so a planted index would otherwise be a persistent,
    /// reboot-surviving arbitrary-write primitive that fires on a routine keystroke —
    /// with the title attacker-controlled too, so the tab can be dressed up as the
    /// user's own file.
    /// </para>
    /// <para>
    /// Honest scope: anyone who can write <c>session.json</c> already runs as the user
    /// and could write the target file directly. This is not a privilege boundary; it
    /// closes a latent-payload route and it costs almost nothing to close now, versus
    /// retrofitting it once a tab model and a save command sit on top.
    /// </para>
    /// <para>
    /// UNC paths are allowed. Opening a file from a share is an ordinary thing to do,
    /// and refusing it would break a real workflow to defend against an attacker who
    /// already has code execution. Device paths are not: <c>\\.\</c> and <c>\\?\</c>
    /// never come from a file-open dialog and reach things that are not files.
    /// </para>
    /// </remarks>
    private static string ValidateFilePath(string filePath)
    {
        if (filePath.Length > MaxFilePathLength)
        {
            throw new ArgumentException(
                $"A file path of {filePath.Length} characters is not a real path.",
                nameof(filePath));
        }

        if (filePath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || filePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || filePath.StartsWith("//./", StringComparison.Ordinal)
            || filePath.StartsWith("//?/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Device paths are not valid buffer targets.", nameof(filePath));
        }

        if (filePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A file path cannot contain a null character.", nameof(filePath));
        }

        if (!Path.IsPathFullyQualified(filePath))
        {
            // A relative path would resolve against the working directory, which for a
            // shell-launched editor is wherever the user happened to be standing.
            throw new ArgumentException(
                "A file buffer's path must be absolute.",
                nameof(filePath));
        }

        string canonical;

        try
        {
            canonical = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"'{filePath}' is not a usable file path.", nameof(filePath), ex);
        }

        // Requiring the already-canonical form rejects traversal segments and the
        // trailing dots and spaces Windows silently strips — both of which let two
        // different strings name the same file, which is how a check on one string
        // ends up guarding a write to another.
        if (!string.Equals(canonical, filePath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A file buffer's path must already be in canonical form.",
                nameof(filePath));
        }

        return canonical;
    }

    /// <summary>Creates a scratch record for a brand-new, empty tab.</summary>
    public static BufferRecord NewScratch(string title, DateTimeOffset now) =>
        new(
            BufferId.New(),
            BufferKind.Scratch,
            title,
            filePath: null,
            caretOffset: 0,
            firstVisibleLine: 1,
            isPinned: false,
            formatOverride: null,
            lastModifiedUtc: now);
}
