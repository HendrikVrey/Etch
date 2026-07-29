using System.Windows;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Startup;
using Wpf.Ui.Appearance;

// Aliased because Application already exposes a MainWindow property; without this,
// `MainWindow` in an expression means the property and in a declaration means the
// type, which is exactly the kind of ambiguity that produces a baffling error.
using EtchWindow = Etch.App.Views.MainWindow;

namespace Etch.App;

/// <summary>The Etch application object.</summary>
public partial class App : Application
{
    private readonly LaunchMode.Edit _mode;

    private EtchWindow? _window;
    private int _failing;

    /// <summary>Creates the application for an interactive launch.</summary>
    internal App(LaunchMode.Edit mode)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimeline.Mark("app-startup");
        base.OnStartup(e);

        _window = new EtchWindow(_mode.FileToOpen);
        StartupTimeline.Mark("window-constructed");

        // Assigned before the theme is applied, deliberately: WPF-UI sets the DWM
        // immersive-dark-mode window attribute through Application.MainWindow and
        // silently skips it when that is still null — which leaves a light window
        // frame and light Mica around dark content.
        MainWindow = _window;

        ApplyTheme();

        _window.ContentRendered += OnContentRendered;
        _window.Show();
        StartupTimeline.Mark("window-shown");

        // Queued here rather than from ContentRendered because this is where
        // deferred *product* work happens — installing the theme watcher and
        // opening the file named on the command line. Hanging that off a
        // diagnostics event means a window that never raises ContentRendered
        // (started minimised, a remote session that renders nothing) silently
        // ignores the file it was asked to open.
        //
        // ApplicationIdle sits below Render and Loaded in the dispatcher's
        // priority order, so it still runs after the first frame.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, CompleteStartup);
    }

    /// <summary>Matches the app theme to Windows.</summary>
    /// <remarks>
    /// Applied unconditionally, even when it already matches the dictionary
    /// compiled into App.xaml. Skipping the call saves a resource re-merge but also
    /// skips the DWM window attribute that comes with it, and a correct window
    /// frame is worth more than the milliseconds. The cost shows up as its own
    /// phase in the timeline; if it proves expensive, M0's output is what justifies
    /// optimising it properly.
    /// </remarks>
    private static void ApplyTheme()
    {
        var theme = SystemThemeReader.Detect() == SystemThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        StartupTimeline.Mark("system-theme-read");

        ApplicationThemeManager.Apply(theme);
        StartupTimeline.Mark("theme-applied", "resource merge + DWM window attribute");
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ((Window)sender!).ContentRendered -= OnContentRendered;
        StartupTimeline.Mark("content-rendered");
    }

    /// <summary>
    /// Runs once the dispatcher has drained everything queued above idle priority,
    /// which is the honest definition of "the window is ready for the user".
    /// </summary>
    private void CompleteStartup()
    {
        StartupTimeline.Mark("interactive");

        var report = StartupTimeline.Complete();
        if (report is { } value)
        {
            DiagnosticLog.Write(value.Text);
        }

        _window?.OnStartupCompleted(report);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Marked handled so the failure can be logged and reported before the
        // process ends. The trade-off is that a recoverable rendering glitch is
        // also treated as fatal; the log is written first either way, so a
        // measurement in progress is never lost.
        e.Handled = true;
        Fail("dispatcher", e.Exception);
    }

    /// <summary>
    /// Last-resort logging. Best-effort only: by the time this runs the runtime has
    /// already committed to terminating, it never fires for stack overflow, and
    /// under genuine memory exhaustion the write itself may fail.
    /// </summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new InvalidOperationException($"Non-exception throw: {e.ExceptionObject}");

        DiagnosticLog.WriteFailure("appdomain", exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLog.WriteFailure("task", e.Exception);

        // Observing it stops the exception escalating, but it is still a bug: it is
        // in the log, and it must not be silently discarded.
        e.SetObserved();
    }

    private void Fail(string context, Exception exception)
    {
        // Logged before the re-entrancy guard, always. The guard exists to stop a
        // second dialog, not a second log entry — and the nested message loop the
        // first dialog pumps is precisely when follow-on exceptions arrive. Dropping
        // those would make every failure after the first one invisible.
        DiagnosticLog.WriteFailure(context, exception);

        // A modal pumps a nested message loop, which can raise a second dispatcher
        // exception and re-enter here — stacking dialogs and shutting down twice.
        if (Interlocked.Exchange(ref _failing, 1) == 1)
        {
            return;
        }

        // Posted rather than shown on the faulting stack: if the exception came out
        // of a measure or arrange pass, dispatcher processing is suspended and
        // MessageBox.Show would itself throw, inside the handler for the original
        // failure.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            MessageBox.Show(
                $"Etch hit an unrecoverable error and has to close.\n\n{exception.Message}\n\nDetails were written to:\n{DiagnosticLog.LogDirectory}",
                "Etch",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(2);
        });
    }
}
