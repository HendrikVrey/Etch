using System.Globalization;
using System.Text;

namespace Etch.App.Startup;

/// <summary>
/// Writes synthetic files for the performance gate.
/// </summary>
/// <remarks>
/// The M0 large-file test is only meaningful if everyone measures against the same
/// input. Generating the fixture from a fixed seed makes "open a 50 MB file" a
/// reproducible experiment rather than a story about whichever log happened to be
/// on the machine.
/// <para>
/// Output is ASCII only, so one character is one byte and the size target can be
/// hit exactly without re-encoding every line to count it.
/// </para>
/// </remarks>
internal static class SampleGenerator
{
    private const int Seed = 20260729;
    private const int FlushEvery = 4096;

    private static readonly string[] Services =
        ["checkout-api", "identity", "ledger", "notifier", "search-indexer", "gateway"];

    private static readonly string[] Levels = ["DEBUG", "INFO", "INFO", "INFO", "WARN", "ERROR"];

    private static readonly string[] Words =
    [
        "request", "completed", "retry", "cache", "miss", "hit", "queue", "depth",
        "connection", "pool", "exhausted", "timeout", "upstream", "latency", "budget",
        "shard", "rebalanced", "token", "refreshed", "payload", "truncated", "backoff",
    ];

    /// <summary>
    /// Writes the requested fixture.
    /// </summary>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="IOException">
    /// The destination already exists and <see cref="LaunchMode.GenerateSample.Force"/>
    /// was not set, there is not enough free space, or the write failed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The path is not writable.</exception>
    public static long Generate(LaunchMode.GenerateSample request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = (long)request.Mebibytes * 1024 * 1024;

        // "Never lose the user's text" is principle 2 of this project. A dev tool
        // that silently replaces `notes.md` with 50 MB of synthetic log would break
        // that promise before the editor ever gets a chance to keep it.
        if (!request.Force && File.Exists(request.Path))
        {
            throw new IOException($"'{request.Path}' already exists. Pass --force to replace it.");
        }

        var directory = Path.GetDirectoryName(request.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        EnsureFreeSpace(request.Path, target);

        // Write to a sibling temporary file and move it into place, so a failed or
        // cancelled run leaves nothing behind rather than a multi-gigabyte fragment.
        var temporaryPath = request.Path + ".etch-tmp";

        try
        {
            var written = WriteTo(temporaryPath, request.Shape, target, cancellationToken);
            File.Move(temporaryPath, request.Path, overwrite: request.Force);
            return written;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static long WriteTo(string path, SampleShape shape, long target, CancellationToken cancellationToken)
    {
        var random = new Random(Seed);
        var line = new StringBuilder(256);
        long written = 0;
        var sinceFlush = 0;

        // UTF8Encoding(false): no BOM. A BOM in a perf fixture would quietly change
        // what the encoding detector sees on load.
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1 << 16);

        while (written < target)
        {
            cancellationToken.ThrowIfCancellationRequested();

            line.Clear();
            if (shape == SampleShape.Json)
            {
                AppendJsonRecord(line, random);
            }
            else
            {
                AppendProseLine(line, random);
            }

            writer.Write(line);
            writer.Write('\n');

            written += line.Length + 1;

            if (++sinceFlush >= FlushEvery)
            {
                writer.Flush();
                sinceFlush = 0;
            }
        }

        writer.Flush();
        return written;
    }

    /// <summary>
    /// Fails before writing anything if the volume cannot hold the fixture.
    /// </summary>
    /// <remarks>
    /// Best-effort: network and mapped paths do not always report free space, and
    /// an unknown answer must not block a write that would have succeeded.
    /// </remarks>
    private static void EnsureFreeSpace(string path, long required)
    {
        long available;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (available < required)
        {
            throw new IOException(string.Create(
                CultureInfo.InvariantCulture,
                $"Not enough free space: {required / (1024.0 * 1024.0):0} MB needed, {available / (1024.0 * 1024.0):0} MB available."));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original failure is what matters; a stranded temp file is noise
            // by comparison and reporting it would bury the real error.
        }
    }

    private static void AppendJsonRecord(StringBuilder line, Random random)
    {
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddSeconds(random.Next(0, 60 * 60 * 24 * 180))
            .AddMilliseconds(random.Next(0, 1000));

        line.Append(CultureInfo.InvariantCulture, $"{{\"ts\":\"{timestamp:yyyy-MM-ddTHH:mm:ss.fffZ}\"");
        line.Append(CultureInfo.InvariantCulture, $",\"level\":\"{Levels[random.Next(Levels.Length)]}\"");
        line.Append(CultureInfo.InvariantCulture, $",\"svc\":\"{Services[random.Next(Services.Length)]}\"");

        line.Append(",\"trace\":\"");
        AppendHex(line, random, 32);
        line.Append('"');

        line.Append(",\"msg\":\"");
        var words = random.Next(4, 12);
        for (var i = 0; i < words; i++)
        {
            if (i > 0)
            {
                line.Append(' ');
            }

            line.Append(Words[random.Next(Words.Length)]);
        }

        line.Append('"');
        line.Append(CultureInfo.InvariantCulture, $",\"dur_ms\":{random.Next(1, 5000)}");
        line.Append(CultureInfo.InvariantCulture, $",\"status\":{(random.Next(10) == 0 ? 500 : 200)}");
        line.Append(CultureInfo.InvariantCulture, $",\"path\":\"/v1/orders/{random.Next(100000, 999999)}\"}}");
    }

    private static void AppendProseLine(StringBuilder line, Random random)
    {
        var words = random.Next(8, 20);
        for (var i = 0; i < words; i++)
        {
            if (i > 0)
            {
                line.Append(' ');
            }

            line.Append(Words[random.Next(Words.Length)]);
        }

        line.Append('.');
    }

    private static void AppendHex(StringBuilder line, Random random, int length)
    {
        const string HexDigits = "0123456789abcdef";
        for (var i = 0; i < length; i++)
        {
            line.Append(HexDigits[random.Next(HexDigits.Length)]);
        }
    }
}
