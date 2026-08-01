using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Etch.App.Tabs;
using Etch.Core.Text;

namespace Etch.App.Views;

/// <summary>
/// Find and replace, and in-place tab renaming.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit ships a search panel and Etch does not use it. Two reasons, both
/// material: it has no replace at all — the upstream control is find-only — so half
/// of this would have had to be built regardless; and its default template is styled
/// for its own host, which in a Mica-backed Fluent window looks like a control from a
/// different application. One bar that does both, styled with everything else, beats
/// two mismatched halves.
/// </para>
/// <para>
/// The matching itself lives in <see cref="TextFinder"/> in <c>Etch.Core</c>, where
/// the awkward cases — an empty regex match, overlapping candidates, a replacement
/// containing the pattern — are unit tests instead of things to find by clicking.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// Largest buffer for which the match count is kept live as the user types.
    /// </summary>
    /// <remarks>
    /// Counting matches walks the whole document, and the find bar stays open while
    /// editing continues — so on a large buffer a live count would run a full scan
    /// between every keypress and the frame that should follow it. Past this size the
    /// count is simply not shown; finding still works, because that only ever scans as
    /// far as the next match.
    /// </remarks>
    private const int MaxLiveCountLength = 512 * 1024;

    /// <summary>
    /// Largest buffer this bar will search at all.
    /// </summary>
    /// <remarks>
    /// Well above the largest document that is still journaled, and far below the plain
    /// text ceiling — where materialising the buffer as a string would allocate a couple
    /// of hundred megabytes on the UI thread for a single press of Enter.
    /// </remarks>
    private const int MaxSearchableLength = 16 * 1024 * 1024;

    /// <summary>Longest selection that is copied into the find box when the bar opens.</summary>
    /// <remarks>
    /// A search term nobody would type by hand is a search term nobody meant to search
    /// for, and Ctrl+A followed by Ctrl+F should not cost a copy of the whole buffer.
    /// </remarks>
    private const int MaxSeedLength = 1024;

    /// <summary>
    /// Quiet period before the match count is recomputed.
    /// </summary>
    /// <remarks>
    /// Counting means materialising the document and scanning it, and the find bar stays
    /// open while editing continues — so doing it inline would put a full scan between
    /// every keypress and the frame that should follow it, which is the one budget in the
    /// plan expressed as an absolute. One-shot, and it stops itself, so idle CPU is
    /// unaffected.
    /// </remarks>
    private static readonly TimeSpan CountDebounce = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _countTimer;
    private TextFinder? _search;

    /// <summary>Shows the bar, seeding it from the selection when there is one.</summary>
    /// <remarks>
    /// Seeding from the selection is the behaviour of every editor worth using.
    /// </remarks>
    private void OpenFindBar(bool replaceMode)
    {
        var replaceVisibility = replaceMode ? Visibility.Visible : Visibility.Collapsed;

        ReplaceInput.Visibility = replaceVisibility;
        ReplaceOneButton.Visibility = replaceVisibility;
        ReplaceAllButton.Visibility = replaceVisibility;

        FindBar.Visibility = Visibility.Visible;

        // Read through the document rather than through Editor.SelectedText, and bounded.
        // The property materialises the whole selection as a string, and someone who has
        // just pressed Ctrl+A on a large buffer would pay for a copy of all of it purely
        // to discover it spans more than one line.
        var length = Editor.SelectionLength;

        if (length is > 0 and <= MaxSeedLength)
        {
            var selection = Editor.Document.GetText(Editor.SelectionStart, length);

            // Skipped for a multi-line selection: nobody selects three lines meaning to
            // search for them, and pasting them into the box hides the previous term for
            // no gain.
            if (!selection.AsSpan().ContainsAny('\r', '\n'))
            {
                FindInput.Text = selection;
            }
        }

        RefreshFindMatches();

        _ = FindInput.Focus();
        FindInput.SelectAll();
    }

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        StopCounting();
        _ = Editor.Focus();
    }

    private void OnFindInputChanged(object sender, TextChangedEventArgs e) => RefreshFindMatches();

    private void OnFindOptionChanged(object sender, RoutedEventArgs e) => RefreshFindMatches();

    /// <summary>
    /// Recompiles the pattern immediately and schedules the count.
    /// </summary>
    /// <remarks>
    /// Compiling is cheap and the result is needed by Enter straight away; counting is
    /// the expensive half and is the only part that waits. Splitting them is what keeps
    /// find responsive on a large buffer without putting a scan on the keystroke path.
    /// </remarks>
    private void RefreshFindMatches()
    {
        var options = new SearchOptions(
            MatchCase: MatchCaseOption.IsChecked == true,
            WholeWord: WholeWordOption.IsChecked == true,
            UseRegex: RegexOption.IsChecked == true);

        var compilation = TextFinder.Compile(FindInput.Text, options);
        _search = compilation.Finder;

        if (compilation.Error is { } error)
        {
            StopCounting();
            SetFindStatus(error);
            return;
        }

        if (_search is null)
        {
            StopCounting();
            SetFindStatus(string.Empty);
            return;
        }

        // Created on first use, one-shot, and it stops itself — the same pattern as the
        // status-bar message timer, for the same reason.
        _countTimer ??= CreateCountTimer();

        _countTimer.Stop();
        _countTimer.Start();
    }

    private DispatcherTimer CreateCountTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = CountDebounce,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CountMatches();
        };

        return timer;
    }

    private void StopCounting() => _countTimer?.Stop();

    /// <summary>
    /// Counts the matches and reports them.
    /// </summary>
    /// <remarks>
    /// A count rather than highlighted matches: counting walks the buffer once, while a
    /// highlight layer would have to be rebuilt and re-rendered. Highlighting belongs
    /// with M2's detection work, where there is a document-version cache to hang it on.
    /// </remarks>
    private void CountMatches()
    {
        if (_search is not { } search)
        {
            return;
        }

        if (Editor.Document.TextLength > MaxLiveCountLength)
        {
            SetFindStatus("Too large to count matches");
            return;
        }

        try
        {
            var matches = search.FindAll(Editor.Document.Text, out var truncated);

            SetFindStatus((matches.Count, truncated) switch
            {
                (_, true) => string.Create(CultureInfo.CurrentCulture, $"More than {matches.Count:N0} matches"),
                (0, _) => "No matches",
                (1, _) => "1 match",
                var (count, _) => string.Create(CultureInfo.CurrentCulture, $"{count:N0} matches"),
            });
        }
        catch (RegexMatchTimeoutException)
        {
            // A pattern that backtracks catastrophically. The timeout is what turns this
            // from a frozen window into a sentence.
            _search = null;
            SetFindStatus("That pattern is too slow to run");
        }
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => MoveToMatch(forward: true);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => MoveToMatch(forward: false);

    private void MoveToMatch(bool forward)
    {
        if (_search is null || !TryReadDocument(out var text))
        {
            return;
        }


        SearchMatch? found;

        try
        {
            // Searching forward starts one past the current selection so that repeated
            // Enter advances instead of finding the same match again; searching
            // backward starts at the selection's start for the mirror-image reason.
            found = forward
                ? _search.FindNext(text, Editor.SelectionStart + Math.Max(Editor.SelectionLength, 1))
                : _search.FindPrevious(text, Editor.SelectionStart);
        }
        catch (RegexMatchTimeoutException)
        {
            SetFindStatus("That pattern is too slow to run");
            return;
        }

        if (found is not { } match)
        {
            SetFindStatus("No matches");
            return;
        }

        Select(match);
    }

    private void OnReplaceOneClick(object sender, RoutedEventArgs e)
    {
        if (_search is null || Editor.Document is not { } document || !TryReadDocument(out var text))
        {
            return;
        }

        try
        {
            // Only replace when the current selection *is* a match. Otherwise Replace
            // behaves as Find — which is what every editor does, and it stops the button
            // from silently overwriting whatever happened to be selected.
            var atSelection = _search.FindNext(text, Editor.SelectionStart);

            if (atSelection is not { } match
                || match.Offset != Editor.SelectionStart
                || match.Length != Editor.SelectionLength)
            {
                MoveToMatch(forward: true);
                return;
            }

            var replacement = _search.Expand(text, match, ReplaceInput.Text ?? string.Empty);

            document.Replace(match.Offset, match.Length, replacement);

            // Caret past the replacement, so the next press moves on rather than
            // reconsidering text that has just been written.
            Editor.Select(match.Offset + replacement.Length, 0);
        }
        catch (RegexMatchTimeoutException)
        {
            SetFindStatus("That pattern is too slow to run");
            return;
        }

        RefreshFindMatches();
        MoveToMatch(forward: true);
    }

    /// <summary>Replaces every match, as one undoable edit.</summary>
    /// <remarks>
    /// Applied back to front, deliberately. Replacing forwards shifts the offsets of
    /// every match after the one just written, so each subsequent replacement lands a
    /// little further off — the classic way this operation corrupts a buffer. Working
    /// backwards leaves the offsets ahead of the cursor untouched.
    /// <para>
    /// Wrapped in a single update so the whole operation is one Ctrl+Z. A thousand
    /// separate undo steps is not an undo history, it is a punishment.
    /// </para>
    /// </remarks>
    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        if (_search is null || Editor.Document is not { } document || !TryReadDocument(out var text))
        {
            return;
        }

        var replaceWith = ReplaceInput.Text ?? string.Empty;

        IReadOnlyList<SearchMatch> matches;
        string[] replacements;

        try
        {
            matches = _search.FindAll(text, out var truncated);

            if (truncated)
            {
                // Doing the first hundred thousand and stopping would leave the buffer in
                // a state nobody asked for and no single Ctrl+Z would obviously describe.
                // Refusing is the honest answer.
                SetFindStatus($"More than {TextFinder.MaxMatches:N0} matches — narrow the pattern first");
                return;
            }

            // Expanded up front, against the original text. Doing it during the edit loop
            // would resolve regex substitutions against a buffer that is already
            // half-rewritten.
            replacements = new string[matches.Count];

            for (var i = 0; i < matches.Count; i++)
            {
                replacements[i] = _search.Expand(text, matches[i], replaceWith);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            SetFindStatus("That pattern is too slow to run");
            return;
        }

        if (matches.Count == 0)
        {
            SetFindStatus("No matches");
            return;
        }

        document.BeginUpdate();

        try
        {
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                document.Replace(matches[i].Offset, matches[i].Length, replacements[i]);
            }
        }
        finally
        {
            document.EndUpdate();
        }

        SetFindStatus(matches.Count == 1 ? "Replaced 1 match" : string.Create(CultureInfo.CurrentCulture, $"Replaced {matches.Count:N0} matches"));
    }

    private void OnFindInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                MoveToMatch(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                break;

            case Key.Escape:
                e.Handled = true;
                CloseFindBar();
                break;
        }
    }

    /// <summary>
    /// Enter in the replace box replaces rather than finds.
    /// </summary>
    /// <remarks>
    /// A separate handler because sharing the find box's would make Enter in a box
    /// labelled "Replace with" move the caret and change nothing, which is the sort of
    /// thing that teaches people not to trust the keyboard.
    /// </remarks>
    private void OnReplaceInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                OnReplaceOneClick(sender, e);
                break;

            case Key.Escape:
                e.Handled = true;
                CloseFindBar();
                break;
        }
    }

    // Escape is handled in MainWindow.Keys.cs, with every other key, so that the order in
    // which the palette and the find bar are offered it lives in one readable place.

    private void Select(SearchMatch match)
    {
        Editor.Select(match.Offset, match.Length);
        Editor.ScrollTo(Editor.Document.GetLineByOffset(match.Offset).LineNumber, 0);
    }

    /// <summary>
    /// Materialises the document for a search, refusing to do it for a huge one.
    /// </summary>
    /// <remarks>
    /// <c>Document.Text</c> walks the whole rope and allocates the buffer as a string. At
    /// the plan's hard ceiling that is a couple of hundred megabytes on the UI thread,
    /// against a 120 MB working-set budget — per press of Enter. Refusing is worse than
    /// searching and better than an out-of-memory crash; a snapshot-based search that
    /// streams belongs with M2, where the document-version cache it needs will exist.
    /// </remarks>
    private bool TryReadDocument(out string text)
    {
        if (Editor.Document.TextLength > MaxSearchableLength)
        {
            text = string.Empty;
            SetFindStatus("Too large to search");
            return false;
        }

        text = Editor.Document.Text;
        return true;
    }

    private void SetFindStatus(string value)
    {
        if (!string.Equals(FindStatus.Text, value, StringComparison.Ordinal))
        {
            FindStatus.Text = value;
        }
    }

    /// <summary>
    /// Focuses the rename box each time the trigger reveals it.
    /// </summary>
    /// <remarks>
    /// Bound to <c>IsVisibleChanged</c> rather than <c>Loaded</c>, and never
    /// unsubscribed. <c>Loaded</c> fires once per container, while the box is still
    /// collapsed, so a self-unsubscribing hook there works for the first F2 on a tab and
    /// silently does nothing for the second — leaving a visible box with no focus, which
    /// never raises <c>LostKeyboardFocus</c> and so can be neither committed nor
    /// dismissed. The tab would be stuck in rename mode.
    /// </remarks>
    private void OnRenameBoxVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || e.NewValue is not true)
        {
            return;
        }

        // Re-seeded from the tab, because the binding is one-way and the box may still be
        // holding what was typed into it during a previous rename that was cancelled.
        if (box.DataContext is BufferTab tab)
        {
            box.Text = tab.Title;
        }

        // At Input priority: the trigger has set Visibility but the layout pass that
        // makes the box focusable has not run yet, so focusing inline silently fails.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!box.IsVisible)
            {
                return;
            }

            _ = box.Focus();
            box.SelectAll();
        }));
    }

    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                CommitRename(box, accept: true);
                break;

            case Key.Escape:
                e.Handled = true;
                CommitRename(box, accept: false);
                break;
        }
    }

    /// <summary>
    /// Commits a rename when focus leaves the box.
    /// </summary>
    /// <remarks>
    /// Committing rather than cancelling, because clicking away from a box you have
    /// just typed into and losing the typing is the more annoying of the two, and the
    /// change is trivially undone by renaming again.
    /// </remarks>
    private void OnRenameBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            CommitRename(box, accept: true);
        }
    }

    private void CommitRename(TextBox box, bool accept)
    {
        if (box.DataContext is not BufferTab tab || !tab.IsRenaming)
        {
            return;
        }

        // Cleared first: the setter below can raise a notification that re-enters here
        // through the focus change the trigger causes.
        tab.IsRenaming = false;

        if (accept)
        {
            // A blank or whitespace-only name is refused by the tab itself, so the old
            // title simply survives.
            tab.Title = box.Text;
            _workspace.RequestSessionSave();
        }
        else
        {
            box.Text = tab.Title;
        }

        if (ReferenceEquals(tab, _workspace.Active))
        {
            Title = $"{tab.Title} — Etch";
        }

        _ = Editor.Focus();
    }
}
