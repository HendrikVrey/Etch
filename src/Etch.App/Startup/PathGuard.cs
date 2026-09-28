using System.Diagnostics.CodeAnalysis;

namespace Etch.App.Startup;

/// <summary>
/// Validates file paths arriving from outside the process.
/// </summary>
/// <remarks>
/// There are two such boundaries and they must agree: the command line, and the
/// hand-off pipe another Etch launch uses to pass a file to the running instance.
/// Keeping one implementation is not tidiness: a validator that is stricter on one
/// route than the other is a validator with a bypass.
/// </remarks>
internal static class PathGuard
{
    /// <summary>
    /// Resolves a user-supplied path to absolute form, rejecting anything malformed
    /// or pointing at something that is not an ordinary file.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> normalises away <c>..</c> traversal and
    /// relative segments, so downstream code only ever sees a fully resolved path. It
    /// throws on invalid characters and over-long paths, which is exactly the
    /// validation wanted: converted here into a clear message rather than an
    /// unhandled exception at startup.
    /// <para>
    /// It also resolves DOS device names, so <c>CON</c> and <c>\\.\PhysicalDrive0</c>
    /// would otherwise pass. Those are rejected explicitly: these paths reach a file
    /// open, and one of the callers opens for writing.
    /// </para>
    /// <para>
    /// Nothing here touches the file system. This runs before the window exists, and
    /// a single <c>File.Exists</c> on a UNC path would block startup for the SMB
    /// timeout.
    /// </para>
    /// </remarks>
    public static bool TryResolve(
        string? candidate,
        [NotNullWhen(true)] out string? fullPath,
        [NotNullWhen(false)] out string? error)
    {
        fullPath = null;
        error = null;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "A path was expected but the value was empty.";
            return false;
        }

        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            error = "A path cannot contain a null character.";
            return false;
        }

        string resolved;

        try
        {
            resolved = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"'{candidate}' is not a usable path: {ex.Message}";
            return false;
        }

        if (resolved.StartsWith(@"\\.\", StringComparison.Ordinal) || resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            error = $"'{candidate}' resolves to a device path, which Etch will not open.";
            return false;
        }

        if (!Path.IsPathFullyQualified(resolved))
        {
            error = $"'{candidate}' does not resolve to a fully qualified path.";
            return false;
        }

        fullPath = resolved;
        return true;
    }
}
