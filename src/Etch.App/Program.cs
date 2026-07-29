using System.Diagnostics;
using System.Globalization;
using Etch.App.Diagnostics;
using Etch.App.Startup;

namespace Etch.App;

/// <summary>
/// Etch's entry point.
/// </summary>
/// <remarks>
/// Etch supplies its own <c>Main</c> rather than letting the WPF SDK generate one,
/// so that the startup clock starts before the <see cref="System.Windows.Application"/>
/// object and its merged XAML resource dictionaries exist. Those dictionaries are
/// the largest controllable cost in a WPF-UI startup, and a timeline that cannot
/// see them separately cannot tell you whether to cut them.
/// </remarks>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitUsageError = 1;
    private const int ExitRuntimeError = 2;

    [STAThread]
    public static int Main(string[] args)
    {
        // Cheapest possible check, done before anything else so the timeline's
        // origin is as close to managed entry as it can be. Full parsing follows.
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

    private static int RunInteractive(LaunchMode.Edit mode)
    {
        var app = new App(mode);
        StartupTimeline.Mark("app-ctor");

        // Materialises App.xaml, which merges the WPF-UI themes and controls
        // dictionaries. Measured on its own because it is the first thing to cut
        // if the budget is missed.
        app.InitializeComponent();
        StartupTimeline.Mark("xaml-resources", "WPF-UI theme + controls dictionaries");

        return app.Run();
    }
}
