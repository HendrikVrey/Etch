using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Etch.App.Tabs;

namespace Etch.App.Views;

/// <summary>
/// The tab strip's mouse behaviour: reordering by drag, pinning, and the context menu.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is about the pointer. The tab's appearance is entirely in XAML, and
/// what a tab <em>is</em> belongs to <see cref="Workspace"/>; this file only turns
/// gestures into calls.
/// </para>
/// <para>
/// The drag is a plain mouse capture rather than <c>DragDrop.DoDragDrop</c>. The
/// framework's drag-and-drop runs a nested message loop, which is a heavy thing to start
/// from inside a custom title bar, and it gives no reordering feedback until the drop.
/// Capturing the mouse and moving the tab as the pointer crosses its neighbours is both
/// simpler and the behaviour people expect: the strip rearranges under the cursor.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>The tab under the pointer when the button went down, if it can be dragged.</summary>
    private BufferTab? _dragCandidate;

    /// <summary>Where the press landed, in the strip's own coordinates.</summary>
    private Point _dragOrigin;

    /// <summary>True once the pointer has moved far enough to mean a drag rather than a click.</summary>
    private bool _dragging;

    /// <summary>
    /// True while a request to scroll the tab in front into view is sitting on the
    /// dispatcher queue.
    /// </summary>
    /// <remarks>
    /// Exists to keep exactly one outstanding. See
    /// <see cref="ScrollActiveTabIntoView"/> for why more than one froze the application.
    /// </remarks>
    private bool _tabScrollQueued;

    /// <summary>
    /// Records a possible drag when a tab is pressed.
    /// </summary>
    /// <remarks>
    /// Deliberately does not mark the event handled: the button still needs to see it so
    /// that a press with no movement raises <c>Click</c> and activates the tab as usual.
    /// The drag only begins if the pointer then travels, at which point taking the mouse
    /// capture cancels the button's press for us and no click is raised.
    /// </remarks>
    private void OnTabPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _dragCandidate = null;
        _dragging = false;

        if (sender is not Button { DataContext: BufferTab tab } surface)
        {
            return;
        }

        // A press that landed on the close button or in the rename box is not the start of
        // a drag. Without this, nudging the mouse while aiming for the close button would
        // reorder the strip instead of closing anything, and selecting text in a caption
        // being renamed would drag the tab out from under the caret.
        if (!LandedOnTabSurface(e.OriginalSource, surface))
        {
            return;
        }

        _dragCandidate = tab;
        _dragOrigin = e.GetPosition(TabStrip);
    }

    /// <summary>
    /// Turns pointer travel into a reorder.
    /// </summary>
    /// <remarks>
    /// The threshold is the system's own, so a drag starts at the distance every other
    /// Windows application starts one at: a hand-picked number here would make the strip
    /// feel subtly wrong on a touchpad or a high-DPI screen.
    /// </remarks>
    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_dragCandidate is not { } dragged)
        {
            return;
        }

        // The button can be released outside the window, where no mouse-up ever reaches
        // us. Checking the live state rather than trusting the last event we saw is what
        // stops the strip staying in a drag nobody is performing.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var point = e.GetPosition(TabStrip);

        if (!_dragging)
        {
            if (Math.Abs(point.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(point.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _dragging = true;

            // Taking the capture ends the tab button's press, so the release at the end of
            // a drag does not also raise Click. A press that never moves keeps its capture
            // and behaves exactly as a click always did.
            _ = Mouse.Capture(TabStrip, CaptureMode.Element);

            // Dragged means selected, as it does in every browser. Waiting for the drop
            // would leave the user rearranging a tab whose contents they cannot see.
            if (!ReferenceEquals(_workspace.Active, dragged))
            {
                Run(ActivateAsync(dragged));
            }
        }

        ContinueDrag(dragged, point);
    }

    /// <summary>
    /// Moves the dragged tab once the pointer has passed the middle of a neighbour.
    /// </summary>
    /// <remarks>
    /// The midpoint rule is not polish, it is what stops the strip oscillating. Tabs are
    /// content-sized between a floor and a ceiling, so neighbours are routinely different
    /// widths, and swapping the instant the pointer crosses an edge puts the wider tab
    /// under the pointer, which immediately satisfies the condition to swap straight back.
    /// The strip would flicker between two orderings while the pointer sat still.
    /// <para>
    /// The direction of travel is part of the test for the same reason: past the middle
    /// going right means "after you", past the middle going left means "before you", and
    /// the two are not the same threshold.
    /// </para>
    /// </remarks>
    private void ContinueDrag(BufferTab dragged, Point point)
    {
        var from = _workspace.Tabs.IndexOf(dragged);

        // The dragged tab can be closed from elsewhere mid-gesture (a second instance
        // handing over a file, or the journal reporting a failure) and reordering a tab
        // that is no longer in the strip would be meaningless.
        if (from < 0 || !TryLocate(point, out var target, out var centre) || target == from)
        {
            return;
        }

        if (target > from ? point.X <= centre : point.X >= centre)
        {
            return;
        }

        _workspace.Move(dragged, target);
    }

    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    /// <summary>
    /// Clears the drag if the capture is taken away.
    /// </summary>
    /// <remarks>
    /// A context menu, an Alt+Tab, or anything else that steals the capture would
    /// otherwise leave the strip believing a drag is still in progress, so the next
    /// pointer movement anywhere over the tabs would reorder them.
    /// </remarks>
    private void OnTabStripLostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragCandidate = null;
        _dragging = false;
    }

    private void EndDrag()
    {
        if (_dragging && ReferenceEquals(Mouse.Captured, TabStrip))
        {
            // Raises LostMouseCapture, which clears the rest of the state.
            TabStrip.ReleaseMouseCapture();
        }

        _dragCandidate = null;
        _dragging = false;
    }

    /// <summary>
    /// Scrolls the strip sideways on the wheel.
    /// </summary>
    /// <remarks>
    /// The scroll viewer's own handling is vertical, and it is disabled, so without this
    /// the wheel would simply be forwarded to the parent and tabs past the right-hand edge
    /// would be reachable only from the keyboard. The scrollbar itself is hidden
    /// deliberately: it belongs neither in a title bar nor in the two pixels where the
    /// active tab's accent underline goes.
    /// </remarks>
    private void OnTabStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (TabScroller.ScrollableWidth <= 0)
        {
            return;
        }

        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// Keeps the tab in front on screen as the strip's own width changes.
    /// </summary>
    /// <remarks>
    /// The strip is bounded to a share of the window's width, so dragging the window
    /// narrower takes tabs off the right-hand edge, and the one in front is as likely to
    /// be among them as any other. Without this, scrolling on activation alone would hold
    /// only until the window was next resized, which is the gesture that provokes the
    /// problem in the first place. Adding and removing tabs changes this width too, so the
    /// same handler covers a strip that has just grown past its ceiling.
    /// </remarks>
    private void OnTabStripViewportChanged(object sender, SizeChangedEventArgs e) =>
        ScrollActiveTabIntoView();

    /// <summary>
    /// Asks, once, for the strip to be scrolled to the tab in front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The strip is bounded to the window's width less an allowance for the caption
    /// buttons, so at the window's own 480 px minimum it holds two tabs of the minimum
    /// width and fewer than two of the maximum. Every route that changes the active tab
    /// can therefore put a tab in front that is scrolled past the edge: Ctrl+Tab,
    /// Ctrl+1..9, reopening a closed tab, a file handed over by a second instance, and
    /// above all Ctrl+T, which appends at the end and so lands off-screen exactly when
    /// the strip is already full. The editor changes under a strip that does not move,
    /// which reads as the shortcut having done nothing at all.
    /// </para>
    /// <para>
    /// Deferred to <see cref="DispatcherPriority.Loaded"/> because a tab activated in the
    /// same dispatcher turn it was created in has no container yet, the items control
    /// generates one on the next layout pass, and asking a container that does not exist
    /// to be shown fails silently, which is the failure this is here to remove rather
    /// than to reproduce one layer down.
    /// </para>
    /// <para>
    /// <b>At most one request may be outstanding, and that is not a tidiness measure.</b>
    /// <see cref="DispatcherPriority.Loaded"/> is priority 6 and <c>Input</c> is 5, so
    /// Loaded operations are serviced <em>before</em> pending input. Resizing the window
    /// raises <c>SizeChanged</c> on every layout pass, dozens of times across one drag
    /// of the window edge, and queueing one operation per pass built a backlog that ran
    /// ahead of the user's own wheel and click events, so the whole application stopped
    /// responding until it drained. Dropping the duplicate is exact rather than
    /// approximate, because the pending operation reads the tab in front when it runs
    /// rather than carrying one from when it was queued.
    /// </para>
    /// </remarks>
    private void ScrollActiveTabIntoView()
    {
        // Not while a tab is being dragged. The gesture is working in the strip's own
        // coordinates, so scrolling underneath the pointer would move the tabs out from
        // under the hand rearranging them, and the dragged tab is on screen by
        // construction, because the user has just pressed on it.
        if (_bound is null || _dragging || _tabScrollQueued)
        {
            return;
        }

        _tabScrollQueued = true;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ShowActiveTab));
    }

    /// <summary>
    /// Scrolls the strip the shortest distance that puts the tab in front on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Doing nothing when the tab is already on screen is most of the cost of this
    /// feature rather than an optimisation on top of it: this runs on every bind and on
    /// every change of the strip's width, and the overwhelming majority of those find a
    /// tab that is already visible.
    /// </para>
    /// <para>
    /// <see cref="ScrollViewer.ScrollToHorizontalOffset"/> rather than
    /// <see cref="FrameworkElement.BringIntoView()"/>. The latter raises a routed event
    /// that walks the tree looking for a scroll viewer and hands it a rectangle to
    /// resolve against its own layout: far more work than this needs, and it was part of
    /// what made the backlog above expensive enough to notice. Setting the offset is a
    /// property assignment and an arrange invalidation, which is exactly what the wheel
    /// handler above has always done. The arithmetic it costs is two comparisons, and
    /// they are the same two the visibility test needs anyway.
    /// </para>
    /// <para>
    /// Everything here is in the strip's own content coordinates: the space
    /// <see cref="TryLocate"/> works in, and the space a scroll offset is expressed in.
    /// A container's position in it does not change when the viewport scrolls, which is
    /// what makes reading it here safe.
    /// </para>
    /// </remarks>
    private void ShowActiveTab()
    {
        // First, so that a fault below cannot leave the flag set and silence every later
        // request for the life of the window.
        _tabScrollQueued = false;

        if (_bound is not { } tab || _dragging)
        {
            return;
        }

        // Nothing is off-screen, so nothing can need showing. Also guards the very first
        // layout pass, where the viewport has no width yet and the arithmetic below would
        // scroll to an offset derived from zero.
        if (TabScroller.ScrollableWidth <= 0 || TabScroller.ViewportWidth <= 0)
        {
            return;
        }

        if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement container
            || !container.IsVisible)
        {
            return;
        }

        var left = container.TransformToAncestor(TabStrip).Transform(default).X;
        var right = left + container.ActualWidth;
        var viewportLeft = TabScroller.HorizontalOffset;
        var viewportRight = viewportLeft + TabScroller.ViewportWidth;

        if (left >= viewportLeft && right <= viewportRight)
        {
            return;
        }

        // Past the left edge wins when a tab is somehow wider than the viewport, because
        // the start of a caption is worth more than the end of one.
        TabScroller.ScrollToHorizontalOffset(
            left < viewportLeft ? left : right - TabScroller.ViewportWidth);
    }

    private void OnPinTabClicked(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } tab)
        {
            _workspace.SetPinned(tab, !tab.IsPinned);
        }
    }

    private void OnRenameTabClicked(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } tab)
        {
            tab.IsRenaming = true;
        }
    }

    private void OnToggleEphemeralClicked(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } tab)
        {
            _workspace.SetEphemeral(tab, !tab.IsEphemeral);

            if (ReferenceEquals(tab, _bound))
            {
                UpdateSaveStatus();
            }
        }
    }

    private void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        if (TabFromMenu(sender) is { } tab)
        {
            Run(CloseAsync(tab));
        }
    }

    /// <summary>
    /// Reads the tab a context-menu item belongs to.
    /// </summary>
    /// <remarks>
    /// One <c>ContextMenu</c> instance is shared by every tab (only one can be open at a
    /// time, so a menu per tab would be pure waste) and it is pointed at the right tab by
    /// a binding through its placement target. Returning null rather than guessing when
    /// that binding has not produced a tab is the point: a menu item that acts on the
    /// wrong tab is far worse than one that does nothing.
    /// </remarks>
    private static BufferTab? TabFromMenu(object sender) =>
        (sender as FrameworkElement)?.DataContext as BufferTab;

    /// <summary>
    /// Finds which tab the pointer is over, and where that tab's middle is.
    /// </summary>
    /// <param name="point">The pointer, in the strip's own coordinates.</param>
    /// <param name="index">The tab's position in the strip.</param>
    /// <param name="centre">The horizontal middle of that tab, in the same coordinates.</param>
    /// <remarks>
    /// By container bounds rather than by hit testing, because during a drag the mouse is
    /// captured by the strip and every event reports the strip as its source. The panel is
    /// a plain <c>StackPanel</c>, so a container exists for every item and none of them is
    /// virtualised away underneath this. Layout runs at a higher dispatcher priority than
    /// input, so the bounds read here are always the ones on screen rather than the ones
    /// from before the previous move.
    /// </remarks>
    private bool TryLocate(Point point, out int index, out double centre)
    {
        for (var i = 0; i < _workspace.Tabs.Count; i++)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(_workspace.Tabs[i]) is not FrameworkElement container
                || !container.IsVisible)
            {
                continue;
            }

            var left = container.TransformToAncestor(TabStrip).Transform(default).X;

            if (point.X >= left && point.X < left + container.ActualWidth)
            {
                index = i;
                centre = left + (container.ActualWidth / 2);
                return true;
            }
        }

        index = -1;
        centre = 0d;
        return false;
    }

    /// <summary>
    /// True when a press landed on the tab itself rather than on something inside it.
    /// </summary>
    /// <remarks>
    /// Walks up from whatever was clicked. Reaching the tab first means the press was on
    /// its own surface; meeting a text box or a nested button on the way means it belongs
    /// to the rename caption or the close button, and neither of those starts a drag.
    /// </remarks>
    private static bool LandedOnTabSurface(object? originalSource, DependencyObject tab)
    {
        for (var node = originalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, tab))
            {
                return true;
            }

            if (node is TextBoxBase or ButtonBase)
            {
                return false;
            }
        }

        return false;
    }
}
