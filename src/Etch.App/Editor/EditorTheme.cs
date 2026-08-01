using System.Windows;
using System.Windows.Media;
using Etch.App.Startup;
using ICSharpCode.AvalonEdit;
using Wpf.Ui.Appearance;

namespace Etch.App.Editor;

/// <summary>
/// Keeps the editor's selection and current-line colours matched to Windows.
/// </summary>
/// <remarks>
/// <para>
/// The colours themselves are worked out by <see cref="EditorColours"/>, which is pure and
/// tested. This type does the wiring only: resolve the theme and the accent, push the
/// result onto the control, and do it again when Windows changes its mind.
/// </para>
/// <para>
/// The theme is read from the registry through <see cref="SystemThemeReader"/> rather than
/// from <c>ApplicationThemeManager.GetAppTheme</c>. Etch constructs its window before it
/// applies a theme, so the library's cached answer is <c>Unknown</c> for part of startup,
/// and the registry is both the truth and already on the startup path.
/// </para>
/// </remarks>
internal sealed class EditorTheme : IDisposable
{
    /// <summary>Used when Windows will not say what the accent is.</summary>
    /// <remarks>The Windows 11 default, so the fallback looks like a choice rather than a failure.</remarks>
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    private readonly TextEditor _editor;
    private bool _disposed;

    internal EditorTheme(TextEditor editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        ApplicationThemeManager.Changed += OnThemeChanged;

        Refresh();
    }

    /// <summary>
    /// Recomputes the colours and applies them.
    /// </summary>
    /// <remarks>
    /// Called explicitly after the application applies its theme, and that call is load
    /// bearing rather than defensive. <c>ApplicationThemeManager.Apply</c> raises
    /// <c>Changed</c> only when the resource dictionary genuinely swapped, so a launch on
    /// a machine whose theme already matches the dictionary compiled into <c>App.xaml</c>
    /// raises nothing at all — which is exactly the launch this would otherwise leave with
    /// AvalonEdit's own colours. Idempotent, so calling it twice costs a few microseconds
    /// and changes nothing.
    /// </remarks>
    internal void Refresh() => Apply(Resolve());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ApplicationThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>
    /// Reacts to Windows switching between light and dark.
    /// </summary>
    /// <remarks>
    /// Both arguments are deliberately ignored in favour of re-reading the current state:
    /// the accent is read afresh below, and the theme comes from the registry, so this
    /// handler and the explicit call above cannot disagree about anything.
    /// <para>
    /// Known limit: changing the accent colour <em>without</em> changing light or dark does
    /// not reach here, because the dictionary does not swap and <c>Changed</c> is not
    /// raised. The selection stays fully legible — its contrast is guaranteed by
    /// construction — but it keeps the previous accent until the next theme change or the
    /// next launch. A second mechanism listening for <c>WM_SETTINGCHANGE</c> would close
    /// it, and two mechanisms for one job is the defect §22 of the plan was written about.
    /// </para>
    /// </remarks>
    private void OnThemeChanged(ApplicationTheme theme, Color accent) => Refresh();

    private EditorColourScheme Resolve() =>
        SystemParameters.HighContrast
            ? EditorColours.HighContrast()
            : EditorColours.Build(SystemThemeReader.Detect() == SystemThemeMode.Dark, Accent());

    /// <summary>Reads the system accent, with somewhere to fall back to at each step.</summary>
    /// <remarks>
    /// WPF-UI populates <c>SystemAccent</c> from inside <c>ApplicationThemeManager.Apply</c>,
    /// so it is set by the time the application asks for a refresh — but it is a plain
    /// static that is simply unset before then, and an unset one is fully transparent
    /// rather than absent. Alpha is therefore what "no answer" looks like, at both steps.
    /// </remarks>
    private static Color Accent()
    {
        if (ApplicationAccentColorManager.SystemAccent is { A: > 0 } accent)
        {
            return accent;
        }

        return SystemParameters.WindowGlassColor is { A: > 0 } glass ? glass : FallbackAccent;
    }

    private void Apply(EditorColourScheme scheme)
    {
        var area = _editor.TextArea;

        area.SelectionBrush = scheme.SelectionBackground;
        area.SelectionBorder = scheme.SelectionBorder;
        area.SelectionForeground = scheme.SelectionForeground;

        // A local value, which outranks the setters in AvalonEdit's own TextArea style —
        // that style is where the defaults being replaced here come from.
        var view = area.TextView;

        view.CurrentLineBackground = scheme.CurrentLineBackground;
        view.CurrentLineBorder = scheme.CurrentLineBorder;
    }
}
