using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Tabs;
using Etch.Core.Documents;
using Etch.Core.Text;
using Etch.Persistence.Model;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

// The XAML-generated field for the editor is called Editor, which shadows the
// Etch.App.Editor namespace inside this type. Aliasing the one type needed from it is
// less surprising than renaming a control that is correctly named.
using EditorTheme = Etch.App.Editor.EditorTheme;
using Loader = Etch.App.Editor.DocumentLoader;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Etch.App.Views;

/// <summary>
/// The Etch shell: tab strip, editor, find bar, status bar.
/// </summary>
/// <remarks>
/// <para>
/// One editor control, not one per tab. Switching tabs swaps
/// <see cref="ICSharpCode.AvalonEdit.TextEditor.Document"/>, which is cheap and keeps
/// memory flat as tabs accumulate — twenty AvalonEdit instances, each with its own
/// text view, margins and render layers, would not fit inside the plan's idle
/// footprint. The price is that caret and scroll position belong to the control rather
/// than to the tab, so they are read back into the tab on the way out and restored on
/// the way in.
/// </para>
/// <para>
/// Deliberately not a full MVVM stack. The window binds to the workspace's own
/// collection and exposes commands as properties; a view-model layer that only
/// forwarded would be ceremony, and the logic worth testing lives in
/// <c>Etch.Core</c> and <c>Etch.Persistence</c>, where it is tested without a window
/// at all.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification =
        "A WPF Window has no disposal story to implement — nothing calls Dispose on one, " +
        "and making it IDisposable would advertise a contract the framework never honours. " +
        "OnClosed is where a Window's deterministic cleanup belongs, and that is where " +
        "_editorTheme is disposed, alongside the timers and the event unsubscriptions. " +
        "The analyser cannot see OnClosed as a disposal path, which is the whole of the " +
        "disagreement.")]
public partial class MainWindow : FluentWindow
{
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StartupResultDuration = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long the final flush may take before the window closes anyway.
    /// </summary>
    /// <remarks>
    /// Generous enough for a slow disk or an antivirus scanner holding a handle, short
    /// enough that a genuinely wedged volume does not produce an editor that will not
    /// quit. Anything unwritten at that point is at most one debounce window old.
    /// </remarks>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long the flush may take when Windows is ending the session.
    /// </summary>
    /// <remarks>
    /// Shorter than the ordinary shutdown budget, because this one blocks the whole
    /// logoff and Windows will kill the process for taking too long anyway.
    /// </remarks>
    private static readonly TimeSpan SessionEndTimeout = TimeSpan.FromSeconds(3);

    private readonly Workspace _workspace;
    private readonly string? _fileToOpen;
    private readonly EditorTheme _editorTheme;

    private DispatcherTimer? _messageTimer;
    private BufferTab? _bound;
    private string? _pendingStartupSummary;
    private bool _suppressEditorEvents;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private Action? _closing;

    internal MainWindow(Workspace workspace, string? fileToOpen)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _fileToOpen = fileToOpen;

        NewTabCommand = new RelayCommand(() => _ = _workspace.NewScratch());
        CloseActiveTabCommand = new RelayCommand(
            () => Run(CloseAsync(_workspace.Active)),
            () => _workspace.Active is not null);
        CloseTabCommand = new RelayCommand<BufferTab>(tab => Run(CloseAsync(tab)));
        ActivateTabCommand = new RelayCommand<BufferTab>(tab => Run(ActivateAsync(tab)));
        // Deliberately ungated. Guarding this on CanReopenClosed made Ctrl+Shift+T do
        // nothing at all with an empty stack, which reads as a broken shortcut — and it
        // made the workspace's own "there is nothing left to reopen" message unreachable.
        // A key that explains itself beats a key that is silently disabled.
        ReopenClosedCommand = new RelayCommand(() => Run(_workspace.ReopenLastClosedAsync()));
        NextTabCommand = new RelayCommand(() => Step(1));
        PreviousTabCommand = new RelayCommand(() => Step(-1));
        RenameTabCommand = new RelayCommand(BeginRename, () => _workspace.Active is not null);
        ToggleEphemeralCommand = new RelayCommand(ToggleEphemeral, () => _workspace.Active is not null);
        TogglePinnedCommand = new RelayCommand(TogglePinned, () => _workspace.Active is not null);

