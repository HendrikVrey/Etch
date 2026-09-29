using System.Text.Json;
using Etch.Persistence.Model;
using Etch.Persistence.Serialization;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Settings;

/// <summary>
/// Reads and writes <c>update.json</c>.
/// </summary>
/// <remarks>
/// Every failure to read is the empty state, silently: the worst that follows is one extra
/// check against GitHub, or a skipped version offered again, and neither is worth a message.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate is a SemaphoreSlim that is never waited on with a wait handle, so it holds no kernel object to release, and the store lives as long as the window.")]
public sealed class UpdateStateStore
{
    private const long MaxBytes = 16L * 1024;

    private readonly EtchPaths _paths;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Creates a store over <paramref name="paths"/>.</summary>
    public UpdateStateStore(EtchPaths paths) =>
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>Loads the state. Never throws, except for a requested cancellation.</summary>
    public async Task<UpdateState> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var info = new FileInfo(_paths.UpdateFile);

            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return UpdateState.Empty;
            }

            var bytes = await File.ReadAllBytesAsync(_paths.UpdateFile, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize(bytes, SettingsJsonContext.Default.UpdateState);

            return state?.Sanitised() ?? UpdateState.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return UpdateState.Empty;
        }
    }

    /// <summary>Writes the state atomically.</summary>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public async Task SaveAsync(UpdateState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var json = JsonSerializer.Serialize(state.Sanitised(), SettingsJsonContext.Default.UpdateState);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _paths.EnsureCreated();
            await AtomicFile.WriteAllTextAsync(_paths.UpdateFile, json, backupPath: null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
