using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Etch.Persistence.Model;

/// <summary>
/// The contents of <c>session.json</c>: which tabs exist, in what order, and which
/// one was in front.
/// </summary>
/// <param name="Version">
/// Schema version. Bumped when the shape changes incompatibly; see
/// <see cref="CurrentVersion"/> for how a version Etch does not understand is handled.
/// </param>
/// <param name="CleanShutdown">
/// False while Etch is running, and set true only on an orderly exit. On the next
/// launch a false value means the last session ended in a crash or a kill — which
/// changes nothing about what Etch does, and that is the point. Restoring is the
/// only path, so it is exercised on every single launch and cannot rot the way a
/// rarely-taken "recover your files?" branch would.
/// </param>
/// <param name="ActiveBufferId">
/// The tab that had focus, restored and rendered before the others hydrate. Null
/// when the session is empty.
/// </param>
/// <param name="Buffers">Tabs, in display order.</param>
public sealed record SessionSnapshot(
    int Version,
    bool CleanShutdown,
    BufferId? ActiveBufferId,
    IReadOnlyList<BufferRecord> Buffers)
{
    /// <summary>The schema version this build writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Tabs, in display order. Never null.
    /// </summary>
    /// <remarks>
    /// Normalised here rather than trusted, because this record is deserialised
    /// straight from a file the user can edit. System.Text.Json supplies
    /// <c>default</c> — that is, null — for any constructor parameter the payload
    /// omits, so a three-line <c>session.json</c> with no <c>buffers</c> key would
    /// otherwise hand a null list to everything downstream and turn a trivially
    /// malformed file into a startup crash that only a manual file deletion clears.
    /// </remarks>
    public IReadOnlyList<BufferRecord> Buffers { get; init; } = Buffers ?? [];

    /// <summary>A session with no tabs — first launch, or everything closed.</summary>
    public static SessionSnapshot Empty { get; } = new(
        CurrentVersion,
        CleanShutdown: true,
        ActiveBufferId: null,
        Buffers: ReadOnlyCollection<BufferRecord>.Empty);

    /// <summary>
    /// True when this session was written by a build newer than this one.
    /// </summary>
    /// <remarks>
    /// Forward compatibility is not attempted. An older Etch that rewrote a newer
    /// session would silently drop every field it did not know about, which is how
    /// someone loses their tab layout by opening the wrong build once.
    /// </remarks>
    [JsonIgnore]
    public bool IsFromFutureVersion => Version > CurrentVersion;

    /// <summary>
    /// The active record, or the first tab when the recorded active id is stale.
    /// </summary>
    /// <remarks>
    /// A dangling active id is expected, not exceptional: the active tab can be
    /// closed by the crash that ended the last session, after the id was last
    /// flushed. Falling back beats restoring with nothing focused.
    /// </remarks>
    public BufferRecord? ResolveActive()
    {
        if (Buffers.Count == 0)
        {
            return null;
        }

        if (ActiveBufferId is { } active)
        {
            foreach (var buffer in Buffers)
            {
                if (buffer.Id == active)
                {
                    return buffer;
                }
            }
        }

        return Buffers[0];
    }
}