        // Before InitializeComponent, like every other command: the XAML binds to these
        // properties, and a binding evaluated against a null command is a control that
        // silently does nothing. The keyboard no longer depends on this — chords resolve
        // through Etch.App.Input.KeyMap when they are pressed — but the tab strip and the
        // palette's row list still do.
        InitialisePalette();

        InitializeComponent();

        // After InitializeComponent, because it needs the editor to exist, and before the
        // window is shown, because AvalonEdit's own selection colours are what it replaces.
        // The application refreshes it again once the theme has been applied — see Refresh.
        _editorTheme = new EditorTheme(Editor);

        _workspace.Notice += OnWorkspaceNotice;
        _workspace.ActiveChanged += OnActiveChanged;

        // Selection changes move the caret, so Caret.PositionChanged alone covers both.
        // Subscribing to SelectionChanged as well would do the same work twice per
        // mouse-move during a drag-select.
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.TextChanged += OnEditorTextChanged;

        // Nothing is restored yet, so there is nothing to type into. Bind enables it
        // again the moment the first tab arrives; without this the window would briefly
        // accept keystrokes into a document bound to no buffer.
        Editor.IsEnabled = false;

        Bind(_workspace.Active);
    }

    // There is deliberately no parameterless constructor. One is not required — App.xaml
    // has no StartupUri and BAML calls InitializeComponent from the constructor above —
    // and adding one back would mean a window with no workspace, which is a window that
    // silently discards everything typed into it. The only casualty is the Visual Studio
    // XAML design surface.

    /// <summary>The open tabs, for the tab strip to bind to.</summary>
    public ReadOnlyObservableCollection<BufferTab> Tabs => _workspace.Tabs;

    /// <summary>Creates a new scratch tab.</summary>
    public ICommand NewTabCommand { get; }

    /// <summary>Closes the tab in front.</summary>
    public ICommand CloseActiveTabCommand { get; }

    /// <summary>Closes a specific tab, from its close button.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>Brings a specific tab to the front.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Reopens the most recently closed tab.</summary>
    public ICommand ReopenClosedCommand { get; }

    /// <summary>Moves to the next tab.</summary>
    public ICommand NextTabCommand { get; }

    /// <summary>Moves to the previous tab.</summary>
    public ICommand PreviousTabCommand { get; }

    /// <summary>Renames the tab in front, in place.</summary>
    public ICommand RenameTabCommand { get; }

    /// <summary>Marks the tab in front as never written to disk, or lifts the mark.</summary>
    public ICommand ToggleEphemeralCommand { get; }

    /// <summary>Pins the tab in front to the left of the strip, or unpins it.</summary>
    public ICommand TogglePinnedCommand { get; }

    /// <summary>
    /// Called once the window is genuinely interactive.
    /// </summary>
    /// <remarks>
    /// Everything deferred to this point is deferred deliberately. Opening a file here
    /// rather than during construction keeps two measurements separate: time to
    /// interactive, and time to open a file.
    /// </remarks>
    internal void OnStartupCompleted(StartupReport? report)
    {
        // Hooks WM_SETTINGCHANGE — event-driven, no polling timer, so idle CPU stays
        // at zero.
        SystemThemeWatcher.Watch(this);

        if (report is { } value)
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Startup {value.Total.TotalMilliseconds:0} ms — {(value.WithinBudget ? "within" : "OVER")} the {StartupTimeline.Budget.TotalMilliseconds:0} ms budget");

            ShowMessage(summary, StartupResultDuration);

            // A file named on the command line is opened next, and its message would
            // overwrite this one inside the same dispatcher turn — so `Etch --diag
            // <file>`, the documented large-file procedure, would never show its
            // startup number. Carry it forward into the next message instead.
            _pendingStartupSummary = _fileToOpen is null ? null : summary;
        }

        if (_fileToOpen is { } path)
        {
            Run(OpenAsync(path));
        }
    }

    /// <summary>Opens a file at the request of a second launch of Etch.</summary>
    internal void OpenFromAnotherInstance(string path)
    {
        ActivateFromAnotherInstance();
        Run(OpenAsync(path));
    }

    /// <summary>
    /// Re-derives the editor's selection and current-line colours.
    /// </summary>
    /// <remarks>
    /// Called by the application immediately after it applies the WPF-UI theme. The window
    /// is constructed before that happens — deliberately, so <c>Application.MainWindow</c>
    /// is assigned in time for the DWM dark-mode attribute — which means the colours picked
    /// during construction were picked against a theme that had not been applied yet.
    /// </remarks>
    internal void RefreshEditorTheme() => _editorTheme.Refresh();

    /// <summary>Registers what to run the moment the window starts closing.</summary>
    /// <remarks>
    /// Used to stop the hand-off channel accepting requests it can no longer honour. A
    /// callback rather than an event because there is exactly one subscriber and it is
    /// set once, at startup.
    /// </remarks>
    internal void OnClosingStarted(Action callback) => _closing = callback;

    /// <summary>Brings the window forward at the request of a second launch of Etch.</summary>
    internal void ActivateFromAnotherInstance()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
    }

    /// <summary>
    /// Writes everything to disk before the window is allowed to close.
    /// </summary>
    /// <remarks>
    /// The close is cancelled and re-issued rather than blocked, because the flush is
    /// asynchronous and blocking the dispatcher to wait for it would deadlock on its own
    /// continuations. This is the last of the plan's immediate-flush moments and the one
    /// that matters most: it is where the largest amount of unwritten text exists and
    /// where there is no second chance to get it right.
    /// <para>
    /// A hung disk cannot hold the window open indefinitely, so the flush runs against a
    /// deadline and the window closes either way. Losing at most the debounce window is
    /// better than an editor that will not quit.
    /// </para>
    /// </remarks>
    protected override void OnClosing(CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        // A second X-click during the flush would otherwise start a second shutdown
        // pipeline against a journal that is already disposing.
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;

        // Stop taking hand-offs the moment shutdown begins. Otherwise a second launch in
        // this window is told its file was accepted, and the file is opened into a window
        // that is closing — so the user double-clicks something and nothing happens, with
        // no error anywhere.
        _closing?.Invoke();

        // The editor goes read-only rather than staying live: anything typed from here on
        // is past the final flush and would be silently discarded.
        Editor.IsReadOnly = true;

        _ = ShutdownThenCloseAsync();
    }

    /// <summary>
    /// Flushes synchronously because Windows is ending the session.
    /// </summary>
    /// <remarks>
    /// The cancel-then-reclose dance above cannot help here: <c>Application.Shutdown</c>
    /// closes windows with cancellation ignored and then shuts the dispatcher down, so a
    /// queued continuation never runs and up to one latency ceiling of typing is lost on
    /// every Windows Update restart. Blocking is legitimate at this point — the message
    /// loop is about to die — and it is bounded so a wedged disk cannot stall a logoff.
    /// </remarks>
    internal void FlushForSessionEnd()
    {
        if (_shutdownComplete)
        {
            return;
        }

        _shutdownStarted = true;
        Editor.IsReadOnly = true;

        try
        {
            // Split deliberately. The first half needs the UI thread — a document snapshot
            // has thread affinity — and the second half must not touch it at all, because
            // this thread is about to block waiting for it. Handing the second half to the
            // thread pool, where every await is ConfigureAwait(false), is what makes that
            // block safe rather than a deadlock that hangs the user's logoff and loses the
            // text it was trying to save.
            CaptureViewState();
            var plan = _workspace.PrepareShutdown();

            // Deliberately not disposed. If the wait below times out the task is still
            // running and still holding this token, and disposing it underneath would
            // replace a slow write with an exception on a task nobody is watching. The
            // process is ending; one handle is not worth the risk.
            var deadline = new CancellationTokenSource(SessionEndTimeout);
            var work = Task.Run(() => _workspace.CompleteShutdownAsync(plan, deadline.Token), CancellationToken.None);

            // A hard stop as well as a cooperative one: Windows kills a process that
            // stalls a logoff, and a stalled logoff is a worse outcome than a few
            // unwritten seconds.
            if (!work.Wait(SessionEndTimeout))
            {
                DiagnosticLog.Write("Etch — the session-end flush timed out; some recent edits may not have been written.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("session-end", ex);
        }
    }

    private async Task ShutdownThenCloseAsync()
    {
        try
        {
            CaptureViewState();

            using var deadline = new CancellationTokenSource(ShutdownTimeout);
            await _workspace.ShutdownAsync(deadline.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("shutdown", ex);
        }

        try
        {
            await _workspace.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("shutdown-dispose", ex);
        }

        _shutdownComplete = true;
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _workspace.Notice -= OnWorkspaceNotice;
        _workspace.ActiveChanged -= OnActiveChanged;

        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        Editor.TextChanged -= OnEditorTextChanged;

        // Deliberately not SystemThemeWatcher.UnWatch: it throws InvalidOperationException
        // for a window that is no longer loaded, which is exactly what this one is by the
        // time OnClosed runs. The hook goes away with the window handle regardless, and
        // Etch has one window for the life of the process.
        //
        // The editor theme is a different case and does need disposing: it holds a handler
        // on a static event, which would keep this window alive for as long as the process
        // is, whatever the window handle does.
        _editorTheme.Dispose();

        _messageTimer?.Stop();
        _messageTimer = null;

        StopCounting();
        _countTimer = null;

        _detection.Changed -= OnDetectionChanged;
        _detection.Stop();

        base.OnClosed(e);
    }

    private void ActivateByIndex(int index)
    {
        if (index >= 0 && index < _workspace.Tabs.Count)
        {
            Run(ActivateAsync(_workspace.Tabs[index]));
        }
    }

    private void Step(int delta)
    {
        var count = _workspace.Tabs.Count;

        if (count == 0 || _workspace.Active is not { } active)
        {
            return;
        }

        var current = _workspace.Tabs.IndexOf(active);

        if (current < 0)
        {
            return;
        }

        // Modulo then correction: C# gives a negative remainder for a negative
        // dividend, so Ctrl+Shift+Tab on the first tab would index out of range.
        var next = (((current + delta) % count) + count) % count;

        Run(ActivateAsync(_workspace.Tabs[next]));
    }

    private Task ActivateAsync(BufferTab tab) => _workspace.ActivateAsync(tab);

    private Task CloseAsync(BufferTab? tab) =>
        tab is null ? Task.CompletedTask : _workspace.CloseAsync(tab);

    // Task<BufferTab?> rather than Task: the workspace already returns the tab, and
    // widening it to the base type only added a cast for every caller to pay for.
    private Task<BufferTab?> OpenAsync(string path) => _workspace.OpenFileAsync(path);

    private void OnActiveChanged(BufferTab? tab) => Bind(tab);

    private void OnWorkspaceNotice(string message) => ShowMessage(message, null);

    /// <summary>
    /// Points the editor at a tab's document.
    /// </summary>
    /// <remarks>
    /// The event suppression is not defensive padding. Assigning
    /// <c>Editor.Document</c> raises <c>TextChanged</c>, and moves the caret to offset
    /// zero of the new document before the restore below runs — so without it, every
    /// tab switch would write a bogus caret position into the tab being switched
    /// <em>to</em>, and would enqueue a journal write for a tab nobody has edited.
    /// </remarks>
    private void Bind(BufferTab? tab)
    {
        if (ReferenceEquals(_bound, tab))
        {
            return;
        }

        // Every route that changes the active tab converges here — the tab strip,
        // Ctrl+Tab, Ctrl+N, reopening a closed tab, opening a file — so this is the one
        // place the outgoing tab's caret and scroll can be captured without every caller
        // having to remember to.
        CaptureViewState();

        _bound = tab;
        _suppressEditorEvents = true;

        try
        {
            if (tab?.Document is not { } document)
            {
                Editor.Document = new TextDocument();
                Editor.IsEnabled = false;
                Title = "Etch";
                return;
            }

            // Fresh binding, so nothing typed into the previous tab is carried over as a
            // read-only state from a shutdown that was started and then abandoned.
            Editor.IsReadOnly = _shutdownStarted;

            Editor.IsEnabled = true;
            Editor.Document = document;
            Editor.Encoding = tab.Encoding;

            // M2 owns highlighting. Being explicit documents where the capability check
            // belongs once there is one.
            Editor.SyntaxHighlighting = null;

            Editor.CaretOffset = Math.Clamp(tab.CaretOffset, 0, document.TextLength);
            Editor.ScrollToLine(Math.Clamp(tab.FirstVisibleLine, 1, document.LineCount));

            Title = $"{tab.Title} — Etch";
        }
        finally
        {
            _suppressEditorEvents = false;

            // In the finally, so the null-document branch above refreshes them too —
            // otherwise the status bar and the match count would both keep describing the
            // previous tab beside an empty, disabled editor.
            UpdateStatus();

            // Only when the bar is open. Otherwise every tab switch would compile the
            // leftover pattern and scan the whole new document for a count nobody can see.
            if (FindBar.Visibility == Visibility.Visible)
            {
                RefreshFindMatches();
            }
        }
    }

    /// <summary>
    /// Copies caret and scroll position out of the editor and into the tab.
    /// </summary>
    /// <remarks>
    /// Called before anything that can change which document the editor holds. The
    /// editor owns these two values while a tab is on screen and the tab owns them the
    /// rest of the time; this is the handover.
    /// </remarks>
    private void CaptureViewState()
    {
        if (_bound is not { Document: not null } tab)
        {
            return;
        }

        tab.CaretOffset = Editor.CaretOffset;

        var textView = Editor.TextArea.TextView;

        // Null before the first layout pass, and for a document scrolled past its own
        // end. Leaving the previous value alone beats storing a guess.
        if (textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y) is { } line)
        {
            tab.FirstVisibleLine = line.LineNumber;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        if (_bound is { } tab)
        {
            tab.CaretOffset = Editor.CaretOffset;
        }

        UpdateCaretStatus();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents)
        {
            return;
        }

        UpdateCounts();
        UpdateSaveStatus();

        // Debounced and off-thread. This is the only line on the keystroke path that
        // detection costs.
        InvalidateDetection(_bound?.Document);

        if (FindBar.Visibility == Visibility.Visible)
        {
            RefreshFindMatches();
        }
    }

    /// <summary>Handles <c>Ctrl+O</c>.</summary>
    private void OpenFileFromDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open",
            Multiselect = false,
            CheckFileExists = true,
            Filter = "All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            Run(OpenAsync(dialog.FileName));
        }
    }

    /// <summary>
    /// Handles <c>Ctrl+S</c>.
    /// </summary>
    /// <remarks>
    /// A scratch tab is already saved — continuously, to Etch's own storage — so
    /// <c>Ctrl+S</c> on one cannot mean "save it". It means "give this a home of my
    /// choosing", which is the only reading under which showing a file dialog is not a
    /// broken promise.
    /// </remarks>
    private void SaveActiveTab()
    {
        if (_workspace.Active is not { } tab)
        {
            return;
        }

        if (tab.Kind == BufferKind.File)
        {
            Run(_workspace.SaveThroughAsync(tab));
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save as",
            FileName = tab.Title,
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".txt",
        };

        if (dialog.ShowDialog(this) == true)
        {
            Run(_workspace.SaveAsAsync(tab, dialog.FileName));
        }
    }

    /// <summary>
    /// Puts the active tab's caption into edit mode.
    /// </summary>
    /// <remarks>
    /// In place, in the tab strip, rather than in a dialog. The plan bans modals, and a
    /// rename prompt is exactly the kind of small interruption that accumulates into an
    /// application that feels heavy.
    /// </remarks>
    private void BeginRename()
    {
        if (_workspace.Active is { } tab)
        {
            tab.IsRenaming = true;
        }
    }

    private void ToggleEphemeral()
    {
        if (_workspace.Active is { } tab)
        {
            _workspace.SetEphemeral(tab, !tab.IsEphemeral);
            UpdateSaveStatus();
        }
    }

    /// <summary>
    /// Pins the active tab, or unpins it.
    /// </summary>
    /// <remarks>
    /// Pinning moves the tab into the pinned group at the left of the strip, so the tab
    /// under the pointer is about to be somewhere else. The message names what happened
    /// because the tab jumping to a different position is otherwise the only feedback, and
    /// on a strip with one tab there is no visible movement at all.
    /// </remarks>
    private void TogglePinned()
    {
        if (_workspace.Active is not { } tab)
        {
            return;
        }

        var pinned = !tab.IsPinned;

        _workspace.SetPinned(tab, pinned);
        ShowMessage($"{tab.Title} {(pinned ? "pinned" : "unpinned")}.", null);
    }

    private void UpdateStatus()
    {
        UpdateCaretStatus();
        UpdateCounts();
        UpdateSaveStatus();

        // The format chip belongs to detection now, not to the size policy. Detection
        // still runs on a large document — it is sampled rather than skipped — so there
        // is no tier here that wants a different answer.
        //
        // Above the early return, deliberately: closing the last tab binds null, and if
        // this were below it the chip would keep naming the format of a buffer that is no
        // longer open. Passing null is what resets it.
        InvalidateDetection(_bound?.Document);

        if (_bound is not { } tab)
        {
            return;
        }

        SetText(LineEndingStatus, LineEndings.ToDisplayName(tab.LineEnding));
        SetText(EncodingStatus, Loader.DescribeEncoding(tab.Encoding));
    }

    private void UpdateCaretStatus()
    {
        var caret = Editor.TextArea.Caret;
        var selectionLength = Editor.SelectionLength;

        SetText(
            CaretStatus,
            selectionLength > 0
                ? string.Create(CultureInfo.CurrentCulture, $"Ln {caret.Line}, Col {caret.Column}  ({selectionLength:N0} selected)")
                : string.Create(CultureInfo.CurrentCulture, $"Ln {caret.Line}, Col {caret.Column}"));
    }

    /// <summary>
    /// Refreshes the character and line counts.
    /// </summary>
    /// <remarks>
    /// Both are maintained by the document in O(1), which is why this can run on every
    /// keystroke. A word count cannot — it needs a full scan of an off-thread snapshot.
    /// </remarks>
    private void UpdateCounts()
    {
        var document = Editor.Document;
        var lines = document.LineCount;

        SetText(
            CountsStatus,
            string.Create(
                CultureInfo.CurrentCulture,
                $"{document.TextLength:N0} chars, {lines:N0} {(lines == 1 ? "line" : "lines")}"));
    }

    /// <summary>
    /// Says plainly whether this tab's text is being written to disk.
    /// </summary>
    /// <remarks>
    /// The whole product rests on the user never thinking about saving, which only
    /// works if the exceptions are visible without being asked for. There are three: a
    /// tab the user marked ephemeral, a document too large to journal, and one that was
    /// truncated on the way in.
    /// </remarks>
    private void UpdateSaveStatus()
    {
        if (_bound is not { } tab)
        {
            SetText(SaveStatus, string.Empty);
            return;
        }

        SetText(SaveStatus, tab switch
        {
            { IsEphemeral: true } => "Ephemeral — not saved",
            { WasTruncated: true } => "Partly loaded — not saved",
            { Capabilities.Journaling: false } => "Large file — not auto-saved",
            _ => _workspace.HasUnsavedWork ? "Saving…" : "Saved",
        });
    }

    /// <summary>
    /// Assigns status text only when it actually changed.
    /// </summary>
    /// <remarks>
    /// These run on every keystroke, and the status items live in a horizontal
    /// StackPanel where any width change re-arranges every sibling. Skipping the no-op
    /// assignment keeps typing off the layout path entirely.
    /// </remarks>
    private static void SetText(TextBlock target, string value)
    {
        if (!string.Equals(target.Text, value, StringComparison.Ordinal))
        {
            target.Text = value;
        }
    }

    /// <summary>Shows a message in the status bar that clears itself.</summary>
    private void ShowMessage(string message, TimeSpan? duration)
    {
        if (_pendingStartupSummary is { } startup)
        {
            _pendingStartupSummary = null;
            message = $"{message}  ·  {startup}";
        }

        TransientMessage.Text = message;

        // Created on first use: a timer nobody has needed yet should not be on the
        // startup path.
        _messageTimer ??= CreateMessageTimer();

        _messageTimer.Stop();
        _messageTimer.Interval = duration ?? MessageDuration;
        _messageTimer.Start();
    }

    private DispatcherTimer CreateMessageTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher);

        timer.Tick += (_, _) =>
        {
            // One-shot. Stopping it immediately is what keeps idle CPU at zero.
            timer.Stop();
            TransientMessage.Text = string.Empty;
        };

        return timer;
    }

    /// <summary>
    /// Runs work started by an event handler, guaranteeing failures are visible.
    /// </summary>
    /// <remarks>
    /// Command handlers cannot return a task, so without this a fault becomes an
    /// unobserved exception surfaced at some arbitrary later collection — attributed to
    /// nothing, long after the click that caused it.
    /// </remarks>
    private async void Run(Task work)
    {
        try
        {
            await work.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("ui", ex);
            ShowMessage($"That did not work: {ex.Message}", null);
        }
    }

    private void Run<TResult>(Task<TResult> work) => Run((Task)work);
}
