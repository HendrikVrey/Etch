using System.Runtime.InteropServices;
using System.Text;

namespace Etch.App.Diagnostics;

/// <summary>
/// Lets a <c>WinExe</c> write to the console it was launched from.
/// </summary>
/// <remarks>
/// A WPF app has no console attached, so <c>Console.WriteLine</c> goes nowhere.
/// Attaching to the parent process's console makes <c>Etch --diag</c> behave like a
/// normal command-line tool when run from a terminal.
/// <para>
/// Deliberately no <c>AllocConsole</c> fallback: creating a console window would
/// add window creation and paint work to the very startup path being measured, so
/// the measurement would report a number the real app never pays. When there is no
/// parent console the log file is the fallback instead.
/// </para>
/// </remarks>
internal static partial class ConsoleBridge
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    /// <summary>
    /// Attach is idempotent and thread-safe because failure paths run from the
    /// thread pool and from arbitrary threads, not just the UI thread. Two threads
    /// racing here would each rebind the standard streams over the same handle.
    /// </summary>
    private static readonly Lazy<bool> Attachment = new(Attach, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The Win32 error from a failed attach, or 0. Recorded into the log file by
    /// <see cref="DiagnosticLog"/>, which is the only place it can be reported:
    /// there is, by definition, no console to print it to.
    /// </summary>
    public static int LastError { get; private set; }

    /// <summary>
    /// Attaches to the parent process's console and rebinds the standard streams.
    /// </summary>
    /// <returns>True when console output is available.</returns>
    public static bool TryAttach() => Attachment.Value;

    private static bool Attach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
            {
                LastError = Marshal.GetLastPInvokeError();
                return false;
            }

            // The attach gives us a console, but the standard streams are still
            // bound to the null device from process start. Both must be rebound,
            // every usage error in Program.cs writes to stderr, so rebinding only
            // stdout would make bad command lines fail silently.
            var encoding = WithoutPreamble(Console.OutputEncoding);

            Console.SetOut(CreateWriter(Console.OpenStandardOutput(), encoding));
            Console.SetError(CreateWriter(Console.OpenStandardError(), encoding));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // AutoFlush, so output is not lost if the process exits abruptly.
    private static StreamWriter CreateWriter(Stream stream, Encoding encoding) =>
        new(stream, encoding) { AutoFlush = true };

    /// <summary>
    /// Strips the byte-order mark from a console encoding.
    /// </summary>
    /// <remarks>
    /// A console stream is not seekable, so StreamWriter never learns that a
    /// preamble is unnecessary and emits one on the first flush. On a UTF-8 console
    /// (PowerShell 7, or any session that has run <c>chcp 65001</c>) that puts a
    /// literal <c>EF BB BF</c> in front of the first line of output.
    /// </remarks>
    private static Encoding WithoutPreamble(Encoding encoding) => encoding is UTF8Encoding
        ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        : encoding;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint dwProcessId);
}
