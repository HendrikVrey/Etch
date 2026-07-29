using System.Globalization;
using System.Text.Json;
using Etch.Core.Documents;
using Etch.Persistence.Model;
using Etch.Persistence.Serialization;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Session;

/// <summary>
/// Reads and writes <c>session.json</c>, the index of which tabs exist.
/// </summary>
/// <remarks>
/// The index is the least valuable thing Etch stores and is treated accordingly.
/// Losing it costs tab order, titles and caret positions; losing a buffer file costs
/// someone's text. So every failure path here degrades to "start from an empty
/// index" rather than propagating, and the caller rebuilds what it can by looking at
/// which buffer files are actually on disk.
/// </remarks>
public sealed class SessionStore
{
    /// <summary>
    /// Ceiling on the index file, in bytes.
    /// </summary>
    /// <remarks>
    /// Generous: a thousand tabs of metadata is well under a megabyte. Anything past
    /// this is not a session file, and parsing it would only turn a corrupt file into
    /// a slow corrupt file.
    /// </remarks>
    private const long MaxSessionBytes = 8L * 1024 * 1024;

    private readonly EtchPaths _paths;
    private readonly TimeProvider _time;

    /// <summary>Creates a store over <paramref name="paths"/>.</summary>
    /// <param name="paths">The data directory layout.</param>
    /// <param name="timeProvider">
    /// Used only to stamp quarantine file names. Injectable so that the name is
    /// predictable in a test, matching how every other time-dependent operation in
    /// this layer takes its clock from the caller.
    /// </param>
    public SessionStore(EtchPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Loads the session index.</summary>
    /// <remarks>
    /// Never throws — not for a missing file, not for a malformed one, not for a
    /// hostile one. That is a contract rather than an aspiration, which is why there
    /// is a catch-all at the end: this runs before the first frame, and any exception
    /// escaping it is an Etch that will not start until the user finds and deletes a
    /// file they have never heard of.
    /// </remarks>
    public async Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Quarantine($"The session index could not be processed ({ex.Message}).");
        }
    }

    private async Task<SessionLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;

        try
        {
            // One handle, opened once: checking the length via FileInfo and then
            // reopening to read is a time-of-check/time-of-use gap that lets a file
            // growing underneath the check bypass the cap entirely.
            var stream = new FileStream(
                _paths.SessionFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 16,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using (stream.ConfigureAwait(false))
            {
                var length = stream.Length;

                if (length > MaxSessionBytes)
                {
                    return Quarantine(
                        $"The session index is {DocumentSizePolicy.Describe(length)}, which is far too large to be one.");
                }

                if (length == 0)
                {
                    // The signature of a crash during a non-atomic write. Etch's own
                    // writes cannot produce it; something else did.
                    return Quarantine("The session index was empty.");
                }

                bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SessionLoadResult.Missing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Readable text may still be sitting in the buffers directory, so this is
            // recoverable — but the file is left exactly where it is, because a file
            // that could not be read is also one that should not be moved.
            return SessionLoadResult.Unreadable(
                $"The session index could not be read ({ex.Message}). Open tabs were recovered from disk instead.");
        }

        SessionSnapshot? session;

        try
        {
            session = JsonSerializer.Deserialize(bytes, SessionJsonContext.Default.SessionSnapshot);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            // ArgumentException covers a record whose invariants were violated on
            // disk — a file buffer with no path, a blank title, a path that is not a
            // legitimate save target. One bad entry takes the whole index with it,
            // which is acceptable precisely because the text does not live here.
            return Quarantine($"The session index was not valid ({Summarise(ex.Message)}).");
        }

        if (session is null)
        {
            return Quarantine("The session index contained no session.");
        }

        if (session.IsFromFutureVersion)
        {
            // Left exactly where it is. Renaming it would lose the newer build's tab
            // layout — the precise harm this branch exists to prevent — so this build
            // simply declines to use it, and declines to save over it.
            return new SessionLoadResult(
                SessionSnapshot.Empty,
                SessionLoadStatus.FromFutureVersion,
                $"The session index was written by a newer version of Etch (v{session.Version.ToString(CultureInfo.InvariantCulture)}). "
                + "It has been left untouched and this session started empty, so nothing from that version is lost.");
        }

        return SessionLoadResult.Loaded(Sanitise(session));
    }

    /// <summary>Writes the session index atomically.</summary>
    /// <exception cref="IOException">The index could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public async Task SaveAsync(SessionSnapshot session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _paths.EnsureCreated();

        var json = JsonSerializer.Serialize(
            session with { Version = SessionSnapshot.CurrentVersion },
            SessionJsonContext.Default.SessionSnapshot);

        await AtomicFile.WriteAllTextAsync(_paths.SessionFile, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes entries a restore could not act on: nulls, duplicates, and any active
    /// id that no longer names a tab.
    /// </summary>
    /// <remarks>
    /// Duplicates cannot arise from Etch's own writes. They can arise from a merged
    /// or hand-edited file, and two tabs sharing one buffer file would mean two
    /// editors writing over each other with no way for either to win.
    /// </remarks>
    private static SessionSnapshot Sanitise(SessionSnapshot session)
    {
        if (session.Buffers.Count == 0)
        {
            return session with { ActiveBufferId = null, Buffers = [] };
        }

        var seen = new HashSet<BufferId>(session.Buffers.Count);
        var unique = new List<BufferRecord>(session.Buffers.Count);

        foreach (var record in session.Buffers)
        {
            if (record is not null && seen.Add(record.Id))
            {
                unique.Add(record);
            }
        }

        var active = session.ActiveBufferId is { } id && seen.Contains(id)
            ? session.ActiveBufferId
            : null;

        return session with { ActiveBufferId = active, Buffers = unique };
    }

    /// <summary>
    /// Moves an unusable index aside instead of deleting it.
    /// </summary>
    /// <remarks>
    /// It costs a few kilobytes and it is the difference between "Etch ate my tab
    /// layout" and "Etch put my tab layout over there". If the move itself fails the
    /// caller still gets a clean start, because the index is about to be overwritten
    /// by the next save either way.
    /// </remarks>
    private SessionLoadResult Quarantine(string notice)
    {
        var quarantined = _paths.QuarantinedSessionFile(
            _time.GetUtcNow(),
            Path.GetRandomFileName()[..4]);

        try
        {
            File.Move(_paths.SessionFile, quarantined, overwrite: false);
            return SessionLoadResult.Unreadable(
                $"{notice} It was set aside at {quarantined} and open tabs were recovered from disk.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return SessionLoadResult.Unreadable($"{notice} Open tabs were recovered from disk.");
        }
    }

    /// <summary>
    /// Shortens a message that may contain content from the file being rejected.
    /// </summary>
    /// <remarks>
    /// A deserialiser message can quote the offending value, and the offending value
    /// comes from a file that may be megabytes of hostile text. This is a local
    /// desktop app so there is nothing to disclose — the file is the user's own — but
    /// a status bar is not the place to render an unbounded string full of control
    /// characters and bidirectional overrides.
    /// </remarks>
    private static string Summarise(string message)
    {
        const int Limit = 160;

        var cleaned = string.Create(
            Math.Min(message.Length, Limit),
            message,
            static (destination, source) =>
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var character = source[i];
                    destination[i] = char.IsControl(character) ? ' ' : character;
                }
            });

        return message.Length > Limit ? cleaned + "…" : cleaned;
    }
}

