using Etch.Persistence.Model;
using Etch.Persistence.Storage;

namespace Etch.App.Tests;

/// <summary>
/// A throwaway Etch data directory, deleted when the test finishes.
/// </summary>
/// <remarks>
/// Every test that touches the disk goes through this. Nothing in the suite is allowed
/// to resolve a real profile path: a test that wrote to <c>%LOCALAPPDATA%\Etch</c>
/// would destroy the tabs of whoever ran it.
/// </remarks>
internal sealed class TemporaryDataDirectory : IDisposable
{
    private TemporaryDataDirectory(string root) => Paths = new EtchPaths(root);

    public EtchPaths Paths { get; }

    public string Root => Paths.Root;

    /// <summary>Creates a directory with the tree already in place.</summary>
    public static TemporaryDataDirectory Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "etch-app-tests", Guid.CreateVersion7().ToString("N"));
        var directory = new TemporaryDataDirectory(root);

        directory.Paths.EnsureCreated();
        return directory;
    }

    /// <summary>Reads a buffer file directly, bypassing the store.</summary>
    /// <remarks>
    /// Deliberately not through <see cref="BufferStore"/>: several of these tests exist to
    /// prove what is actually on disk, and asserting through the same abstraction that
    /// wrote it would let a bug in that abstraction pass unnoticed.
    /// </remarks>
    public string? ReadBuffer(BufferId id)
    {
        var path = Paths.BufferFile(id);

        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Reads a trashed buffer file directly.</summary>
    public string? ReadTrashed(BufferId id)
    {
        var path = Paths.TrashFile(id);

        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory fails nobody's build. Masking the real assertion
            // failure with a cleanup exception would.
        }
    }
}
