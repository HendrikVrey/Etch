using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Etch.App.Views;

/// <summary>
/// What happens to text on its way into the editor from the clipboard or a drop.
/// </summary>
/// <remarks>
/// <para>
/// AvalonEdit owns pasting, and it is left to: full-line and rectangular pastes, line-ending
/// normalisation and the undo grouping all stay its own. This file only stands in the one
/// doorway it offers, <see cref="DataObject.PastingEvent"/>, which is raised for
/// <c>Ctrl+V</c>, the context menu and a drop alike, before a single character is read.
/// </para>
/// <para>
/// Two things happen there. A paste that would take the tab past the editor's size limit is
/// refused, because the limit that has always applied to opening a file had never applied
/// to anything else. And a paste containing tabs keeps them.
/// </para>
/// </remarks>
public partial class MainWindow
{
    private void InitialisePaste() => DataObject.AddPastingHandler(Editor.TextArea, OnEditorPasting);

    private void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_bound is not { Document: { } document } tab)
        {
            return;
        }

        string? text;

        try
        {
            text = PastedText(e);
        }
        catch (OutOfMemoryException)
        {
            // AvalonEdit would try the same read again and swallow the same failure, so the
            // paste would silently do nothing. Saying so is the only thing left to add.
            e.CancelCommand();
            ShowMessage("That is too large to paste.", null);
            return;
        }

        if (text is null)
        {
            return;
        }

        // A drop inserts at the caret, so the selection is not what it replaces.
        var replaced = e.IsDragDrop ? 0 : Editor.SelectionLength;

        if (!tab.Fits((long)document.TextLength - replaced + text.Length, out var refusal))
        {
            e.CancelCommand();
            ShowMessage($"Nothing was pasted. {refusal}", null);
            return;
        }

        if (Editor.Options.ConvertTabsToSpaces && text.Contains('\t', StringComparison.Ordinal))
        {
            KeepTabsForThisPaste();
        }
    }

    /// <summary>
    /// The text a paste would insert, read the way AvalonEdit reads it, or null when there is none.
    /// </summary>
    /// <remarks>
    /// The same order of formats as AvalonEdit's own <c>GetTextToPaste</c>, so the length
    /// checked is the length that would land. A clipboard that will not hand its contents
    /// over is left for AvalonEdit to fail on in its own way.
    /// </remarks>
    /// <exception cref="OutOfMemoryException">The text is too large to read at all.</exception>
    private static string? PastedText(DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;

        try
        {
            var format = e.FormatToApply is { } preferred && data.GetDataPresent(preferred) ? preferred
                : data.GetDataPresent(DataFormats.UnicodeText) ? DataFormats.UnicodeText
                : data.GetDataPresent(DataFormats.Text) ? DataFormats.Text
                : null;

            return format is null ? null : data.GetData(format) as string;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stops AvalonEdit turning the tabs in this one paste into spaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConvertTabsToSpaces</c> is on so that the Tab key indents with spaces, and
    /// AvalonEdit applies it to pasted text as well: every tab in a paste became four
    /// spaces. For tab-separated values, a Makefile or Go source that is not a style
    /// preference, it is corruption, and the transforms that would run next (split, sort,
    /// dedupe) then see columns that are not there. "Tabs to spaces" exists as a transform
    /// for the person who wants it, and it converts leading tabs only, for exactly this
    /// reason.
    /// </para>
    /// <para>
    /// The paste is applied synchronously once this event returns, so the option is put back
    /// at <see cref="DispatcherPriority.Send"/>, which runs before any input is dispatched:
    /// the next Tab keypress still indents with spaces.
    /// </para>
    /// </remarks>
    private void KeepTabsForThisPaste()
    {
        var options = Editor.Options;

        options.ConvertTabsToSpaces = false;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, () => options.ConvertTabsToSpaces = true);
    }
}
