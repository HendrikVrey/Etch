using System.Windows;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Startup;
using Etch.App.Tabs;
using Etch.Persistence.Settings;
using Etch.Persistence.Storage;
using Wpf.Ui.Appearance;

// Aliased because Application already exposes a MainWindow property; without this,
// `MainWindow` in an expression means the property and in a declaration means the type,
// which is exactly the kind of ambiguity that produces a baffling error.
using EtchWindow = Etch.App.Views.MainWindow;

namespace Etch.App;

/// <summary>The Etch application object.</summary>
public partial class App : Application
{
    private readonly LaunchMode.Edit _mode;
    private readonly EtchPaths _paths;
    private readonly InstanceChannel _channel;
    private readonly SettingsStore _settings;

    private Workspace? _workspace;
    private EtchWindow? _window;
    private Exception? _fatal;
    private int _failing;

    /// <summary>Creates the application for an interactive launch.</summary>
    internal App(LaunchMode.Edit mode, EtchPaths paths, InstanceChannel channel)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _settings = new SettingsStore(_paths);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Windows is logging the user off or restarting.
    /// </summary>
    /// <remarks>
    /// The window's ordinary close path cancels and re-issues the close so the flush can
    /// be awaited. That does not survive here: after this event the framework closes
    /// windows with cancellation ignored and then shuts the dispatcher down, so a queued
    /// continuation never runs and up to one latency ceiling of typing is lost on every
    /// Windows Update restart. The flush is therefore synchronous and bounded.
    /// </remarks>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        _channel.StopAccepting();
        _window?.FlushForSessionEnd();
    }

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupTimeline.Mark("app-startup");
        base.OnStartup(e);

        // Empty, and it touches no disk. Restoring before the first frame would put a
        // file read in front of the thing the user is actually waiting for; the plan is
        // explicit that the order is render, then hydrate.
        _workspace = Workspace.Create(_paths);
        StartupTimeline.Mark("workspace-created");

        // Constructed, not read. The file is read in CompleteStartup alongside the
        // session, because the plan's order is render then hydrate and a settings read
        // in front of the first frame would be exactly the kind of small disk cost the
        // startup budget is spent avoiding.
        _window = new EtchWindow(_workspace, _settings, _mode.FileToOpen);
        _window.OnClosingStarted(_channel.StopAccepting);
        StartupTimeline.Mark("window-constructed");

        // Assigned before the theme is applied, deliberately: WPF-UI sets the DWM
        // immersive-dark-mode window attribute through Application.MainWindow and
        // silently skips it when that is still null, which leaves a light window frame
        // and light Mica around dark content.
        MainWindow = _window;

        ApplyTheme(_window);

        _window.ContentRendered += OnContentRendered;
        _window.Show();
        StartupTimeline.Mark("window-shown");

        // Queued here rather than from ContentRendered because this is where the
        // deferred *product* work happens: restoring the session, installing the theme
        // watcher, opening the file named on the command line. Hanging that off a
        // diagnostics event would mean a window that never raises ContentRendered
        // (started minimised, a remote session that renders nothing) silently loses the
        // user's tabs.
        //
        // ApplicationIdle sits below Render and Loaded in the dispatcher's priority
        // order, so it still runs after the first frame.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, CompleteStartup);
    }

    /// <summary>Matches the app theme to Windows.</summary>
    /// <remarks>
    /// Applied unconditionally, even when it already matches the dictionary compiled
    /// into App.xaml. Skipping the call saves a resource re-merge but also skips the DWM
    /// window attribute that comes with it, and a correct window frame is worth more
    /// than the milliseconds. The cost shows up as its own phase in the timeline.
    /// </remarks>
    private static void ApplyTheme(EtchWindow window)
    {
        var theme = SystemThemeReader.Detect() == SystemThemeMode.Light
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        StartupTimeline.Mark("system-theme-read");

        ApplicationThemeManager.Apply(theme);
        StartupTimeline.Mark("theme-applied", "resource merge + DWM window attribute");

        // AvalonEdit's colours do not come from the resource dictionaries, so they have to
        // be pushed rather than inherited. Explicitly, and not left to the library's own
        // Changed event: that event is raised only when the dictionary actually swapped, so
        // on a machine whose theme already matches the one compiled into App.xaml it never
        // fires at all, and the editor would keep AvalonEdit's defaults for the whole
        // session. Microseconds; it does not get its own timeline phase.
        window.RefreshEditorTheme();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ((Window)sender!).ContentRendered -= OnContentRendered;
        StartupTimeline.Mark("content-rendered");
    }

    /// <summary>
    /// Restores the session and reports the startup timeline, once the dispatcher has
    /// drained everything above idle priority.
    /// </summary>
    /// <remarks>
    /// The hand-off channel's handler is installed last, on purpose. Requests that
    /// arrived before then were buffered by the channel and are delivered the moment it
    /// is set, so a second launch a few hundred milliseconds behind the first is
    /// answered rather than told the running instance is not responding.
    /// </remarks>
    private async void CompleteStartup()
    {
        // Before the restore, and that ordering is load-bearing rather than tidy: the
        // restore evaluates every buffer against the workspace's size policy, and the
        // size thresholds are one of the things the settings file configures. Reading it
        // afterwards would mean the first session after a threshold change was still
        // judged by the old one.
        await LoadSettingsAsync().ConfigureAwait(true);

        if (_workspace is { } workspace)
        {
            try
            {
                await workspace.RestoreAsync().ConfigureAwait(true);
                StartupTimeline.Mark("session-restored");
            }
            catch (Exception ex)
            {
                // Never fatal. A restore that fails should cost the user their tab
                // layout, never their ability to start the editor and keep working.
                DiagnosticLog.WriteFailure("restore", ex);
            }
        }

        StartupTimeline.Mark("interactive");

        var report = StartupTimeline.Complete();

        if (report is { } value)
        {
            DiagnosticLog.Write(value.Text);
        }

        _window?.OnStartupCompleted(report);

        _channel.SetHandler(OnInstanceRequest);
    }

    /// <summary>
    /// Reads the settings file and hands the result to the window.
    /// </summary>
    /// <remarks>
    /// <see cref="SettingsStore.LoadAsync"/> is contracted never to throw, and the catch
    /// here is not a hedge against that being wrong so much as against this method
    /// growing a second statement later. Losing the settings costs the user their
    /// preferences for one session; it must never cost them the editor.
    /// </remarks>
    private async Task LoadSettingsAsync()
    {
        if (_window is not { } window)
        {
            return;
        }

        try
        {
            var loaded = await _settings.LoadAsync().ConfigureAwait(true);

            window.ApplyStartupSettings(loaded);
            StartupTimeline.Mark("settings-loaded");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("settings-load", ex);
        }
    }

    /// <summary>
    /// Handles a request from a second launch of Etch.
    /// </summary>
    /// <remarks>
    /// Arrives on a thread-pool thread, so everything it touches has to be marshalled.
    /// The path has already been validated by <see cref="InstanceRequest"/> against the
    /// same rules the command line uses.
    /// </remarks>
    private void OnInstanceRequest(InstanceRequest request) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            switch (request)
            {
                case InstanceRequest.Open open:
                    _window?.OpenFromAnotherInstance(open.Path);
                    break;

                case InstanceRequest.Activate:
                    _window?.ActivateFromAnotherInstance();
                    break;
            }
        });

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Marked handled so the failure can be logged and reported before the process
        // ends. The trade-off is that a recoverable rendering glitch is also treated as
        // fatal; the log is written first either way, so nothing is lost.
        e.Handled = true;
        Fail("dispatcher", e.Exception);
    }

    /// <summary>
    /// Last-resort logging. Best-effort only: by the time this runs the runtime has
    /// already committed to terminating, it never fires for stack overflow, and under
    /// genuine memory exhaustion the write itself may fail.
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

        // Observing it stops the exception escalating, but it is still a bug: it is in
        // the log, and it must not be silently discarded.
        e.SetObserved();
    }

    private void Fail(string context, Exception exception)
    {
        // Logged before the re-entrancy guard, always. The guard exists to stop a second
        // dialog, not a second log entry, and the nested message loop the first dialog
        // pumps is precisely when follow-on exceptions arrive. Dropping those would make
        // every failure after the first one invisible.
        DiagnosticLog.WriteFailure(context, exception);

        // A modal pumps a nested message loop, which can raise a second dispatcher
        // exception and re-enter here: stacking dialogs and shutting down twice.
        if (Interlocked.Exchange(ref _failing, 1) == 1)
        {
            return;
        }

        _fatal = exception;

        // Posted rather than shown on the faulting stack: if the exception came out of a
        // measure or arrange pass, dispatcher processing is suspended and
        // MessageBox.Show would itself throw, inside the handler for the original
        // failure. A method group rather than an inline async lambda, because the
        // dispatcher's overloads take a Delegate and an async lambda has no natural
        // delegate type to infer.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ReportFatal));
    }

    /// <summary>
    /// Flushes, tells the user, and shuts down.
    /// </summary>
    /// <remarks>
    /// The flush comes first. The user is about to be told the editor is closing, and
    /// the honest thing to do before saying so is get their text onto the disk: this is
    /// the one moment where the promise that nothing is ever lost is hardest to keep and
    /// most worth keeping.
    /// </remarks>
    private async void ReportFatal()
    {
        var exception = _fatal ?? new InvalidOperationException("Etch failed for an unrecorded reason.");
        var flushed = false;

        if (_workspace is { } workspace)
        {
            try
            {
                await workspace.ShutdownAsync().ConfigureAwait(true);
                flushed = true;
            }
            catch (Exception flushFailure)
            {
                DiagnosticLog.WriteFailure("shutdown-after-failure", flushFailure);
            }
        }

        _ = MessageBox.Show(
            $"Etch hit an unrecoverable error and has to close.\n\n{exception.Message}\n\n"
            + (flushed
                ? "Your open tabs were written to disk first."
                : "Etch could not finish writing your tabs to disk - the last few seconds of typing may be missing.")
            + $"\n\nDetails were written to:\n{DiagnosticLog.LogDirectory}",
            "Etch",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Shutdown(2);
    }
}
