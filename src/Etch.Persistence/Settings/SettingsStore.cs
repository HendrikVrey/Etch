using System.Globalization;
using System.Text.Json;
using Etch.Core.Documents;
using Etch.Persistence.Model;
using Etch.Persistence.Serialization;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Settings;

/// <summary>
/// Reads and writes <c>settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="Session.SessionStore"/> and for the same reason: this is read
/// before the first frame, so an exception escaping it is an Etch that will not start
/// until the user finds and deletes a file they have never heard of. Every failure path
/// degrades to <see cref="EtchSettings.Default"/>.
/// </para>
/// <para>
/// Unlike the session index, a settings file that cannot be parsed is <b>not</b> moved
/// aside. Quarantining the index is right because Etch is about to write a new one over
/// it; here the user is far more likely to have made a typo in a file they were invited
/// to edit, and the fix is to correct that typo. Renaming it out from under them would
/// turn a misplaced comma into lost preferences. Nothing is written back until they
/// change something, at which point the broken file is replaced deliberately.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>
    /// Ceiling on the settings file, in bytes.
    /// </summary>
    /// <remarks>
    /// A few hundred bytes of preferences with generous slack. Anything past this is not
    /// a settings file, and parsing it would only make a corrupt file a slow one.
    /// </remarks>
    private const long MaxSettingsBytes = 256L * 1024;

    /// <summary>The UTF-8 byte-order mark, which a hand-edited file may have acquired.</summary>
    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];

    private readonly EtchPaths _paths;

    /// <summary>
    /// Serialises writes, so that two overlapping saves cannot land out of order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settings panel starts a save without awaiting it, a checkbox must not feel
    /// slow, so two can be in flight at once. Each writes to a uniquely-named temporary
    /// file, so nothing interleaves inside the file; what is not protected without this
    /// gate is the <em>order</em> of the two renames, which is the filesystem's to choose.
    /// The loser of that race decides what is on disk, and it is as likely to be the older
    /// value as the newer one. That surfaces as a setting that did not stick until the
    /// user changed something else.
    /// </para>
    /// <para>
    /// Held only across <c>ConfigureAwait(false)</c> awaits, matching how
    /// <c>Workspace</c> guards <c>session.json</c>: nothing on this path may need the UI
    /// thread to resume while another caller is waiting on the gate.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Creates a store over <paramref name="paths"/>.</summary>
    public SettingsStore(EtchPaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>Loads the settings.</summary>
    /// <remarks>
    /// Never throws, with one deliberate exception: a cancellation requested through
    /// <paramref name="cancellationToken"/> propagates, because a caller that asked for
    /// the read to stop wants to know it did. The startup path passes no token, so for
    /// that caller, the one this contract exists for, the guarantee is unqualified.
    /// </remarks>
    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
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
            return SettingsLoadResult.Unusable($"Settings could not be read ({ex.Message}). Etch's defaults are in use.");
        }
    }

    private async Task<SettingsLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        byte[] bytes;

        try
        {
            // One handle, opened once, for the reason SessionStore gives: checking the
            // length and then reopening to read is a time-of-check/time-of-use gap that a
            // file growing underneath the check walks straight through.
            var stream = new FileStream(
                _paths.SettingsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using (stream.ConfigureAwait(false))
            {
                var length = stream.Length;

                if (length > MaxSettingsBytes)
                {
                    return SettingsLoadResult.Unusable(
                        $"The settings file is {DocumentSizePolicy.Describe(length)}, which is far too large to be one. Etch's defaults are in use.");
                }

                if (length == 0)
                {
                    // A crash during a non-atomic write by something other than Etch. Not
                    // worth a message: an empty file and no file mean the same thing to the
                    // user, and the next save replaces it.
                    return SettingsLoadResult.Missing();
                }

                bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SettingsLoadResult.Missing();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsLoadResult.Unusable(
                $"Settings could not be read ({ex.Message}). Etch's defaults are in use.");
        }

        EtchSettings? settings;

        try
        {
            // The byte-order mark is skipped rather than fed to the parser, which rejects
            // it. This file is documented as hand-editable, so someone opening it in
            // Notepad and pressing save is an expected event rather than a strange one.
            var payload = bytes.AsSpan();

            if (payload.StartsWith(Utf8Preamble))
            {
                payload = payload[Utf8Preamble.Length..];
            }

            settings = JsonSerializer.Deserialize(payload, SettingsJsonContext.Default.EtchSettings);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return SettingsLoadResult.Unusable(
                $"Settings are not valid JSON ({Summarise(ex.Message)}). Etch's defaults are in use - the file has been left alone.");
        }

        if (settings is null)
        {
            return SettingsLoadResult.Missing();
        }

        if (settings.IsFromFutureVersion)
        {
            // Left exactly where it is, and this build declines to save over it. Rewriting
            // a newer file would silently drop every setting this build does not know
            // about, which is how someone loses their preferences by opening an old build
            // once.
            return new SettingsLoadResult(
                EtchSettings.Default,
                SettingsLoadStatus.FromFutureVersion,
                $"Settings were written by a newer version of Etch (v{settings.Version.ToString(CultureInfo.InvariantCulture)}). "
                + "They have been left untouched and this session is using Etch's defaults, so nothing from that version is lost.");
        }

        return SettingsLoadResult.Loaded(settings.Sanitised());
    }

    /// <summary>Writes the settings atomically.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public async Task SaveAsync(EtchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Sanitised on the way out as well as on the way in. The application layer clamps
        // its own inputs, but this is the boundary that owns what reaches the file, and a
        // future caller that forgets should not be able to write a file this same class
        // would then refuse to load.
        //
        // Serialised before the write rather than inside the gate, so the gate is held for
        // the I/O alone.
        var json = JsonSerializer.Serialize(settings.Sanitised(), SettingsJsonContext.Default.EtchSettings);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _paths.EnsureCreated();

            // No backup generation, for the reason the session index has none: this is
            // cheap to lose and expensive to keep a second copy of. Unlike the index it is
            // not even rebuilt: it simply reverts to the shipped defaults, which is a
            // recoverable state rather than a lossy one.
            await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, backupPath: null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Shortens a parser message that may quote the file being rejected.
    /// </summary>
    /// <remarks>
    /// The same treatment <see cref="Session.SessionStore"/> applies, and for the same
    /// reason: a status bar is not the place to render an unbounded string full of
    /// control characters and bidirectional overrides.
    /// </remarks>
    private static string Summarise(string message)
    {
        const int Limit = 160;

        // Backed off by one when the cut would fall between the halves of a surrogate
        // pair. This method exists to keep hostile text out of a status bar, and leaving
        // an orphaned surrogate on the end of it would be this method producing exactly
        // the sort of string it is meant to remove.
        var length = Math.Min(message.Length, Limit);

        if (length < message.Length && length > 0 && char.IsHighSurrogate(message[length - 1]))
        {
            length--;
        }

        var cleaned = string.Create(
            length,
            message,
            static (destination, source) =>
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    var character = source[i];

                    destination[i] = char.IsControl(character)
                        || char.GetUnicodeCategory(character) == UnicodeCategory.Format
                        ? ' '
                        : character;
                }
            });

        return message.Length > Limit ? cleaned + "…" : cleaned;
    }
}

