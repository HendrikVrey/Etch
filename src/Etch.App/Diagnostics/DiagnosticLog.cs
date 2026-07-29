using System.Globalization;

namespace Etch.App.Diagnostics;

/// <summary>
/// Writes diagnostic output to the parent console and to a dated file.
/// </summary>
/// <remarks>
/// Both destinations matter. The console is what you read while iterating; the
/// file is what survives when Etch was launched from Explorer, from a shortcut, or
/// died before you could read the terminal.
/// <para>
/// Nothing here ever throws. This is instrumentation: a broken pipe or a full disk
/// must not be the reason the editor fails, and on the failure paths a second
/// exception would bury the first.
/// </para>
/// </remarks>
internal static class DiagnosticLog
{
    private const long MaxLogBytes = 4L * 1024 * 1024;

    private static readonly Lock Gate = new();

    /// <summary><c>%LOCALAPPDATA%\Etch\diag</c>.</summary>
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Etch",
        "diag");

    /// <summary>
    /// Writes <paramref name="text"/> to the console and appends it to today's log.
    /// </summary>
    /// <returns>The log file path, or null when the file could not be written.</returns>
    public static string? Write(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var attached = false;
        try
        {
            attached = ConsoleBridge.TryAttach();
            if (attached)
            {
                Console.Out.WriteLine();
                Console.Out.WriteLine(text);
            }
        }
        catch (IOException)
        {
            // A closed or redirected parent console. The file copy still matters.
        }

        // Usually just "launched from Explorer, no parent console", but if it is
        // something else the log is the only place that can say so.
        var body = attached || ConsoleBridge.LastError == 0
            ? text
            : $"{text}{Environment.NewLine}(no console attached; AttachConsole failed with Win32 error {ConsoleBridge.LastError})";

        return TryAppendToFile(body);
    }

    /// <summary>Records an unhandled exception.</summary>
    public static void WriteFailure(string context, Exception exception)
    {
        if (exception is null)
        {
            return;
        }

        Write($"UNHANDLED ({context})\n{exception}");
    }

    private static string? TryAppendToFile(string text)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            var path = Path.Combine(
                LogDirectory,
                string.Create(CultureInfo.InvariantCulture, $"etch-{DateTime.Now:yyyyMMdd}.log"));

            var entry = string.Create(
                CultureInfo.InvariantCulture,
                $"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} ==={Environment.NewLine}{text}{Environment.NewLine}");

            lock (Gate)
            {
                // A crash loop appends without limit otherwise, and WriteFailure
                // runs whether or not --diag was passed.
                RollIfOversized(path);
                File.AppendAllText(path, entry);
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Diagnostics are best-effort. A read-only or full disk must not stop
            // the editor from starting.
            return null;
        }
    }

    private static void RollIfOversized(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        // One generation back is enough: these logs are read minutes after they
        // are written, never archived.
        File.Move(path, path + ".1", overwrite: true);
    }
}
