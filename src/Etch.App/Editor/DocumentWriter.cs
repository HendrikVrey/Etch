using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Editor;

/// <summary>
/// Writes a buffer back to the user's own file.
/// </summary>
/// <remarks>
/// <para>
/// The only place Etch writes outside its own data directory, and the only write a
/// user explicitly asks for. Everything else is journaled to a shadow copy: the
/// distinction between the two is the single most consequential one in the whole
/// design, because getting it wrong means silently replacing a file someone cared
/// about.
/// </para>
/// <para>
/// Not <see cref="Persistence.Storage.AtomicFile"/>, deliberately, and the trade-off
/// is worth stating rather than assuming. That type replaces a destination by
/// renaming a new file over it, which is exactly right for files Etch created and
/// owns. Applied to the user's file it would hand back a *different* file: a new
/// security descriptor inherited from the temporary one, a new file id, and any hard
/// link or alternate data stream pointing at the original left behind. Writing in
/// place keeps the file the user opened.
/// </para>
/// <para>
/// The cost is a window in which the file is truncated and not yet rewritten, which a
/// power cut turns into a short file. Etch is unusually well placed to survive that:
/// the same text is in the editor and in the journaled shadow copy under
/// <c>%LOCALAPPDATA%</c>, so the content still exists in two places even if this write
/// does not finish. That mitigation is what makes writing in place the better of the
/// two, not an argument that the window does not exist.
/// </para>
/// </remarks>
internal static class DocumentWriter
{
    private const int BufferSize = 1 << 16;

    /// <summary>
    /// Writes <paramref name="snapshot"/> to <paramref name="path"/> in
    /// <paramref name="encoding"/>.
    /// </summary>
    /// <param name="path">An absolute path the user chose.</param>
    /// <param name="snapshot">
    /// An immutable view of the document, taken on the UI thread. The write itself
    /// runs off it.
    /// </param>
    /// <param name="encoding">
    /// The encoding the file was read with. Preserved rather than normalised:
    /// rewriting a UTF-16 configuration file as UTF-8 because Etch prefers UTF-8 is a
    /// silent, tool-breaking change to a file the user only meant to edit.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static Task WriteThroughAsync(
        string path,
        ITextSource snapshot,
        Encoding encoding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(encoding);

        // Off the UI thread as a whole, not merely awaited from it. Opening a FileStream
        // is synchronous however the handle is configured, and Flush(flushToDisk) is a
        // blocking disk round trip, both of which land on whatever thread called in, and
        // the caller is the UI thread. A network share makes the open alone seconds long.
        return Task.Run(() => WriteCoreAsync(path, snapshot, encoding, cancellationToken), cancellationToken);
    }

    private static async Task WriteCoreAsync(
        string path,
        ITextSource snapshot,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, encoding, BufferSize, leaveOpen: true);

            await using (writer.ConfigureAwait(false))
            {
                // Streamed from the snapshot's reader rather than through
                // snapshot.Text: the latter materialises the entire document as one
                // string first, which for a large file doubles peak memory for no
                // benefit at all.
                using var reader = snapshot.CreateReader();

                var buffer = new char[BufferSize];

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The user pressed Ctrl+S and expects the bytes to be on the device, not
            // in a cache that a power cut discards. This is an explicit, infrequent
            // action, so the disk round trip is affordable in a way it would not be
            // on the journal's path.
            stream.Flush(flushToDisk: true);
        }
    }
}
