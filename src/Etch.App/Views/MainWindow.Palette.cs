using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Palette;
using Etch.Core.Abstractions;
using Etch.Core.Palette;
using Etch.Core.Text;
using ICSharpCode.AvalonEdit.Document;

namespace Etch.App.Views;

/// <summary>
/// The command palette, <c>Ctrl+Enter</c>, and applying a transform to the buffer.
/// </summary>
/// <remarks>
/// <para>
/// The single most important interaction in the product, and the reason the rest of it
/// exists: transforms come to the text rather than the text going to a tool. Everything
/// here is in service of two keystrokes — <c>Ctrl+Enter</c> to do the obvious thing,
/// <c>Ctrl+Shift+P</c> to choose something else — and neither of them opens a dialog,
/// leaves the buffer, or asks a question.
/// </para>
/// <para>
/// In its own file because it is a coherent feature rather than more of the shell. The
/// ranking, the matching and every transform live in <c>Etch.Core</c>; what is left
/// here is reading the selection, hopping off the UI thread, and putting the answer
/// back as one undo step.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// How long a transform may run before it is abandoned.
    /// </summary>
    /// <remarks>
    /// The plan budgets 500 ms for formatting 10 MB of JSON. This is twenty times that,
    /// so it is not a performance gate — it is the backstop that stops a pathological
    /// input from leaving the editor with no way to say what happened.
    /// </remarks>
    private static readonly TimeSpan TransformTimeout = TimeSpan.FromSeconds(10);

    private readonly DetectionService _detection = new();
    private readonly ObservableCollection<PaletteRow> _paletteRows = [];

    private IReadOnlyList<string> _recentTransformIds = [];
    private bool _transformInFlight;

    /// <summary>The palette's rows, for the list to bind to.</summary>
    public ReadOnlyObservableCollection<PaletteRow> PaletteRows { get; private set; } = null!;

    /// <summary>True while the palette overlay is up.</summary>
    private bool IsPaletteOpen => PaletteOverlay.Visibility == Visibility.Visible;

    /// <summary>Opens the command palette.</summary>
    /// <remarks>
    /// Typed as <see cref="ICommand"/>, not <c>RelayCommand</c>, and not by preference:
    /// <c>RelayCommand</c> is internal, and a public member exposing an internal type is
    /// CS0053. The rest of the window's commands are declared the same way.
    /// </remarks>
    public ICommand OpenPaletteCommand { get; private set; } = null!;

    /// <summary>Applies the transform the buffer suggests, if there is one.</summary>
    public ICommand ApplySuggestedCommand { get; private set; } = null!;

    /// <summary>
    /// Builds the palette's commands and wires detection to the status bar.
    /// </summary>
    /// <remarks>
    /// Called from the constructor <em>before</em> <c>InitializeComponent</c>, because
    /// the XAML key bindings bind to the two commands above. Touches no control: every
    /// member of this type that does is reached from a user action, by which time the
    /// template has been built.
    /// </remarks>
    private void InitialisePalette()
    {
        PaletteRows = new ReadOnlyObservableCollection<PaletteRow>(_paletteRows);

        OpenPaletteCommand = new RelayCommand(OpenPalette, () => _bound?.Document is not null);
        ApplySuggestedCommand = new RelayCommand(ApplySuggested, () => _bound?.Document is not null);

        _detection.Changed += OnDetectionChanged;
    }

    /// <summary>Schedules a re-detection after the buffer changed.</summary>
    /// <remarks>
    /// The one call the keystroke path makes into this feature. Everything expensive is
    /// behind the debounce inside <see cref="DetectionService"/>.
    /// </remarks>
    private void InvalidateDetection(TextDocument? document) => _detection.Invalidate(document);

    /// <summary>Shows what the buffer was detected as.</summary>
    /// <remarks>
    /// "(sampled)" is not padding. A 10 MB document is identified from its first 64 KB,
    /// and a chip that said plain "JSON" would be claiming the whole file had been
    /// checked when it had not.
    /// </remarks>
    private void OnDetectionChanged(DetectionResult result)
    {
        SetText(FormatChip, DescribeFormat(result));

        // The palette is ranked against the detection, so a result arriving while it is
        // open has to reorder it — otherwise pasting into an open palette leaves the
        // wrong first row under an Enter that is about to be pressed.
        if (IsPaletteOpen)
        {
            RefreshPalette();
        }
    }

    /// <summary>The label for the status bar's format chip.</summary>
    /// <remarks>
    /// The default arm is a lie waiting to happen: a <see cref="FormatId"/> added without a
    /// case here shows a recognised buffer as "Plain text" while every test still passes.
    /// <c>FormatChipTests</c> walks the enum and asserts every member has a label of its own,
    /// which is the only thing that makes this switch safe to leave exhaustive-by-convention.
    /// </remarks>
    internal static string DescribeFormat(DetectionResult result)
    {
        if (!result.IsRecognised)
        {
            return "Plain text";
        }

        var name = result.Format switch
        {
            FormatId.Json => "JSON",
            FormatId.Ndjson => "NDJSON",
            FormatId.Base64 => "Base64",
            FormatId.Base64Url => "Base64url",
            FormatId.Hex => "Hex",
            FormatId.UrlEncoded => "URL-encoded",
            FormatId.Jwt => "JWT",
            FormatId.Guid => "GUID",
            FormatId.UnixEpoch => "Unix time",
            FormatId.Iso8601 => "ISO-8601",
            _ => "Plain text",
        };

        return result.WasSampled ? $"{name} (sampled)" : name;
    }

    /// <summary>Opens the palette over the editor.</summary>
    private void OpenPalette()
    {
        if (_bound?.Document is null || IsPaletteOpen)
        {
            return;
        }

        // Detected synchronously rather than waiting out the debounce. Someone who
        // pastes and immediately presses Ctrl+Shift+P must not get a list ranked for the
        // buffer they had a moment ago.
        _detection.DetectNow(_bound.Document);

        PaletteQuery.Text = string.Empty;
        PaletteOverlay.Visibility = Visibility.Visible;

        RefreshPalette();

        // After a layout pass. Focusing an element in the same call stack that made its
        // parent visible does not stick, and a palette that opens without the caret in
        // it is a palette you have to click before you can type.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsPaletteOpen)
                {
                    PaletteQuery.Focus();
                    PaletteQuery.SelectAll();
                }
            }));
    }

    private void ClosePalette()
    {
        if (!IsPaletteOpen)
        {
            return;
        }

        PaletteOverlay.Visibility = Visibility.Collapsed;
        _paletteRows.Clear();

        // Focus goes back where it came from, or the next keystroke lands nowhere.
        Editor.Focus();
    }

    private void RefreshPalette()
    {
        // Remembered across the rebuild, because a detection landing while the palette is
        // open rebuilds this list under a selection the user has already moved. Putting a
        // different transform under an Enter that is about to be pressed is the one thing
        // a palette must never do.
        var wasSelected = (PaletteList.SelectedItem as PaletteRow)?.Transform;

        var ranked = PaletteRanking.Rank(PaletteQuery.Text, _detection.Current, _recentTransformIds);

        _paletteRows.Clear();

        foreach (var entry in ranked)
        {
            _paletteRows.Add(PaletteRow.From(entry.Transform, entry.IsSuggested));
        }

        PaletteList.SelectedIndex = Reselect(wasSelected);

        SetText(
            PaletteEmpty,
            _paletteRows.Count > 0 ? string.Empty : "Nothing matches that.");
    }

    /// <summary>
    /// Finds the previously selected transform in the rebuilt list, or falls back to the
    /// top row.
    /// </summary>
    /// <remarks>
    /// Falling back to the top rather than to nothing: a rebuild caused by typing should
    /// land on the best new match, and that is the same row a fresh open would select.
    /// </remarks>
    private int Reselect(ITransform? previous)
    {
        if (_paletteRows.Count == 0)
        {
            return -1;
        }

        if (previous is null)
        {
            return 0;
        }

        for (var i = 0; i < _paletteRows.Count; i++)
        {
            if (ReferenceEquals(_paletteRows[i].Transform, previous))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Moves the selection, wrapping at both ends.</summary>
    /// <remarks>
    /// Wrapping because the list is short and the alternative is a down-arrow that
    /// silently does nothing at the bottom.
    /// </remarks>
    private void MovePaletteSelection(int delta)
    {
        if (_paletteRows.Count == 0)
        {
            return;
        }

        var next = (PaletteList.SelectedIndex + delta + _paletteRows.Count) % _paletteRows.Count;

        PaletteList.SelectedIndex = next;
        PaletteList.ScrollIntoView(PaletteList.SelectedItem);
    }

    private void RunSelectedPaletteRow()
    {
        if (PaletteList.SelectedItem is not PaletteRow row)
        {
            return;
        }

        ClosePalette();
        Run(ApplyAsync(row.Transform));
    }

    /// <summary>
    /// Handles <c>Ctrl+Enter</c>: apply the obvious transform, without opening anything.
    /// </summary>
    /// <remarks>
    /// Only ever runs something that applies to the detected format. This key acts
    /// without showing what it is about to do, so "the obvious thing or nothing" is the
    /// only safe rule — anything looser is a keystroke that reformats a buffer at random.
    /// </remarks>
    private void ApplySuggested()
    {
        if (_bound?.Document is not { } document)
        {
            return;
        }

        var detection = _detection.DetectNow(document);
        var suggested = PaletteRanking.Suggested(detection, _recentTransformIds);

        if (suggested is null)
        {
            ShowMessage(
                detection.IsRecognised
                    ? $"Nothing obvious to do with {DescribeFormat(detection)}. Ctrl+Shift+P for everything."
                    : "Etch does not recognise this text. Ctrl+Shift+P for everything.",
                null);

            return;
        }

        Run(ApplyAsync(suggested));
    }

    /// <summary>
    /// Runs a transform over the selection, or the whole buffer when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is applied inside one <c>BeginUpdate</c>/<c>EndUpdate</c> pair, so a
    /// transform is a single <c>Ctrl+Z</c> however much of the document it rewrote. That
    /// is what makes chaining safe to experiment with: three transforms in, three undos
    /// out.
    /// </para>
    /// <para>
    /// The document version is captured before the work starts and checked after. If the
    /// text changed while a transform was running, the result describes a buffer that no
    /// longer exists and is discarded <em>silently</em> — the user has moved on, and an
    /// error about work they did not know was happening would be noise.
    /// </para>
    /// </remarks>
    private async Task ApplyAsync(ITransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        if (_bound is not { Document: { } document } tab || Editor.IsReadOnly)
        {
            return;
        }

        // One at a time. Holding Ctrl+Enter would otherwise start a transform per repeat,
        // each racing to replace the buffer the others are reading.
        if (_transformInFlight)
        {
            ShowMessage("Still working on the last one.", null);
            return;
        }

        var hasSelection = Editor.SelectionLength > 0;
        var start = hasSelection ? Editor.SelectionStart : 0;
        var length = hasSelection ? Editor.SelectionLength : document.TextLength;

        // Generators are exempt: an empty tab is where someone reaches for "New GUID", and
        // refusing there would make the transform unreachable in its normal case.
        if (length == 0 && transform.NeedsInput)
        {
            ShowMessage("There is nothing in this tab yet.", null);
            return;
        }

        var input = new TransformInput(
            document.GetText(start, length),
            hasSelection,
            new TransformOptions(
                Editor.Options.IndentationSize,
                LineEndings.ToLiteral(tab.LineEnding) is { Length: > 0 } literal ? literal : "\n"));

        var version = document.Version;

        _transformInFlight = true;

        try
        {
            TransformResult result;

            using (var deadline = new CancellationTokenSource(TransformTimeout))
            {
                try
                {
                    result = await Task
                        .Run(() => transform.Apply(input, deadline.Token), deadline.Token)
                        .ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    ShowMessage($"{transform.Name} was taking too long and was stopped. The text is unchanged.", null);
                    return;
                }
                catch (Exception ex)
                {
                    // A transform is documented to report bad input by returning a
                    // failure. Reaching here means a genuine defect in one, and losing
                    // the buffer to it would be out of all proportion.
                    DiagnosticLog.WriteFailure($"transform:{transform.Id}", ex);
                    ShowMessage($"{transform.Name} failed unexpectedly: {ex.Message}", null);
                    return;
                }
            }

            // Nothing this transform produced describes the tab in front any more. Checked
            // before the result is looked at rather than only before it is written, because
            // the status bar belongs to the foreground tab just as much as the document
            // does: "Valid JSON — an object with 42 keys" over a tab holding a stack trace
            // is a false statement, and the transform would also climb the recency list on
            // the strength of a buffer the user has left.
            if (!ReferenceEquals(_bound, tab))
            {
                return;
            }

            if (!result.Success)
            {
                ShowMessage(result.Error ?? $"{transform.Name} could not be applied.", null);
                return;
            }

            // A transform that answered a question rather than performing an edit — the
            // validators, and anything that found nothing to do. Nothing is written, so
            // there is no *version* check to make and no undo step to create: replacing a
            // document with a byte-identical copy would cost the caret position and a
            // Ctrl+Z for no visible change.
            //
            // Captured into a local rather than read twice: the flow analysis that would
            // otherwise have to carry the null-state across the version check is exactly the
            // sort of thing a later edit quietly invalidates.
            var replacement = result.Text;

            if (replacement is null)
            {
                _recentTransformIds = PaletteRanking.Remember(_recentTransformIds, transform.Id);
                ShowMessage(result.Message ?? $"{transform.Name} found nothing to report.", null);

                return;
            }

            // The version as well as the tab. Replace() finishes by moving the caret and
            // selection, and those belong to the one editor control rather than to the tab —
            // so a transform that completes after an edit would write over text it never
            // read, and Editor.Select validates against the document as it is now, throwing
            // if it has become shorter.
            if (IsStale(document, version))
            {
                return;
            }

            Replace(document, start, length, replacement, hasSelection);

            // Recorded only on success, so a transform that never worked does not climb
            // the palette for having been tried.
            _recentTransformIds = PaletteRanking.Remember(_recentTransformIds, transform.Id);

            // Detection re-runs immediately rather than on the debounce, so the palette
            // is already ranked for the new format if the next chain step follows at
            // once. This is what makes base64 to JSON to sorted keys feel like one move.
            _detection.DetectNow(document);

            ShowMessage(
                result.Message
                ?? $"{transform.Name} applied{(hasSelection ? " to the selection" : string.Empty)}.",
                null);
        }
        finally
        {
            _transformInFlight = false;
        }
    }

    /// <summary>True when the document has changed since <paramref name="version"/>.</summary>
    private static bool IsStale(TextDocument document, ITextSourceVersion? version) =>
        version is null
        || !document.Version.BelongsToSameDocumentAs(version)
        || document.Version.CompareAge(version) != 0;

    /// <summary>Writes the result back as a single undo step.</summary>
    private void Replace(TextDocument document, int start, int length, string text, bool wasSelection)
    {
        document.BeginUpdate();

        try
        {
            document.Replace(start, length, text);
        }
        finally
        {
            document.EndUpdate();
        }

        // The selection is restored over the new text so a chain can be applied to the
        // same region again without reselecting it. Without a selection the caret goes
        // to the start, because the alternative — an offset into text that has been
        // wholly rewritten — is meaningless.
        if (wasSelection)
        {
            Editor.Select(start, Math.Min(text.Length, document.TextLength - start));
        }
        else
        {
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
        }
    }

    private void OnPaletteQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsPaletteOpen)
        {
            RefreshPalette();
        }
    }

    private void OnPaletteQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePalette();
                e.Handled = true;
                break;

            case Key.Enter:
                RunSelectedPaletteRow();
                e.Handled = true;
                break;

            case Key.Down:
                MovePaletteSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MovePaletteSelection(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Runs a row when it is clicked.</summary>
    /// <remarks>
    /// Handled on the list rather than on each container, so the hit test has to be
    /// checked here: a click on the empty space below the last row, or the end of a
    /// scrollbar drag, also arrives here and must not run whatever happened to be
    /// selected.
    /// </remarks>
    private void OnPaletteRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (!IsWithinRow(e.OriginalSource as DependencyObject))
        {
            return;
        }

        RunSelectedPaletteRow();
        e.Handled = true;
    }

    /// <summary>Walks up the visual tree looking for a list row.</summary>
    private static bool IsWithinRow(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.ListBoxItem)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Dismisses the palette when the dimmed backdrop is clicked.</summary>
    private void OnPaletteBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        ClosePalette();
        e.Handled = true;
    }
}
