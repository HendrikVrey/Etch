using System.Buffers;
using System.Diagnostics;
using System.Text;
using Etch.Core.Documents;
using Etch.Core.Text;

namespace Etch.App.Editor;

/// <summary>Reads files from disk, off the UI thread.</summary>
internal static class DocumentLoader
{
    private const int BufferSize = 1 << 16;

    /// <summary>
    /// Upper bound on the buffer pre-allocated for a read. Beyond this the builder
    /// grows in chunks instead: slower, but it cannot be turned into an
    /// out-of-memory failure by an over-generous size policy.
    /// </summary>
    private const long MaxPreallocatedChars = 128L * 1024 * 1024;

    /// <summary>
    /// The encoding assumed for a file with no byte-order mark.
    /// </summary>
    /// <remarks>
    /// A private instance rather than <see cref="Encoding.UTF8"/>, and the difference is
    /// not cosmetic: the singleton carries a three-byte preamble, so a file that never
    /// had a mark would acquire one the first time it was saved.
    /// </remarks>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Reads <paramref name="path"/>, refusing anything past the policy's ceiling.
    /// </summary>
    /// <param name="path">An absolute path that has already been validated.</param>
    /// <param name="policy">The size policy to enforce.</param>
    /// <param name="cancellationToken">Cancels an in-flight read.</param>
    /// <remarks>
    /// The size decision is made here, from the length of the handle actually being
    /// read, and nowhere else. A <c>FileInfo</c> check before the open would be a
    /// time-of-check/time-of-use gap: between the two the file can grow, be
    /// replaced, or have a junction retargeted, and the most likely input for this
    /// editor is a log file that something else still holds open for writing.
    /// </remarks>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    public static async Task<DocumentLoadResult> LoadAsync(
        string path,
        DocumentSizePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(policy);

        var stopwatch = Stopwatch.StartNew();

        // FileShare.ReadWrite: log files are frequently open for writing elsewhere,
        // and refusing to read them would be a needless failure.
        // SequentialScan tells the cache manager how these pages will be used.
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);

        var sizeInBytes = stream.Length;
        var capabilities = policy.Evaluate(sizeInBytes);

        if (!capabilities.CanOpen)
        {
            return new DocumentLoadResult.Refused(
                capabilities.Notice ?? $"{DocumentSizePolicy.Describe(sizeInBytes)} is too large to open.");
        }

        // Taken from this handle, for the same reason the size is: anything read from
        // the path instead could describe a different file by the time it is used. The
        // identity de-duplicates tabs and the witness lets Ctrl+S notice a change made
        // by something else, so both have to mean *this* file, not that path.
        var identity = FileIdentity.FromHandle(stream.SafeFileHandle, path);
        var witness = FileWitness.FromHandle(stream.SafeFileHandle);

        // The fallback is a private byte-order-mark-free instance, not Encoding.UTF8.
        // StreamReader only replaces its encoding when it actually detects a mark, and
        // for UTF-8 it replaces it with the Encoding.UTF8 singleton, whose GetPreamble()
        // is three bytes long. Passing that singleton in as the fallback makes the two
        // cases indistinguishable afterwards, and the write-through path would then
        // prepend EF BB BF to every ordinary UTF-8 file it saves. With this instance,
        // CurrentEncoding is Encoding.UTF8 if and only if a mark was really there.
        using var reader = new StreamReader(
            stream,
            Utf8NoBom,
            detectEncodingFromByteOrderMarks: true,
            BufferSize);

        // A UTF-8 or UTF-16 decode never yields more chars than there are bytes, so
        // capping chars at the byte ceiling bounds memory even if the file grows
        // while it is being read.
        var (text, wasTruncated) = await ReadCappedAsync(
            reader,
            capacityHint: sizeInBytes,
            charLimit: policy.HardCeiling,
            cancellationToken).ConfigureAwait(false);

        // CurrentEncoding only reflects a detected byte-order mark once a read has
        // happened, so it must be captured after the read, not before.
        var encoding = reader.CurrentEncoding;

        stopwatch.Stop();

        return new DocumentLoadResult.Loaded(new LoadedDocument(
            Path: path,
            Text: text,
            Encoding: encoding,
            LineEnding: LineEndings.Detect(text),
            SizeInBytes: sizeInBytes,
            Capabilities: capabilities,
            WasTruncated: wasTruncated,
            ReadDuration: stopwatch.Elapsed,
            Identity: identity,
            Witness: witness));
    }

    /// <summary>
    /// Reads to the end of the stream, stopping at <paramref name="charLimit"/>.
    /// </summary>
    /// <remarks>
    /// <c>ReadToEndAsync</c> would be shorter but has no upper bound: it keeps
    /// allocating for as long as the source keeps producing, which for a file
    /// another process is actively appending to is unbounded.
    /// </remarks>
    private static async Task<(string Text, bool WasTruncated)> ReadCappedAsync(
        StreamReader reader,
        long capacityHint,
        long charLimit,
        CancellationToken cancellationToken)
    {
        // Clamped, not just cast. DocumentSizePolicy is public and its ceiling is
        // unbounded, so a caller-supplied ceiling above int.MaxValue would wrap the
        // cast negative; one merely above a gigabyte would ask for a multi-gigabyte
        // char[]. Pre-sizing is an optimisation, and an optimisation must never be
        // the thing that throws.
        var capacity = (int)Math.Clamp(
            Math.Max(capacityHint, BufferSize),
            BufferSize,
            Math.Min(charLimit, MaxPreallocatedChars));

        var builder = new StringBuilder(capacity);
        var buffer = ArrayPool<char>.Shared.Rent(BufferSize);

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    return (builder.ToString(), false);
                }

                var remaining = charLimit - builder.Length;
                if (read > remaining)
                {
                    builder.Append(buffer, 0, (int)remaining);
                    return (builder.ToString(), true);
                }

                builder.Append(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    /// <summary>A short label for the status bar ("UTF-8", "UTF-8 BOM", "UTF-16 LE").</summary>
    public static string DescribeEncoding(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        var hasPreamble = encoding.GetPreamble().Length > 0;

        return encoding.CodePage switch
        {
            65001 => hasPreamble ? "UTF-8 BOM" : "UTF-8",
            1200 => "UTF-16 LE",
            1201 => "UTF-16 BE",
            12000 => "UTF-32 LE",
            12001 => "UTF-32 BE",
            _ => encoding.WebName.ToUpperInvariant(),
        };
    }
}
