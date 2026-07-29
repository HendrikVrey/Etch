using Etch.Persistence.Session;
using Etch.Persistence.Storage;

namespace Etch.Persistence.Tests;

/// <summary>
/// A throwaway Etch data directory, deleted when the test finishes.
/// </summary>
/// <remarks>
/// Every test that touches the disk goes through this. Nothing in the suite is
/// allowed to resolve a real profile path: a test that wrote to
/// <c>%LOCALAPPDATA%\Etch</c> would destroy the tabs of whoever ran it.
/// </remarks>
internal sealed class TemporaryWorkspace : IDisposable
{
    private TemporaryWorkspace(string root)
    {
        Paths = new EtchPaths(root);
        Buffers = new BufferStore(Paths);
        Sessions = new SessionStore(Paths);
    }

    public EtchPaths Paths { get; }

    public BufferStore Buffers { get; }

    public SessionStore Sessions { get; }

    public string Root => Paths.Root;

    /// <summary>Creates a workspace with the directory tree already in place.</summary>
    public static TemporaryWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "etch-tests", Guid.CreateVersion7().ToString("N"));
        var workspace = new TemporaryWorkspace(root);

        workspace.Paths.EnsureCreated();
        return workspace;
    }

    /// <summary>Creates a workspace whose directories do not exist yet.</summary>
    public static TemporaryWorkspace CreateUninitialised()
    {
        var root = Path.Combine(Path.GetTempPath(), "etch-tests", Guid.CreateVersion7().ToString("N"));
        return new TemporaryWorkspace(root);
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
            // A leaked temp directory fails nobody's build. Masking the real
            // assertion failure with a cleanup exception would.
        }
    }
}
