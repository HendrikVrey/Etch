using System.Windows;
using Etch.App.Diagnostics;
using Etch.App.Updates;
using Etch.Persistence.Model;
using Etch.Persistence.Settings;

namespace Etch.App.Views;

/// <summary>
/// Telling the user a new version of Etch is out, and installing it when they say so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in, and asked once.</b> Etch's plan promised no network at all, "or opt-in and
/// explicit". So until the user answers, nothing is sent: the first launch of a build that
/// has this code shows a strip asking whether Etch may check, and the answer is kept in
/// <c>settings.json</c> and can be changed in <c>Ctrl+,</c>.
/// </para>
/// <para>
/// <b>A strip, not a dialog</b>, for the reason the find bar and the settings panel are not
/// dialogs: the plan bans modals, and an update is never urgent enough to take the
/// keyboard away from the text.
/// </para>
/// <para>
/// <b>At most once a day</b>, when Etch starts or is brought to the front, never on a
/// timer: an Etch left open all week costs nothing in the background. A failed automatic
/// check says nothing (it is written to the diagnostic log); one the user asked for says
/// what went wrong.
/// </para>
/// <para>
/// <b>The installer runs visibly</b>, Hendrik's choice: once it is downloaded and its
/// checksum matches the one GitHub published, it is started as it would be from a browser,
/// and Etch closes through its ordinary shutdown so every tab is written first.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How long after startup the first automatic check waits.</summary>
    /// <remarks>So the check never competes with restoring the session.</remarks>
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often a failed automatic check may be retried.
    /// </summary>
    /// <remarks>
    /// A success is remembered for a day in <c>update.json</c>; a failure only here, so an
    /// Etch started offline tries again later, but not every time it is brought forward.
    /// </remarks>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly UpdateStateStore _updateStore;
    private UpdateState _updateState = UpdateState.Empty;
    private UpdateBarMode _updateBarMode;
    private LatestRelease? _offeredRelease;
    private CancellationTokenSource? _updateWork;
    private DateTimeOffset? _lastUpdateAttempt;
    private bool _updatesStarted;

    private enum UpdateBarMode
    {
        Hidden,
        Asking,
        Offering,
        Downloading,
    }

    private bool IsUpdateWorkRunning => _updateWork is not null;

    /// <summary>
    /// Starts the update behaviour, once startup has settled.
    /// </summary>
    /// <remarks>
    /// Called from <see cref="OnStartupCompleted"/>, after the settings have been read, so
    /// whether the user has answered is known.
    /// </remarks>
    private async void StartUpdates()
    {
        try
        {
            _updateState = await _updateStore.LoadAsync().ConfigureAwait(true);
            _updatesStarted = true;

            switch (_settings.CheckForUpdates)
            {
                case null:
                    ShowUpdateBar(UpdateBarMode.Asking);
                    break;

                case true when UpdateSchedule.IsDue(_updateState.LastCheckedUtc, DateTimeOffset.UtcNow):
                    await Task.Delay(FirstCheckDelay).ConfigureAwait(true);
                    CheckForUpdatesIfDue();
                    break;
            }
        }
        catch (Exception ex)
        {
            // Never fatal: an editor that will not start because GitHub is unreachable
            // would be the worst possible trade.
            DiagnosticLog.WriteFailure("update-start", ex);
        }
    }

    /// <summary>Runs an automatic check if one is due and the user has allowed it.</summary>
    /// <remarks>Also called on activation, which is how an Etch left open all week still hears of a release.</remarks>
    private void CheckForUpdatesIfDue()
    {
        var now = DateTimeOffset.UtcNow;

        if (!_updatesStarted
            || _shutdownStarted
            || _settings.CheckForUpdates != true
            || IsUpdateWorkRunning
            || _updateBarMode != UpdateBarMode.Hidden
            || !UpdateSchedule.IsDue(_updateState.LastCheckedUtc, now)
            || (_lastUpdateAttempt is { } attempt && now - attempt < RetryAfterFailure))
        {
            return;
        }

        Run(CheckForUpdatesAsync(userAsked: false));
    }

    /// <inheritdoc />
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        CheckForUpdatesIfDue();
    }

    /// <summary>Asks GitHub for the newest release, and offers it when it is newer.</summary>
    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (IsUpdateWorkRunning)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _updateWork = work;
        _lastUpdateAttempt = DateTimeOffset.UtcNow;

        if (userAsked)
        {
            ShowMessage("Checking GitHub for a new version of Etch…", null);
        }

        try
        {
            await Task.Run(() => UpdateInstaller.SweepOldDownloads(DateTimeOffset.UtcNow), work.Token).ConfigureAwait(true);

            LatestRelease latest;

            using (var client = new UpdateClient())
            {
                latest = await client.GetLatestAsync(work.Token).ConfigureAwait(true);
            }

            _updateState = _updateState with { LastCheckedUtc = DateTimeOffset.UtcNow };
            SaveUpdateState();

            var current = ReleaseVersion.Current;

            if (UpdateSchedule.ShouldOffer(current, latest.Version, _updateState.SkippedVersion, userAsked))
            {
                _offeredRelease = latest;
                ShowUpdateBar(UpdateBarMode.Offering);
            }
            else if (userAsked)
            {
                ShowMessage(
                    latest.Version >= current
                        ? $"Etch {current} is the newest version."
                        : $"Etch {current} is newer than the newest release ({latest.Version}).",
                    StartupResultDuration);
            }
        }
        catch (UpdateCheckException ex)
        {
            DiagnosticLog.Write($"Etch - update check: {ex.Message}");

            if (userAsked)
            {
                ShowMessage(ex.Message, StartupResultDuration);
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        finally
        {
            _updateWork = null;
        }
    }

    /// <summary>Downloads the offered installer, verifies it, starts it and closes Etch.</summary>
    private async Task DownloadAndInstallAsync(LatestRelease release)
    {
        if (IsUpdateWorkRunning)
        {
            return;
        }

        using var work = new CancellationTokenSource();
        _updateWork = work;

        ShowUpdateBar(UpdateBarMode.Downloading);
        SetText(UpdateBarText, $"Downloading Etch {release.Version} ({release.DescribeSize()})…");

        var progress = new Progress<double>(fraction =>
        {
            if (_updateBarMode == UpdateBarMode.Downloading)
            {
                SetText(UpdateBarText, $"Downloading Etch {release.Version}… {fraction:P0}");
            }
        });

        string? directory = null;

        try
        {
            directory = UpdateInstaller.CreateDownloadDirectory();

            string installer;

            using (var client = new UpdateClient())
            {
                installer = await client.DownloadAsync(release, directory, progress, work.Token).ConfigureAwait(true);
            }

            // The window may have been closed while the last bytes arrived: the flush can
            // take seconds with large tabs, and somebody who closed Etch did not ask for an
            // installer to appear afterwards.
            if (_shutdownStarted)
            {
                UpdateInstaller.Discard(directory);
                return;
            }

            UpdateInstaller.Launch(installer, release.Sha256);

            // The installer is up; Etch steps aside through its ordinary close, which
            // writes every tab first. The installer's Restart Manager would otherwise have
            // to ask the user to close Etch, and a flush it forced would be a flush cut short.
            DiagnosticLog.Write($"Etch - started the installer for {release.Version}.");
            HideUpdateBar();
            ShowMessage($"The Etch {release.Version} installer is open. Etch is closing so it can be updated…", null);
            Close();
        }
        catch (Exception ex) when (ex is UpdateCheckException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            if (directory is not null)
            {
                UpdateInstaller.Discard(directory);
            }

            if (ex is OperationCanceledException)
            {
                // Cancelled by the user or by the window closing: offered again next time.
                if (!_shutdownStarted)
                {
                    ShowUpdateBar(UpdateBarMode.Offering);
                }

                return;
            }

            DiagnosticLog.WriteFailure("update-download", ex);
            ShowUpdateBar(UpdateBarMode.Offering);
            ShowMessage(ex.Message, StartupResultDuration);
        }
        finally
        {
            _updateWork = null;
        }
    }

    /// <summary>Records the answer to "may Etch check?", from the strip or from the settings panel.</summary>
    private void SetUpdateChecks(bool allowed)
    {
        ApplySettings(_settings with { CheckForUpdates = allowed });

        if (_updateBarMode == UpdateBarMode.Asking)
        {
            HideUpdateBar();
        }

        // Switching off stops a check in flight and takes down an offer already showing. A
        // download is left alone: somebody pressed Update for that one, and it has its own
        // Cancel.
        if (!allowed)
        {
            if (_updateBarMode == UpdateBarMode.Offering)
            {
                HideUpdateBar();
            }

            if (_updateBarMode != UpdateBarMode.Downloading)
            {
                _updateWork?.Cancel();
            }
        }

        if (allowed)
        {
            CheckForUpdatesIfDue();
        }
    }

    private void SaveUpdateState() => Run(SaveUpdateStateAsync(_updateState));

    private async Task SaveUpdateStateAsync(UpdateState state)
    {
        try
        {
            await _updateStore.SaveAsync(state).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Costs one extra check tomorrow, or a skipped version offered again; not
            // worth interrupting anyone for.
            DiagnosticLog.WriteFailure("update-state-save", ex);
        }
    }

    /// <summary>Puts the strip into <paramref name="mode"/> and fills in its words and buttons.</summary>
    private void ShowUpdateBar(UpdateBarMode mode)
    {
        if (_shutdownStarted)
        {
            return;
        }

        _updateBarMode = mode;

        switch (mode)
        {
            case UpdateBarMode.Asking:
                SetText(
                    UpdateBarText,
                    "Can Etch check GitHub once a day for a new version? Only the version number is asked for: none of your text leaves this machine.");
                SetUpdateButtons("Yes, check for updates", "No", null);
                break;

            case UpdateBarMode.Offering when _offeredRelease is { } release:
                SetText(
                    UpdateBarText,
                    $"Etch {release.Version} is available. You have {ReleaseVersion.Current}.");
                SetUpdateButtons("Update", "Later", "Skip this version");
                break;

            case UpdateBarMode.Downloading:
                SetUpdateButtons(null, "Cancel", null);
                break;

            default:
                HideUpdateBar();
                return;
        }

        UpdateBar.Visibility = Visibility.Visible;
    }

    private void HideUpdateBar()
    {
        _updateBarMode = UpdateBarMode.Hidden;
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private void SetUpdateButtons(string? primary, string? secondary, string? tertiary)
    {
        Configure(UpdatePrimaryButton, primary);
        Configure(UpdateSecondaryButton, secondary);
        Configure(UpdateTertiaryButton, tertiary);

        static void Configure(System.Windows.Controls.Button button, string? label)
        {
            button.Content = label;
            button.Visibility = label is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnUpdatePrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_updateBarMode)
        {
            case UpdateBarMode.Asking:
                SetUpdateChecks(true);
                break;

            case UpdateBarMode.Offering when _offeredRelease is { } release:
                Run(DownloadAndInstallAsync(release));
                break;
        }
    }

    private void OnUpdateSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_updateBarMode)
        {
            case UpdateBarMode.Asking:
                SetUpdateChecks(false);
                ShowMessage("Etch will not check for updates. Turn it on in Settings (Ctrl+,) whenever you like.", StartupResultDuration);
                break;

            case UpdateBarMode.Offering:
                // Later: the check already ran today, so it is offered again tomorrow.
                HideUpdateBar();
                break;

            case UpdateBarMode.Downloading:
                _updateWork?.Cancel();
                break;
        }

        Editor.Focus();
    }

    private void OnUpdateTertiaryClick(object sender, RoutedEventArgs e)
    {
        if (_updateBarMode == UpdateBarMode.Offering && _offeredRelease is { } release)
        {
            _updateState = _updateState with { SkippedVersion = release.Version.ToString() };
            SaveUpdateState();
            HideUpdateBar();
            ShowMessage($"Etch {release.Version} skipped. Settings (Ctrl+,) has Check now if you change your mind.", StartupResultDuration);
        }

        Editor.Focus();
    }

    /// <summary>Handles the settings panel's update checkbox.</summary>
    private void OnUpdateCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents || !IsSettingsOpen)
        {
            return;
        }

        SetUpdateChecks(UpdateCheckToggle.IsChecked == true);
    }

    /// <summary>Handles the settings panel's Check now button.</summary>
    private void OnCheckNowClick(object sender, RoutedEventArgs e)
    {
        CloseSettings();

        if (IsUpdateWorkRunning)
        {
            ShowMessage("Etch is already talking to GitHub.", null);
            return;
        }

        Run(CheckForUpdatesAsync(userAsked: true));
    }

    /// <summary>Stops anything in flight; called as soon as the window starts closing, and again as it closes.</summary>
    private void StopUpdates() => _updateWork?.Cancel();
}
