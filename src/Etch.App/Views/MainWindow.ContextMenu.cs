using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Etch.Core.Abstractions;
using Etch.Core.Palette;

namespace Etch.App.Views;

/// <summary>
/// The editor's right-click menu: the ordinary edit commands, plus whatever the buffer
/// is currently ready for.
/// </summary>
/// <remarks>
/// <para>
/// The third route to a transform, after <c>Ctrl+Enter</c> and <c>Ctrl+Shift+P</c>, and
/// the only one that is discoverable without having read anything. It shows the same
/// transforms the palette would mark green, in the same order, because
/// <see cref="PaletteRanking.SuggestedTop"/> is the single place that decides what is
/// ready: see its remarks for why that matters more than it looks.
/// </para>
/// <para>
/// <b>The menu is attached to <c>Editor.TextArea</c>, not to the <c>TextEditor</c>, and
/// that is load-bearing.</b> AvalonEdit's <c>TextEditor</c> has no command bindings of
/// its own: <c>Copy()</c>, <c>Cut()</c>, <c>Paste()</c> and <c>SelectAll()</c> all
/// forward to the <c>TextArea</c>, and the real bindings are registered by
/// <c>EditingCommandHandler</c> and <c>CaretNavigationCommandHandler</c> onto the
/// <c>TextArea</c>'s input handler, whose <c>CanExecute</c> handlers begin with
/// <c>target as TextArea</c> and do nothing when the target is anything else. A menu
/// targeting the <c>TextEditor</c> would therefore render every clipboard item greyed
/// out, which looks like a WPF bug and is not one. (Verified against upstream source,
/// 2026-08-07.)
/// </para>
/// <para>
/// Built in code rather than declared in XAML because the transform rows are rebuilt on
/// every open, and because <c>TextArea</c> is not a settable property from a XAML
/// attribute on <c>&lt;avalonEdit:TextEditor&gt;</c>. Keeping the static items in markup
/// and the dynamic ones here would split one short menu across two files and leave the
/// insertion point to index arithmetic.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How many ready transforms the menu offers. Hendrik's number.</summary>
    /// <remarks>
    /// Four is enough to cover the obvious chain steps for every format Etch detects
    /// without the menu becoming a second palette. Anything past this is
    /// <c>Ctrl+Shift+P</c>, which is on the last row precisely so the ceiling is never a
    /// dead end.
    /// </remarks>
    private const int ContextMenuTransformCount = 4;

    /// <summary>The green marker. The same glyph and the same brush as the palette's.</summary>
    /// <remarks>
    /// "Green means this is ready for what you have" has to mean one thing in both
    /// places. If these two drift apart the feature stops explaining itself, so the
    /// glyph, the size and the brush key are all deliberately identical to the palette
    /// row template in <c>MainWindow.xaml</c>.
    /// </remarks>
    private const string SuggestedMarker = "●";

    /// <summary>Brush key for the marker, shared with the palette.</summary>
    private const string SuggestedMarkerBrushKey = "SystemFillColorSuccessBrush";

    /// <summary>Attaches the right-click menu to the editor.</summary>
    /// <remarks>
    /// Called after <c>InitializeComponent</c>, because it needs the editor, and
    /// therefore its <c>TextArea</c>, to exist. The menu instance is created once and
    /// its contents rebuilt on each open.
    /// </remarks>
    private void InitialiseContextMenu()
    {
        var menu = new ContextMenu();

        Editor.TextArea.ContextMenu = menu;
        Editor.TextArea.ContextMenuOpening += OnEditorContextMenuOpening;
    }

    /// <summary>
    /// Rebuilds the menu against whatever the buffer holds right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilt on <c>ContextMenuOpening</c> rather than on <c>Opened</c>, so the items
    /// are in place before the menu is measured and shown; a menu that changes shape
    /// after appearing moves the row under the pointer.
    /// </para>
    /// <para>
    /// <b>Right-clicking does not move the caret or change the selection</b>, which is
    /// AvalonEdit's behaviour and is deliberately left alone. The menu therefore acts on
    /// the current selection, or on the whole buffer when there is none: byte-identical
    /// to what <c>Ctrl+Enter</c> would do at that moment. Making right-click move the
    /// caret, as some editors do, would silently change what the transform runs against
    /// between the click that opened the menu and the row that was chosen. Do not
    /// "fix" this.
    /// </para>
    /// <para>
    /// One consequence of hanging the menu off the <c>TextArea</c> rather than the
    /// <c>TextEditor</c>: a right-click inside the editor's 14 px padding band, or on a
    /// scrollbar, lands on <c>PART_ScrollViewer</c> and raises nothing here, so no menu
    /// appears at all. That is a fair price for the clipboard commands working, and it is
    /// recorded so the next person reads it as a trade rather than as a bug.
    /// </para>
    /// </remarks>
    private void OnEditorContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Editor.TextArea.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();

        menu.Items.Add(EditCommandItem(ApplicationCommands.Cut, "Cut", "Ctrl+X"));
        menu.Items.Add(EditCommandItem(ApplicationCommands.Copy, "Copy", "Ctrl+C"));
        menu.Items.Add(EditCommandItem(ApplicationCommands.Paste, "Paste", "Ctrl+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(EditCommandItem(ApplicationCommands.SelectAll, "Select all", "Ctrl+A"));
        menu.Items.Add(new Separator());

        AddTransformItems(menu);

        menu.Items.Add(new Separator());
        menu.Items.Add(AllTransformsItem());
    }

    /// <summary>Adds the ready transforms, or one row explaining that there are none.</summary>
    /// <remarks>
    /// Detected synchronously, for the same reason <c>OpenPalette</c> and
    /// <c>ApplySuggested</c> do it: waiting out the 150 ms debounce would rank the menu
    /// against the buffer the user had a moment ago. <c>DetectNow</c> is bounded to a
    /// 64 KB sample, so this is not a cost that grows with the document.
    /// </remarks>
    private void AddTransformItems(ContextMenu menu)
    {
        IReadOnlyList<ITransform> ready = [];

        if (_bound?.Document is { } document)
        {
            ready = PaletteRanking.SuggestedTop(
                _detection.DetectNow(document),
                _recentTransformIds,
                ContextMenuTransformCount);
        }

        if (ready.Count == 0)
        {
            // A disabled row rather than nothing, so the menu keeps its shape and the
            // absence is stated. A menu that silently loses a section reads as broken.
            menu.Items.Add(new MenuItem { Header = "Nothing obvious for this text", IsEnabled = false });

            return;
        }

        for (var i = 0; i < ready.Count; i++)
        {
            menu.Items.Add(TransformItem(ready[i], isFirst: i == 0));
        }
    }

    /// <summary>One clipboard or selection command, targeted at the text area.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="MenuItem.CommandTarget"/> is set explicitly rather than bound through
    /// <c>PlacementTarget</c>: the menu is built here, so the target is known outright
    /// and a binding would only add a way for it to resolve to nothing.
    /// </para>
    /// <para>
    /// The routed commands are used rather than hand-rolled handlers so that
    /// <c>CanExecute</c> stays AvalonEdit's answer, whatever that answer is. Two of those
    /// answers are worth knowing:
    /// </para>
    /// <para>
    /// <b>Cut and Copy are enabled even with no selection.</b> AvalonEdit's
    /// <c>CanCutOrCopy</c> is <c>Options.CutCopyWholeLine || !Selection.IsEmpty</c>, and
    /// <c>CutCopyWholeLine</c> defaults to true, so both act on the caret's line, which
    /// is exactly what <c>Ctrl+X</c> and <c>Ctrl+C</c> already do in this editor. The menu
    /// agreeing with the keyboard is the point.
    /// </para>
    /// <para>
    /// <b>Paste disables itself while the document is read-only</b>, because its handler
    /// asks the read-only section provider. In Etch that window is only the moment between
    /// <c>OnClosing</c> and the window actually closing; there is no read-only large-file
    /// tier, whatever the size tiers switch off.
    /// </para>
    /// </remarks>
    private MenuItem EditCommandItem(RoutedUICommand command, string header, string gesture) =>
        new()
        {
            Header = header,
            Command = command,
            CommandTarget = Editor.TextArea,
            InputGestureText = gesture,
        };

    /// <summary>One transform row, marked green and running the ordinary apply path.</summary>
    /// <remarks>
    /// <c>Ctrl+Enter</c> is shown on the first row only, because that is the row it would
    /// actually run. Putting the gesture on all four would be a lie about three of them.
    /// </remarks>
    private MenuItem TransformItem(ITransform transform, bool isFirst)
    {
        var item = new MenuItem
        {
            Header = SuggestedHeader(transform.Name),
            InputGestureText = isFirst ? "Ctrl+Enter" : string.Empty,
        };

        // The existing apply path, not a second one: undo grouping, the staleness checks,
        // the timeout, the recency bookkeeping and the failure-offset caret move all come
        // along unchanged because this is the same call the palette makes.
        item.Click += (_, _) => Run(ApplyAsync(transform));

        return item;
    }

    /// <summary>The last row: everything else, in the palette.</summary>
    private MenuItem AllTransformsItem()
    {
        var item = new MenuItem
        {
            Header = "All transforms...",
            InputGestureText = "Ctrl+Shift+P",
            IsEnabled = _bound?.Document is not null,
        };

        item.Click += (_, _) => OpenPalette();

        return item;
    }

    /// <summary>Builds a header of the green marker followed by the transform's name.</summary>
    /// <remarks>
    /// The brush is attached with <see cref="FrameworkElement.SetResourceReference"/>,
    /// which is the code equivalent of <c>DynamicResource</c>: a static lookup would
    /// freeze the marker at whichever theme was loaded when the menu was first opened.
    /// </remarks>
    private static StackPanel SuggestedHeader(string name)
    {
        var marker = new TextBlock
        {
            Text = SuggestedMarker,
            FontSize = 11,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        marker.SetResourceReference(TextBlock.ForegroundProperty, SuggestedMarkerBrushKey);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(marker);
        panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });

        return panel;
    }
}