/// <summary>How a session index load turned out.</summary>
public enum SessionLoadStatus
{
    /// <summary>The index was read and is usable.</summary>
    Loaded = 0,

    /// <summary>There was no index. A first launch, or everything was wiped.</summary>
    Missing = 1,

    /// <summary>
    /// The index existed but could not be used. Tabs must be recovered from the
    /// buffers directory instead.
    /// </summary>
    Unreadable = 2,

    /// <summary>
    /// The index was written by a newer build and has been left untouched. Distinct
    /// from <see cref="Unreadable"/> because nothing is wrong with it, and because
    /// this build must not save over it.
    /// </summary>
    FromFutureVersion = 3,
}

/// <summary>The outcome of loading the session index.</summary>
/// <param name="Session">The session, or <see cref="SessionSnapshot.Empty"/> when there was nothing usable.</param>
/// <param name="Status">Which of the outcomes occurred.</param>
/// <param name="Notice">
/// A user-facing explanation when something was wrong, or null when it was not.
/// Shown quietly in the status bar — a dialog on startup would be exactly the
/// interruption Etch promises never to produce.
/// </param>
public sealed record SessionLoadResult(SessionSnapshot Session, SessionLoadStatus Status, string? Notice)
{
    /// <summary>
    /// Whether this build may write the index. False when a newer build owns it.
    /// </summary>
    public bool CanSave => Status != SessionLoadStatus.FromFutureVersion;

    internal static SessionLoadResult Loaded(SessionSnapshot session) =>
        new(session, SessionLoadStatus.Loaded, Notice: null);

    internal static SessionLoadResult Missing() =>
        new(SessionSnapshot.Empty, SessionLoadStatus.Missing, Notice: null);

    internal static SessionLoadResult Unreadable(string notice) =>
        new(SessionSnapshot.Empty, SessionLoadStatus.Unreadable, notice);
}