/// <summary>How a settings load turned out.</summary>
public enum SettingsLoadStatus
{
    /// <summary>The file was read and is usable.</summary>
    Loaded = 0,

    /// <summary>There was no file. A first launch, not an error, and not worth a message.</summary>
    Missing = 1,

    /// <summary>
    /// The file existed but could not be used. The defaults are in force and the file
    /// has been left exactly where it is.
    /// </summary>
    Unusable = 2,

    /// <summary>
    /// The file was written by a newer build. Distinct from <see cref="Unusable"/>
    /// because nothing is wrong with it, and because this build must not save over it.
    /// </summary>
    FromFutureVersion = 3,
}

/// <summary>The outcome of loading the settings.</summary>
/// <param name="Settings">The settings, or <see cref="EtchSettings.Default"/> when there was nothing usable.</param>
/// <param name="Status">Which of the outcomes occurred.</param>
/// <param name="Notice">
/// A user-facing explanation when something was wrong, or null when it was not. Shown
/// quietly in the status bar; a dialog on startup would be exactly the interruption Etch
/// promises never to produce.
/// </param>
public sealed record SettingsLoadResult(EtchSettings Settings, SettingsLoadStatus Status, string? Notice)
{
    /// <summary>Whether this build may write the file. False when a newer build owns it.</summary>
    public bool CanSave => Status != SettingsLoadStatus.FromFutureVersion;

    internal static SettingsLoadResult Loaded(EtchSettings settings) =>
        new(settings, SettingsLoadStatus.Loaded, Notice: null);

    internal static SettingsLoadResult Missing() =>
        new(EtchSettings.Default, SettingsLoadStatus.Missing, Notice: null);

    internal static SettingsLoadResult Unusable(string notice) =>
        new(EtchSettings.Default, SettingsLoadStatus.Unusable, notice);
}
