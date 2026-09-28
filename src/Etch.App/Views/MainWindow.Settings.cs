using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Etch.App.Diagnostics;
using Etch.App.Startup;
using Etch.Persistence.Model;
using Etch.Persistence.Settings;

namespace Etch.App.Views;

/// <summary>
/// The settings panel, on <c>Ctrl+,</c>.
/// </summary>
/// <remarks>
/// <para>
/// An overlay, like the palette, and for the same reason: the plan bans modals, and a
/// second window would take focus off the editor and put it back somewhere the caret has
/// to be hunted for. Collapsed until it is asked for, so it costs a template and nothing
/// else.
/// </para>
/// <para>
/// <b>Every control here changes something.</b> That was the acceptance criterion for
/// this panel rather than a pleasant property of it: a settings screen accumulates
/// knobs that do nothing faster than any other part of an application, and each one
/// teaches the user that the rest might be lying too. Retention reaches the trash sweep,
/// ligatures reach the editor's typeface, the thresholds reach the size policy every
/// open goes through, the wipe calls the one in <c>Etch.Persistence</c> that has had no
/// caller since M1, and the association checkboxes read and write the registry.
/// </para>
/// <para>
/// Changes apply on the spot rather than behind an OK button. There is nothing to
/// confirm and nothing to cancel, which is the promise the rest of the application makes
/// about saving.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>
    /// How long the wipe confirmation stays armed.
    /// </summary>
    /// <remarks>
    /// The wipe is the one irreversible action in Etch, so it is the one place a second
    /// press is required. Long enough to read the warning, short enough that the armed
    /// state cannot be inherited by whoever sits down next: the same failure the
    /// <c>Ctrl+K</c> prefix had, fixed the same way, with a timer that puts the button
    /// back rather than a check that silently re-arms it.
    /// </remarks>
    private static readonly TimeSpan WipeConfirmationWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long typing stops before the settings file is written.
    /// </summary>
    /// <remarks>
    /// <see cref="Etch.Persistence.Storage.AtomicFile"/> flushes to the device on every
    /// write, and says in its own remarks that this is affordable only because the journal
    /// debounces. Settings are edited by typing into a text box, so without a debounce
    /// here, entering "100" would cost three full disk round trips.
    /// </remarks>
    private static readonly TimeSpan SettingsSaveDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>The editor typeface with ligatures, and without.</summary>
    /// <remarks>
    /// Two families rather than a typography property. <c>Typography.StandardLigatures</c>
    /// is a WPF text-formatting feature, and AvalonEdit renders its runs through its own
    /// text source, so setting it on the control does nothing to the text. Cascadia ships
    /// two families for exactly this purpose: Code has the ligatures, Mono is the same
    /// design without them. Both chains end in the same fallbacks so a machine with
    /// neither Cascadia still gets a monospace face.
    /// </remarks>
    private static readonly FontFamily LigatureFont = new("Cascadia Code, Cascadia Mono, Consolas, Courier New");

    /// <inheritdoc cref="LigatureFont" />
    private static readonly FontFamily PlainFont = new("Cascadia Mono, Consolas, Courier New");

    private EtchSettings _settings = EtchSettings.Default;
    private bool _canSaveSettings = true;
    private bool _suppressSettingsEvents;
    private DateTimeOffset? _wipeArmedAt;
    private DispatcherTimer? _wipeTimer;
    private DispatcherTimer? _settingsSaveTimer;

    /// <summary>
    /// A settings-load complaint waiting for somewhere to be shown.
    /// </summary>
    /// <remarks>
    /// Held rather than shown immediately. Settings are read before the session restore,
    /// which is before <see cref="OnStartupCompleted"/>, so a message shown here would be
    /// overwritten by the startup summary a few milliseconds later, and "your settings file
    /// is broken" would flash past unread.
    /// </remarks>
    private string? _pendingSettingsNotice;

    /// <summary>Opens the settings panel.</summary>
    /// <remarks>
    /// Typed as <see cref="ICommand"/> rather than <c>RelayCommand</c> for the reason
    /// every other command on this window is: <c>RelayCommand</c> is internal, and a
    /// public member exposing an internal type is CS0053.
    /// </remarks>
    public ICommand OpenSettingsCommand { get; private set; } = null!;

    /// <summary>True while the settings overlay is up.</summary>
    private bool IsSettingsOpen => SettingsOverlay.Visibility == Visibility.Visible;

    /// <summary>
    /// Adopts the settings read at startup.
    /// </summary>
    /// <remarks>
    /// Called by the application once the settings file has been read and before the
    /// first frame. The typeface is applied here rather than lazily because a font change
    /// after the editor is visible is a visible reflow; the size thresholds go to the
    /// workspace because it evaluates them on every open.
    /// </remarks>
    internal void ApplyStartupSettings(SettingsLoadResult loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);

        _settings = loaded.Settings;
        _canSaveSettings = loaded.CanSave;

        Editor.FontFamily = _settings.Ligatures ? LigatureFont : PlainFont;
        _workspace.ApplySettings(_settings);

        _pendingSettingsNotice = loaded.Notice;
    }

    /// <summary>Shows the settings complaint, if there was one, once startup has settled.</summary>
    private void ShowPendingSettingsNotice()
    {
        if (_pendingSettingsNotice is { } notice)
        {
            _pendingSettingsNotice = null;
            ShowMessage(notice, StartupResultDuration);
        }
    }

    /// <summary>
    /// Builds the settings command.
    /// </summary>
    /// <remarks>
    /// Called from the constructor before <c>InitializeComponent</c>, like every other
    /// command: the status bar's settings gear binds to this property, and a binding
    /// evaluated against a null command is a control that silently does nothing.
    /// </remarks>
    private void InitialiseSettings() => OpenSettingsCommand = new RelayCommand(OpenSettings);

    private void OpenSettings()
    {
        if (IsSettingsOpen)
        {
            return;
        }

        LoadSettingsIntoPanel();
        SettingsOverlay.Visibility = Visibility.Visible;

        // After a layout pass, exactly as the palette does it. Focusing an element in the
        // same call stack that made its parent visible does not stick, and a settings
        // panel that opens without focus is worse than one you have to click, because the
        // keyboard is still pointed at the editor behind the backdrop and typing goes into
        // a document the user cannot see.
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsSettingsOpen)
                {
                    _ = RetentionInput.Focus();
                    RetentionInput.SelectAll();
                }
            }));
    }

    private void CloseSettings()
    {
        if (!IsSettingsOpen)
        {
            return;
        }

        SettingsOverlay.Visibility = Visibility.Collapsed;
        DisarmWipe();

        // Anything the debounce is still holding goes now. Closing the panel is the
        // moment the user considers the change made, and a setting that survived only
        // until they quit would be the one bug this panel cannot afford.
        FlushPendingSettingsSave();

        // Focus goes back where it came from, or the next keystroke lands nowhere.
        Editor.Focus();
    }

    /// <summary>
    /// Fills the panel from the current settings and from the registry.
    /// </summary>
    /// <remarks>
    /// The association checkboxes are read from the registry every time the panel opens
    /// rather than cached. Windows' own Default Apps page can change them while Etch is
    /// running, and a checkbox that disagrees with the system is worse than no checkbox.
    /// </remarks>
    private void LoadSettingsIntoPanel()
    {
        _suppressSettingsEvents = true;

        try
        {
            RetentionInput.Text = _settings.TrashRetentionDays.ToString(CultureInfo.CurrentCulture);
            LigatureToggle.IsChecked = _settings.Ligatures;

            ReducedThresholdInput.Text = SettingsThresholds.Format(_settings.ReducedThresholdBytes);
            PlainTextThresholdInput.Text = SettingsThresholds.Format(_settings.PlainTextThresholdBytes);
            HardCeilingInput.Text = SettingsThresholds.Format(_settings.HardCeilingBytes);

            foreach (var checkBox in AssociationCheckBoxes())
            {
                checkBox.IsChecked = checkBox.Tag is string extension && FileAssociations.IsHonoured(extension);
            }

            // Read fresh for the same reason, and with one of its own: the verb can be
            // registered by the installer, removed by hand, or left pointing at a copy of
            // Etch that has since moved. IsOpenWithVerbPresent answers "would clicking it
            // run this executable", not "did somebody write the key".
            OpenWithVerbCheckBox.IsChecked = FileAssociations.IsOpenWithVerbPresent();

            SetText(SettingsValidation, string.Empty);
            SetText(
                SettingsNotice,
                _canSaveSettings
                    ? string.Empty
                    : "A newer version of Etch wrote your settings file. Changes made here apply to this session but will not be saved over it.");

            // Arming does not survive a close and reopen: somebody who left and came back
            // has not read the warning they would otherwise walk straight through.
            DisarmWipe();
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    /// <summary>The association checkboxes, each tagged with the extension it governs.</summary>
    /// <remarks>
    /// Every child of <c>AssociationList</c> is expected to carry an extension in its
    /// <c>Tag</c>, because that is what the loop above reads. The "Open with Etch"
    /// checkbox is therefore deliberately outside that panel: see the comment beside it
    /// in <c>MainWindow.xaml</c>.
    /// </remarks>
    private IEnumerable<CheckBox> AssociationCheckBoxes() => AssociationList.Children.OfType<CheckBox>();

    /// <summary>Handles the three size boxes and the retention box.</summary>
    /// <remarks>
    /// Declared with <see cref="TextChangedEventArgs"/> rather than the
    /// <see cref="RoutedEventArgs"/> the toggles use, so the delegate the XAML compiler
    /// constructs matches exactly instead of relying on method-group contravariance.
    /// </remarks>
    private void OnSettingTextChanged(object sender, TextChangedEventArgs e) => ApplyPanelValues();

    /// <summary>Handles the ligature toggle.</summary>
    private void OnSettingToggled(object sender, RoutedEventArgs e) => ApplyPanelValues();

    /// <summary>
    /// Reads every input, applies what is valid, and says what was rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One handler for all of them rather than one per control. The three thresholds are
    /// only meaningful as an ascending set and have to be validated together anyway; a
    /// per-control handler would have to reach for its two siblings to do it, which is
    /// the same code written three times.
    /// </para>
    /// <para>
    /// A field that does not parse leaves its setting alone rather than resetting it.
    /// Somebody halfway through typing "12" has typed "1", and an editor that snapped the
    /// value back to a default on every keystroke would be unusable. Nothing is lost by
    /// waiting: the text stays where the user put it, and the panel says which part of it
    /// is not yet usable.
    /// </para>
    /// </remarks>
    private void ApplyPanelValues()
    {
        if (_suppressSettingsEvents || !IsSettingsOpen)
        {
            return;
        }

        var complaints = new List<string>(2);

        // Read before the `with` rather than inside it. An out-variable declaration in an
        // object initialiser is a corner of the language not worth relying on, and the
        // two-line version is clearer regardless.
        var retentionDays = ReadRetention(out var retentionIsUsable);

        var candidate = _settings with
        {
            TrashRetentionDays = retentionDays,
            Ligatures = LigatureToggle.IsChecked == true,
        };

        if (!retentionIsUsable)
        {
            complaints.Add(
                string.Create(
                    CultureInfo.CurrentCulture,
                    $"Retention must be a whole number of days between 0 and {EtchSettings.MaxRetentionDays}."));
        }

        // Only when one of the three boxes has actually been typed in. Reading them back
        // otherwise would rewrite every threshold at display precision the moment somebody
        // edited the retention field beside them (102,400 bytes becoming 102,445) because
        // Format cannot represent an arbitrary byte count in four decimal places of MB.
        // All three are taken together or none is, so a mixture can never produce a set
        // that fails to ascend.
        if (ThresholdsWereEdited())
        {
            if (SettingsThresholds.TryParse(
                ReducedThresholdInput.Text,
                PlainTextThresholdInput.Text,
                HardCeilingInput.Text,
                out var reduced,
                out var plainText,
                out var ceiling))
            {
                candidate = candidate with
                {
                    ReducedThresholdBytes = reduced,
                    PlainTextThresholdBytes = plainText,
                    HardCeilingBytes = ceiling,
                };
            }
            else
            {
                complaints.Add("Sizes must increase from top to bottom, and sit between 0.0625 MB and 4096 MB.");
            }
        }

        SetText(
            SettingsValidation,
            complaints.Count == 0
                ? string.Empty
                : string.Join("  ", complaints) + "  The last usable values are still in effect.");

        ApplySettings(candidate.Sanitised());
    }

    /// <summary>
    /// Whether any of the three size boxes still holds exactly what the panel wrote into it.
    /// </summary>
    /// <remarks>
    /// One answer for all three rather than one each. A per-box decision would let a
    /// touched box and two untouched ones combine into a set that no longer ascends,
    /// which <c>DocumentSizePolicy</c> throws on, and there is no reading of "the user
    /// edited the thresholds" under which two of them should keep sub-display precision
    /// while the third does not.
    /// </remarks>
    private bool ThresholdsWereEdited() =>
        !SettingsThresholds.IsUnchanged(ReducedThresholdInput.Text, _settings.ReducedThresholdBytes)
        || !SettingsThresholds.IsUnchanged(PlainTextThresholdInput.Text, _settings.PlainTextThresholdBytes)
        || !SettingsThresholds.IsUnchanged(HardCeilingInput.Text, _settings.HardCeilingBytes);

    /// <summary>
    /// Reads the retention box, reporting whether it held a usable value.
    /// </summary>
    /// <remarks>
    /// The flag is the point. Silently keeping the previous value is right, see
    /// <see cref="ApplyPanelValues"/>, but doing it without saying so is how a settings
    /// box comes to look broken, and every other rejected input in this panel explains
    /// itself.
    /// </remarks>
    private int ReadRetention(out bool isUsable)
    {
        isUsable = int.TryParse(RetentionInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var days)
            && days is >= 0 and <= EtchSettings.MaxRetentionDays;

        return isUsable ? days : _settings.TrashRetentionDays;
    }

    /// <summary>Applies a settings change everywhere it is read, then queues the write.</summary>
    private void ApplySettings(EtchSettings settings)
    {
        var previous = _settings;

        if (previous == settings)
        {
            return;
        }

        _settings = settings;

        if (previous.Ligatures != settings.Ligatures)
        {
            Editor.FontFamily = settings.Ligatures ? LigatureFont : PlainFont;
        }

        // Retention is read on every close and the size policy on every open, so both
        // take effect immediately rather than at the next launch. Only the write to disk
        // is deferred.
        _workspace.ApplySettings(settings);

        QueueSettingsSave();
    }

    /// <summary>
    /// Restarts the debounce that writes the settings file.
    /// </summary>
    /// <remarks>
    /// Created on first use and one-shot, on the same discipline as every other timer in
    /// this window: an Etch that has been open all day without its settings being touched
    /// has no timer object at all, which is what the plan's 0% idle CPU target requires.
    /// </remarks>
    private void QueueSettingsSave()
    {
        if (!_canSaveSettings)
        {
            return;
        }

        _settingsSaveTimer ??= CreateSettingsSaveTimer();

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private DispatcherTimer CreateSettingsSaveTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = SettingsSaveDebounce,
        };

        timer.Tick += (_, _) =>
        {
            // Stopped first, so a failure below cannot leave a timer writing the settings
            // file for ever.
            timer.Stop();
            Run(SaveSettingsAsync(_settings));
        };

        return timer;
    }

    /// <summary>Writes anything the debounce is still holding, immediately.</summary>
    /// <remarks>
    /// Fire-and-forget through <see cref="Run"/>, which is right for the panel-closing
    /// case: the user is still in the application and a failure has somewhere to be
    /// reported. The shutdown path wants <see cref="FlushPendingSettingsSaveAsync"/>
    /// instead, because there it has to be waited for.
    /// </remarks>
    private void FlushPendingSettingsSave()
    {
        if (TakePendingSettingsSave() is { } settings)
        {
            Run(SaveSettingsAsync(settings));
        }
    }

    /// <summary>Writes anything the debounce is still holding, and waits for it.</summary>
    private Task FlushPendingSettingsSaveAsync() =>
        TakePendingSettingsSave() is { } settings ? SaveSettingsAsync(settings) : Task.CompletedTask;

    /// <summary>
    /// Stops the debounce and returns what it was about to write, or null if nothing was.
    /// </summary>
    /// <remarks>
    /// One place decides, so the two flush paths cannot disagree about whether a write is
    /// outstanding, and stopping the timer here means neither of them can leave it to
    /// fire a second write afterwards.
    /// </remarks>
    private EtchSettings? TakePendingSettingsSave()
    {
        if (_settingsSaveTimer is not { IsEnabled: true } timer)
        {
            return null;
        }

        timer.Stop();
        return _settings;
    }

    /// <summary>
    /// Writes the settings file.
    /// </summary>
    /// <remarks>
    /// Started rather than awaited, because a settings write must never make a checkbox
    /// feel slow. <see cref="Run"/> is what turns a failure into a visible message
    /// instead of an unobserved task exception. Overlapping writes cannot reorder on
    /// disk: <see cref="SettingsStore"/> serialises them.
    /// </remarks>
    private async Task SaveSettingsAsync(EtchSettings settings)
    {
        try
        {
            await _settingsStore.SaveAsync(settings).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reported, not thrown. The setting is already in force for this session;
            // what has been lost is that it would still be in force tomorrow, and a
            // read-only profile is something only the user can do anything about.
            DiagnosticLog.WriteFailure("settings-save", ex);
            ShowMessage($"That setting is active now but could not be saved: {ex.Message}", null);
        }
    }

    /// <summary>Handles one of the file-association checkboxes.</summary>
    private void OnAssociationChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents || sender is not CheckBox { Tag: string extension } checkBox)
        {
            return;
        }

        var wanted = checkBox.IsChecked == true;

        switch (FileAssociations.Set(extension, wanted))
        {
            case AssociationResult.Applied:
                ShowMessage(
                    wanted ? $"Etch now opens {extension} files." : $"Etch no longer opens {extension} files.",
                    null);
                break;

            case AssociationResult.OverriddenByWindows:
                // Not a failure. The checkbox is corrected rather than left showing a
                // state Windows is not honouring, and the message names the page where
                // the choice can actually be made: the user cannot fix this from inside
                // Etch and there is no point implying otherwise.
                SetChecked(checkBox, false);

                ShowMessage(
                    $"Etch is now in the \"Open with\" list for {extension}, but Windows keeps your existing default. "
                    + "Change it in Settings → Apps → Default apps.",
                    StartupResultDuration);
                break;

            case AssociationResult.Failed:
                SetChecked(checkBox, !wanted);
                ShowMessage($"Etch could not change the {extension} association.", null);
                break;
        }
    }

    /// <summary>Handles the "Open with Etch" right-click verb checkbox.</summary>
    /// <remarks>
    /// Separate from <see cref="OnAssociationChanged"/> because a shell verb is not an
    /// extension: it has no <c>Tag</c>, nothing arbitrates it the way <c>UserChoice</c>
    /// arbitrates a file type, and it therefore has no
    /// <see cref="AssociationResult.OverriddenByWindows"/> case to handle.
    /// </remarks>
    private void OnOpenWithVerbChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents || sender is not CheckBox checkBox)
        {
            return;
        }

        var wanted = checkBox.IsChecked == true;

        if (FileAssociations.SetOpenWithVerb(wanted) == AssociationResult.Failed)
        {
            SetChecked(checkBox, !wanted);
            ShowMessage("Etch could not change the right-click menu.", null);

            return;
        }

        ShowMessage(
            wanted
                ? "\"Open with Etch\" is now on the right-click menu."
                : "\"Open with Etch\" has been removed from the right-click menu.",
            null);
    }

    /// <summary>
    /// Sets a checkbox without its handler running.
    /// </summary>
    /// <remarks>
    /// Correcting a checkbox from inside its own handler re-raises <c>Checked</c> or
    /// <c>Unchecked</c>, which would take the registry action a second time and, for the
    /// failure arm, in the opposite direction.
    /// </remarks>
    private void SetChecked(CheckBox checkBox, bool value)
    {
        _suppressSettingsEvents = true;

        try
        {
            checkBox.IsChecked = value;
        }
        finally
        {
            _suppressSettingsEvents = false;
        }
    }

    /// <summary>
    /// Handles "Wipe all scratch data", which needs pressing twice.
    /// </summary>
    /// <remarks>
    /// The confirmation is a second press of the same button rather than a dialog, which
    /// keeps the no-modals rule and puts the warning where the pointer already is.
    /// </remarks>
    private void OnWipeClicked(object sender, RoutedEventArgs e)
    {
        if (_wipeArmedAt is not null)
        {
            DisarmWipe();
            Run(WipeAsync());

            return;
        }

        _wipeArmedAt = DateTimeOffset.UtcNow;

        WipeButton.Content = "Click again to wipe everything";
        SetText(
            WipeWarning,
            "This deletes every scratch buffer, the trash and the tab list - now, and with no way back. "
            + "It unlinks files rather than erasing them, so it is not a secure wipe.");

        _wipeTimer ??= CreateWipeTimer();

        _wipeTimer.Stop();
        _wipeTimer.Start();
    }

    /// <summary>
    /// Puts the button back once the confirmation window has passed.
    /// </summary>
    /// <remarks>
    /// A timer rather than a timestamp compared on the next click, and the difference
    /// matters: a button still reading "Click again to wipe everything" a minute later,
    /// which silently re-arms instead of firing, asks the user to press it three times
    /// with the second press doing something invisible.
    /// </remarks>
    private DispatcherTimer CreateWipeTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = WipeConfirmationWindow,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DisarmWipe();
        };

        return timer;
    }

    private void DisarmWipe()
    {
        _wipeArmedAt = null;
        _wipeTimer?.Stop();

        WipeButton.Content = "Wipe all scratch data";
        SetText(WipeWarning, string.Empty);
    }

    /// <summary>
    /// Carries out the wipe.
    /// </summary>
    /// <remarks>
    /// The panel closes first. What follows removes every tab, and a settings overlay
    /// left floating over an editor that has just emptied itself reads as a failure
    /// rather than as the success it is.
    /// </remarks>
    private async Task WipeAsync()
    {
        CloseSettings();

        var result = await _workspace.WipeAllAsync().ConfigureAwait(true);
        var files = $"{result.Deleted} file{(result.Deleted == 1 ? string.Empty : "s")}";

        ShowMessage(
            result.Failed == 0
                ? $"Wiped {files}. Nothing is left in Etch's storage."
                : $"Wiped {files}, but {result.Failed} could not be deleted - something else has them open.",
            StartupResultDuration);
    }

    /// <summary>Dismisses the panel when the dimmed backdrop is clicked.</summary>
    private void OnSettingsBackdropClicked(object sender, MouseButtonEventArgs e)
    {
        CloseSettings();
        e.Handled = true;
    }

    private void OnSettingsCloseClicked(object sender, RoutedEventArgs e) => CloseSettings();
}
