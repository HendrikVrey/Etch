using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Editor;
using Etch.Core.Documents;
using Etch.Core.Text;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Etch.App.Views;

/// <summary>
/// The Etch shell: title bar, editor, status bar.
/// </summary>
/// <remarks>
/// M0 is a spike. There are no tabs, no persistence and no transforms yet — the
/// job of this window is to prove that WPF-UI plus AvalonEdit can hit the startup
/// and large-file budgets before any of that gets built on top.
/// </remarks>
public partial class MainWindow : FluentWindow
{
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StartupResultDuration = TimeSpan.FromSeconds(8);

    private readonly string? _fileToOpen;

    private DispatcherTimer? _messageTimer;
    private CancellationTokenSource? _activeLoad;
    private string? _pendingStartupSummary;

    /// <summary>
    /// A property rather than an initialised field: a field initialiser runs during
    /// construction, which would pull Etch.Core in — assembly load plus static
    /// constructor — inside the measured window-construction phase, for something
    /// not needed until a file is opened.
    /// </summary>
    private static DocumentSizePolicy SizePolicy => DocumentSizePolicy.Default;

    internal MainWindow(string? fileToOpen)
    {
        _fileToOpen = fileToOpen;

        InitializeComponent();

        // Selection changes move the caret, so Caret.PositionChanged alone covers
        // both. Subscribing to SelectionChanged as well would do the same work
        // twice per mouse-move during a drag-select.
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.TextChanged += OnTextChanged;

        // The status bar's initial values are already in XAML; recomputing them
        // here would allocate and invalidate layout on the startup path to produce
        // identical strings.
    }

    /// <summary>Parameterless constructor for the XAML designer only.</summary>
    public MainWindow()
        : this(null)
    {
    }

