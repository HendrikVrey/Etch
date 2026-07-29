using System.Text;
using Etch.Core.Documents;
using Etch.Core.Text;

namespace Etch.App.Editor;

/// <summary>A file read from disk, with everything the editor and status bar need.</summary>
/// <param name="Path">The absolute path it was read from.</param>
/// <param name="Text">The contents, truncated if the file exceeded the read cap.</param>
/// <param name="Encoding">The encoding actually used, after byte-order-mark detection.</param>
/// <param name="LineEnding">The dominant newline convention.</param>
/// <param name="SizeInBytes">The size reported by the handle the read was performed on.</param>
/// <param name="Capabilities">What the editor may switch on for a document this size.</param>
/// <param name="WasTruncated">
/// True when the file grew past the ceiling mid-read and the tail was dropped.
/// The user must be told: a silently partial buffer is one <c>Ctrl+S</c> away from
/// destroying the rest of their file.
/// </param>
/// <param name="ReadDuration">
/// How long the read itself took, excluding handing the text to the editor. Split
/// out because I/O and document construction are optimised in completely different
/// ways, and a single combined number tells you nothing about which to attack.
/// </param>
internal sealed record LoadedDocument(
    string Path,
    string Text,
    Encoding Encoding,
    LineEndingStyle LineEnding,
    long SizeInBytes,
    DocumentCapabilities Capabilities,
    bool WasTruncated,
    TimeSpan ReadDuration);

/// <summary>The outcome of attempting to load a file.</summary>
internal abstract record DocumentLoadResult
{
    private protected DocumentLoadResult()
    {
    }

    /// <summary>The file was read.</summary>
    internal sealed record Loaded(LoadedDocument Document) : DocumentLoadResult;

    /// <summary>The file was not opened, for a reason worth showing the user.</summary>
    internal sealed record Refused(string Reason) : DocumentLoadResult;
}
