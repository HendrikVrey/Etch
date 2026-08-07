using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Etch.App.Diagnostics;
using Etch.App.Startup;
using Etch.Persistence.Storage;

namespace Etch.App;

/// <summary>
/// Etch's entry point.
/// </summary>
/// <remarks>
/// Etch supplies its own <c>Main</c> rather than letting the WPF SDK generate one, so
/// that the startup clock starts before the <see cref="System.Windows.Application"/>
/// object and its merged XAML resource dictionaries exist. Those dictionaries are the
/// largest controllable cost in a WPF-UI startup, and a timeline that cannot see them
/// separately cannot tell you whether to cut them.
/// </remarks>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitUsageError = 1;
    private const int ExitRuntimeError = 2;
    private const int ExitAlreadyRunning = 3;

    /// <summary>How long to wait for the running instance to accept a handed-off file.</summary>
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Gap between attempts to take a lock a departing instance is about to release.</summary>
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long to wait for a lock held by an instance that answered "I am closing".
    /// </summary>
    /// <remarks>
    /// Sized to cover the closing instance's own budget — its final flush plus the
    /// journal's shutdown timeout — with slack. It is only ever spent when the holder has
    /// explicitly said it is on the way out, so waiting is the right thing to do.
    /// </remarks>
    private static readonly TimeSpan ClosingHolderWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to wait for a lock held by an instance that said nothing at all.
    /// </summary>
    /// <remarks>
    /// Short, because the likely explanation is a wedged process and the user is waiting
    /// on a file they double-clicked. Long enough to cover the gap between an orderly
    /// exit releasing its pipe and releasing its lock.
    /// </remarks>
    private static readonly TimeSpan SilentHolderWait = TimeSpan.FromSeconds(2);

    [STAThread]
    public static int Main(string[] args)
    {
        // Cheapest possible check, done before anything else so the timeline's origin
        // is as close to managed entry as it can be. Full parsing follows.
        StartupTimeline.Begin(Array.IndexOf(args, CommandLineParser.DiagnosticsFlag) >= 0);

        if (!CommandLineParser.TryParse(args, out var options, out var error))
        {
            ConsoleBridge.TryAttach();
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return ExitUsageError;
        }

        StartupTimeline.Mark("args-parsed");

        return options.Mode switch
        {
            LaunchMode.ShowHelp => ShowHelp(),
            LaunchMode.GenerateSample request => GenerateSample(request),
            LaunchMode.Edit edit => RunInteractive(edit),
            _ => throw new UnreachableException($"Unhandled launch mode: {options.Mode.GetType().Name}"),
        };
    }

    private static int ShowHelp()
    {
        ConsoleBridge.TryAttach();
        Console.Out.WriteLine(CommandLineOptions.Usage);
        return ExitSuccess;
    }

    private static int GenerateSample(LaunchMode.GenerateSample request)
    {
        ConsoleBridge.TryAttach();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var bytes = SampleGenerator.Generate(request);
            stopwatch.Stop();

            Console.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {bytes / (1024.0 * 1024.0):0.0} MB of {request.Shape.ToString().ToLowerInvariant()} to {request.Path} in {stopwatch.ElapsedMilliseconds} ms."));

            return ExitSuccess;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"Could not write '{request.Path}': {ex.Message}");
            return ExitRuntimeError;
        }
    }

    /// <summary>
    /// Starts the editor, or hands this launch's file to the instance already running.
    /// </summary>
    /// <remarks>
    /// The lock is taken before anything else touches the data directory. Two Etch
    /// processes journaling to one set of buffer files is silent, unrecoverable data
    /// loss with no error raised anywhere, and nothing further down the stack is in a
    /// position to notice it happening.
    /// </remarks>
    private static int RunInteractive(LaunchMode.Edit mode)
    {
        EtchPaths paths;

        try
        {
            paths = EtchPaths.CreateDefault();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Fail("Etch could not find a place to store your work.", ex);
        }

        InstanceLock? instanceLock;
        string channelName;

        try
        {
            if (!InstanceLock.TryAcquire(paths, out instanceLock, out channelName))
            {
                // Nested rather than folded into the condition above with `&&`: an `out`
                // from the right-hand side of a short-circuiting operator is not
                // definitely assigned on the path where the left side already decided the
                // answer, so reading it afterwards would not compile.
                if (!TryClaimFromDepartingInstance(paths, channelName, mode, out instanceLock, out var exitCode))
                {
                    return exitCode;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"Etch could not prepare its data folder at {paths.Root}.", ex);
        }

        StartupTimeline.Mark("instance-claimed");

        using (instanceLock)
        {
            var channel = InstanceChannel.Listen(channelName);

            try
            {
                var app = new App(mode, paths, channel);
                StartupTimeline.Mark("app-ctor");

                // Materialises App.xaml, which merges the WPF-UI themes and controls
                // dictionaries. Measured on its own because it is the first thing to cut
                // if the budget is ever missed.
                app.InitializeComponent();
                StartupTimeline.Mark("xaml-resources", "WPF-UI theme + controls dictionaries");

                return app.Run();
            }
            finally
            {
                // Blocking here is safe and necessary: the message loop has already
                // returned, so there is no dispatcher left to deadlock against, and the
                // accept loop has to stop before the lock file handle is released — or a
                // second instance could claim the directory while this one is still
                // listening on its channel.
                channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// Hands this launch's file to the running instance, or waits for a departing one to
    /// let go of the data directory.
    /// </summary>
    /// <param name="instanceLock">The lock, when this process ended up claiming it.</param>
    /// <param name="exitCode">What to exit with, when it did not.</param>
    /// <returns>True when this process should carry on and open a window.</returns>
    /// <remarks>
    /// Refusing to start and saying so would be honest and useless: the user
    /// double-clicked a file and expects to see it. Forwarding is what makes the lock
    /// invisible, and being invisible is the only way a correctness measure like this
    /// survives contact with daily use.
    /// <para>
    /// The three-way answer matters. An instance that took the file is done. One that
    /// replied "closing" is worth waiting properly for — quitting Etch and immediately
    /// relaunching it is an ordinary thing to do, and it holds the lock for its whole
    /// final flush. One that said nothing at all is probably wedged, and the user is
    /// waiting on a file, so it gets a much shorter grace period before being told.
    /// </para>
    /// </remarks>
    private static bool TryClaimFromDepartingInstance(
        EtchPaths paths,
        string channelName,
        LaunchMode.Edit mode,
        out InstanceLock? instanceLock,
        out int exitCode)
    {
        instanceLock = null;

        InstanceRequest request = mode.FileToOpen is { } path
            ? new InstanceRequest.Open(path)
            : new InstanceRequest.Activate();

        var answer = InstanceChannel
            .TrySendAsync(channelName, request, HandoffTimeout)
            .GetAwaiter()
            .GetResult();

        if (answer == HandoffResult.Accepted)
        {
            exitCode = ExitSuccess;
            return false;
        }

        // Nothing to open yet, but the holder may be on its way out. Quitting Etch and
        // immediately relaunching it is an ordinary thing to do; being told the editor is
        // wedged for being quick would be a poor way to reward it. A holder that answered
        // "closing" is worth waiting properly for; one that said nothing is probably
        // wedged, so it gets a much shorter grace period before the user is told.
        var deadline = DateTime.UtcNow + (answer == HandoffResult.Refused ? ClosingHolderWait : SilentHolderWait);

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(LockRetryDelay);

            if (InstanceLock.TryAcquire(paths, out instanceLock, out _))
            {
                exitCode = ExitSuccess;
                return true;
            }
        }

        // The lock is genuinely held and nothing is coming. Starting anyway is the exact
        // scenario the lock exists to prevent, so this reports and stops.
        ConsoleBridge.TryAttach();
        Console.Error.WriteLine("Etch is already running but is not responding.");

        _ = MessageBox.Show(
            "Etch is already running but is not responding, so this window cannot open - "
            + "two copies sharing one set of notes would overwrite each other.\n\n"
            + "Close the other window, or end the Etch process, and try again.\n\n"
            + $"Holder: {InstanceLock.DescribeHolder(paths)}",
            "Etch",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        exitCode = ExitAlreadyRunning;
        return false;
    }

    private static int Fail(string message, Exception exception)
    {
        DiagnosticLog.WriteFailure("startup", exception);

        ConsoleBridge.TryAttach();
        Console.Error.WriteLine($"{message} {exception.Message}");

        MessageBox.Show(
            $"{message}\n\n{exception.Message}",
            "Etch",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        return ExitRuntimeError;
    }
}