    /// <summary>
    /// Called once the window is genuinely interactive.
    /// </summary>
    /// <remarks>
    /// Everything deferred to this point is deferred deliberately. Opening a file
    /// here rather than during construction keeps two separate measurements
    /// separate: time-to-interactive, and time-to-open-a-file. Doing the file read
    /// first would fold one into the other and make both useless.
    /// </remarks>
    internal void OnStartupCompleted(StartupReport? report)
    {
        // Hooks WM_SETTINGCHANGE — event-driven, no polling timer, so idle CPU
        // stays at zero.
        SystemThemeWatcher.Watch(this);

        if (report is { } value)
        {
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Startup {value.Total.TotalMilliseconds:0} ms — {(value.WithinBudget ? "within" : "OVER")} the {StartupTimeline.Budget.TotalMilliseconds:0} ms budget");

            ShowMessage(summary, StartupResultDuration);

            // A file named on the command line is opened next, and its progress
            // message would overwrite this one within the same dispatcher turn —
            // so `Etch --diag <file>`, the documented large-file procedure, would
            // never show its startup number. Carry it forward into the load report
            // instead.
            _pendingStartupSummary = _fileToOpen is null ? null : summary;
        }

        if (_fileToOpen is { } path)
        {
            OpenFile(path);
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        // An in-flight read of a large file should stop when the window it was for
        // has gone, rather than running to completion against a dead UI.
        Interlocked.Exchange(ref _activeLoad, null)?.Cancel();

        Editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        Editor.TextChanged -= OnTextChanged;

        _messageTimer?.Stop();
        _messageTimer = null;

        base.OnClosed(e);
    }

    private void OnOpenExecuted(object sender, ExecutedRoutedEventArgs e)
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
            OpenFile(dialog.FileName);
        }
    }

    /// <summary>
    /// Starts a load and guarantees the outcome reaches the user.
    /// </summary>
    /// <remarks>
    /// The awaited work is intentionally not returned to the caller — both call
    /// sites are event handlers. That makes an unhandled exception invisible unless
    /// it is caught here, which is why the catch-all exists: a bare fire-and-forget
    /// would turn "click Open, nothing happens" into a log line nobody reads.
    /// </remarks>
    private async void OpenFile(string path)
    {
        try
        {
            await OpenFileAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("open", ex);
            ShowMessage($"Could not open {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private async Task OpenFileAsync(string path)
    {
        // A second open supersedes the first: cancel the previous read rather than
        // racing two documents into the editor. The cancelled operation disposes
        // its own token source in its finally block.
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _activeLoad, cancellation)?.Cancel();

        ShowMessage($"Opening {Path.GetFileName(path)}…");

        try
        {
            var result = await DocumentLoader.LoadAsync(path, SizePolicy, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            switch (result)
            {
                case DocumentLoadResult.Loaded loaded:
                    Apply(loaded.Document);
                    break;

                case DocumentLoadResult.Refused refused:
                    ShowMessage(refused.Reason);
                    break;

                default:
                    throw new UnreachableException($"Unhandled load result: {result.GetType().Name}");
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer open, or the window closed. Say nothing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OutOfMemoryException)
        {
            ShowMessage($"Could not read {Path.GetFileName(path)}: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeLoad, null, cancellation);
            cancellation.Dispose();
        }
    }

    /// <summary>Installs a loaded document into the editor and reports what it cost.</summary>
    private void Apply(LoadedDocument loaded)
    {
        var stopwatch = Stopwatch.StartNew();

        // Assigning a fresh Document is far cheaper than setting Editor.Text, which
        // routes the whole buffer through a replace operation and onto the undo
        // stack.
        var document = new TextDocument(new StringTextSource(loaded.Text))
        {
            FileName = loaded.Path,
        };

        Editor.Document = document;
        Editor.Encoding = loaded.Encoding;
        Editor.IsModified = false;

        // M0 has no highlighting engine yet; being explicit here documents where
        // the capability check belongs once M2 adds one.
        Editor.SyntaxHighlighting = null;

        var constructed = stopwatch.Elapsed;

        Title = $"{Path.GetFileName(loaded.Path)} — Etch";
        SetText(LineEndingStatus, LineEndings.ToDisplayName(loaded.LineEnding));
        SetText(EncodingStatus, DocumentLoader.DescribeEncoding(loaded.Encoding));
        SetText(FormatChip, loaded.Capabilities.Tier == DocumentTier.PlainText ? "Plain text (forced)" : "Plain text");

        UpdateCaretStatus();
        UpdateCounts();

        // Built here, while `loaded` is in scope, so the continuation below does not
        // capture it. Holding the 50 MB string alive past this point would both
        // delay its collection and make the memory figure meaningless.
        var metadata = DescribeLoad(loaded);
        var notice = loaded.Capabilities.Notice;
        var truncated = loaded.WasTruncated;

        // Assigning the document only invalidates measure. AvalonEdit's first
        // layout, line-number margin sizing and text-view render all happen on
        // later dispatcher passes — so stopping the clock here would measure rope
        // construction and call it rendering, which is the number M0 exists to
        // produce.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            stopwatch.Stop();
            ReportLoad(metadata, notice, truncated, constructed, stopwatch.Elapsed);
        });
    }

    private void ReportLoad(
        string metadata,
        string? notice,
        bool wasTruncated,
        TimeSpan constructed,
        TimeSpan toFirstFrame)
    {
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Loaded in {toFirstFrame.TotalMilliseconds:0} ms");

        if (wasTruncated)
        {
            // Never let this be quiet. A partial buffer that looks complete is a
            // data-loss bug waiting for the first Ctrl+S.
            summary += " — TRUNCATED: the file grew past the size ceiling while being read";
        }
        else if (notice is not null)
        {
            summary += $". {notice}";
        }

        if (Interlocked.Exchange(ref _pendingStartupSummary, null) is { } startup)
        {
            summary += $"  ·  {startup}";
        }

        ShowMessage(summary, StartupResultDuration);

        if (!StartupTimeline.Enabled)
        {
            return;
        }

        DiagnosticLog.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"""
             {metadata}
               lines        {Editor.Document.LineCount:N0}
               constructed  {constructed.TotalMilliseconds:0.00} ms   (UI thread, document build)
               first frame  {toFirstFrame.TotalMilliseconds:0.00} ms   (UI thread, through first render)
               working set  {Environment.WorkingSet / (1024.0 * 1024.0):0.0} MB
               retained     {GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024.0):0.0} MB (after full GC)
             """));
    }

    private static string DescribeLoad(LoadedDocument loaded) => string.Create(
        CultureInfo.InvariantCulture,
        $"""
         Etch — file load
           path         {loaded.Path}
           size         {DocumentSizePolicy.Describe(loaded.SizeInBytes)} ({loaded.SizeInBytes:N0} bytes)
           tier         {loaded.Capabilities.Tier}
           truncated    {loaded.WasTruncated}
           encoding     {DocumentLoader.DescribeEncoding(loaded.Encoding)}
           line ending  {LineEndings.ToDisplayName(loaded.LineEnding)}
           read         {loaded.ReadDuration.TotalMilliseconds:0.00} ms   (off the UI thread)
         """);

    private void OnCaretPositionChanged(object? sender, EventArgs e) => UpdateCaretStatus();

    private void OnTextChanged(object? sender, EventArgs e) => UpdateCounts();

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
    /// Both values are maintained by the document in O(1), which is why this can
    /// safely run on every keystroke. A word count cannot — it needs a full scan of
    /// an off-thread snapshot, and that arrives with M1's debounce infrastructure.
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
    /// Assigns status text only when it actually changed.
    /// </summary>
    /// <remarks>
    /// These run on every keystroke, and the status items sit in a horizontal
    /// StackPanel where any width change re-arranges its siblings. Skipping the
    /// no-op assignment keeps typing off the layout path entirely.
    /// </remarks>
    private static void SetText(System.Windows.Controls.TextBlock target, string value)
    {
        if (!string.Equals(target.Text, value, StringComparison.Ordinal))
        {
            target.Text = value;
        }
    }

    /// <summary>Shows a message in the status bar that clears itself.</summary>
    private void ShowMessage(string message, TimeSpan? duration = null)
    {
        TransientMessage.Text = message;

        // Created on first use rather than in the constructor: a timer nobody has
        // needed yet should not be on the startup path.
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
}
