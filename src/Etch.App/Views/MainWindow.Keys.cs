using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Etch.App.Input;

namespace Etch.App.Views;

/// <summary>
/// The keyboard: one map, resolved in one place.
/// </summary>
/// <remarks>
/// <para>
/// Chords are resolved on the way <em>down</em>, in <see cref="OnPreviewKeyDown"/>,
/// rather than through WPF's <c>InputBindings</c>. That is the whole point of this file.
/// Input bindings are matched in <c>PostProcessInput</c> — after the focused control has
/// already had the key, and after <c>KeyboardNavigation</c> has had its look at Tab — so
/// whether <c>Ctrl+Tab</c> reached the window at all depended on ordering between two
/// framework components Etch does not control, and <c>Ctrl+Enter</c> was one missed
/// <c>Handled</c> away from also inserting a newline. Tunnelling from the window makes
/// both questions moot: the key is claimed before anything below can see it.
/// </para>
/// <para>
/// The cost of tunnelling is that it must be disciplined about what it takes. Two rules
/// keep it honest, and both are enforced below rather than documented and hoped for: the
/// palette owns the keyboard entirely while it is open, and a focused text box keeps
/// everything that is not a Ctrl chord.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>How long the "waiting for the second key" hint stays up.</summary>
    private static readonly TimeSpan ChordHintDuration = TimeSpan.FromSeconds(8);

    /// <summary>
    /// True once <see cref="KeyMap.ChordPrefix"/> has been pressed and its second key is
    /// still outstanding.
    /// </summary>
    /// <remarks>
    /// A bool rather than the prefix itself, because there is exactly one prefix. If a
    /// second is ever added this becomes the <c>Shortcut?</c> it wants to be, and the status
    /// message has to start naming which sequence is in progress.
    /// </remarks>
    private bool _chordPending;

    /// <summary>
    /// Abandons a half-typed sequence after <see cref="ChordHintDuration"/>.
    /// </summary>
    /// <remarks>
    /// <b>The pending state must not outlive the hint that explains it.</b> Without this, a
    /// Ctrl+K pressed and then forgotten leaves the window looking completely normal — the
    /// hint has faded — while the next key typed into the editor is silently swallowed, and
    /// the next Escape cancels the invisible sequence instead of closing the find bar. A
    /// tunnelling key handler that takes keys nobody knows it is waiting for is precisely
    /// what this whole file exists to avoid.
    /// </remarks>
    private DispatcherTimer? _chordTimer;

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.Escape && TryDismiss())
        {
            e.Handled = true;
            return;
        }

        if (TryRunShortcut(e))
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A click is the user addressing something other than the sequence they started. Ending
    /// it here means the pointer cancels a chord the way it does in every editor that has
    /// them, rather than leaving it armed behind whatever was just clicked.
    /// </remarks>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_chordPending)
        {
            EndChord("Cancelled.");
        }

        base.OnPreviewMouseDown(e);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Alt-tabbing away abandons the sequence too. The keystroke that comes back is part of
    /// whatever the user returned to do.
    /// </remarks>
    protected override void OnDeactivated(EventArgs e)
    {
        CancelChord();

        base.OnDeactivated(e);
    }

    /// <summary>
    /// Closes whatever Escape should close, innermost first.
    /// </summary>
    /// <remarks>
    /// The palette is checked before the find bar because it is drawn on top of
    /// everything: when both are open, Escape has to dismiss the thing the user is
    /// actually looking at. Escape means nothing when neither is open — the editor keeps
    /// it, which is what lets a future modal-free feature claim it in turn.
    /// </remarks>
    private bool TryDismiss()
    {
        // Checked first: a half-typed sequence is the innermost thing on screen, even though
        // the only place it appears is the status bar. Escape has to abandon it before it
        // closes anything the user can see.
        if (_chordPending)
        {
            EndChord("Cancelled.");
            return true;
        }

        if (IsPaletteOpen)
        {
            ClosePalette();
            return true;
        }

        // Above the find bar for the same reason the palette is: both are drawn over
        // everything, so when the bar is also open Escape has to dismiss the thing the
        // user is actually looking at.
        if (IsSettingsOpen)
        {
            CloseSettings();
            return true;
        }

        if (FindBar.Visibility == Visibility.Visible)
        {
            CloseFindBar();
            return true;
        }

        return false;
    }

    private bool TryRunShortcut(KeyEventArgs e)
    {
        // The palette owns the keyboard while it is up. Its query box drives Enter, the
        // arrows and Escape itself, and a global chord firing underneath would act on a
        // buffer the user cannot currently see — the palette is drawn over it.
        if (IsPaletteOpen)
        {
            // A sequence cannot survive the palette opening over it. Left pending, the next
            // key pressed after the palette closed would complete a chord the user began
            // before doing something else entirely.
            CancelChord();
            return false;
        }

        // The settings panel owns the keyboard while it is up, exactly as the palette
        // does. Every shortcut here acts on a buffer the overlay is covering — Ctrl+T
        // would silently create tabs behind it, and Ctrl+Enter would rewrite text the
        // user cannot currently see. Escape still works: TryDismiss runs before this.
        if (IsSettingsOpen)
        {
            CancelChord();
            return false;
        }

        var shortcut = Shortcut.From(e);

        if (shortcut == KeyMap.ChordPrefix)
        {
            // Held down, or pressed a second time because the user hesitated. Both re-arm
            // rather than falling through: without the first check, auto-repeat toggles the
            // pending flag at 30 Hz and whether a sequence is in progress when the key is
            // released comes down to the parity of the repeat count. Without the second, the
            // natural double-press cancels instead of restarting.
            BeginChord();
            return true;
        }

        if (_chordPending)
        {
            return CompleteChord(shortcut);
        }

        if (!KeyMap.TryResolve(shortcut, out var action))
        {
            return false;
        }

        // A text box has the keyboard: the find inputs, or a tab caption being renamed.
        // Only Ctrl chords are taken from it. Everything else — F2 included — is typing or
        // navigation that belongs to the box, and an editor that steals keys out of its own
        // search field is worse than one with no shortcuts at all.
        if (Keyboard.FocusedElement is TextBox && (shortcut.Modifiers & ModifierKeys.Control) == 0)
        {
            return false;
        }

        Execute(action, e.IsRepeat);
        return true;
    }

    /// <summary>Arms the sequence, or re-arms one already in progress.</summary>
    private void BeginChord()
    {
        _chordPending = true;

        // The hint is the only thing on screen saying a sequence is in progress, so it lasts
        // long enough to read and to act on rather than the usual few seconds.
        ShowMessage("Ctrl+K … waiting for the second key. P pins the tab, Esc cancels.", ChordHintDuration);

        _chordTimer ??= CreateChordTimer();

        // Restarted rather than left running, so a re-arm gets a full window rather than
        // whatever was left of the previous one.
        _chordTimer.Stop();
        _chordTimer.Start();
    }

    private DispatcherTimer CreateChordTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
        {
            Interval = ChordHintDuration,
        };

        timer.Tick += (_, _) =>
        {
            // One-shot, like the status-bar timer: a repeating timer is idle CPU for a state
            // that is almost never set.
            timer.Stop();

            if (_chordPending)
            {
                EndChord("Ctrl+K timed out.");
            }
        };

        return timer;
    }

    /// <summary>Ends the sequence without saying anything.</summary>
    private void CancelChord()
    {
        _chordPending = false;
        _chordTimer?.Stop();
    }

    /// <summary>
    /// Handles the key after <see cref="KeyMap.ChordPrefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// While a sequence is in progress the window owns the keyboard outright — the focus
    /// rules that let a text box keep its own keys are suspended, exactly as they are for
    /// the palette. Someone who has pressed <c>Ctrl+K</c> is addressing the application, not
    /// whatever happens to have the caret.
    /// </para>
    /// <para>
    /// An unrecognised second key ends the sequence and is <em>swallowed</em> rather than
    /// passed on. Letting it through would type a stray character into the buffer as the
    /// parting gift of a mistyped shortcut, and the message says what happened so the
    /// silence is not mysterious.
    /// </para>
    /// </remarks>
    private bool CompleteChord(Shortcut second)
    {
        // Pressing Ctrl or Shift is not the second key of anything — it is the user holding
        // the modifier down on their way to pressing it. Treating a modifier as an unknown
        // key would cancel the sequence for anyone who does not let go of Ctrl in between,
        // which is most people.
        if (IsModifierKey(second.Key))
        {
            return true;
        }

        if (KeyMap.TryResolveChord(second, out var action))
        {
            CancelChord();
            Execute(action, isRepeat: false);

            return true;
        }

        EndChord("That is not a Ctrl+K sequence.");

        return true;
    }

    private void EndChord(string message)
    {
        CancelChord();
        ShowMessage(message, null);
    }

    /// <summary>True for the keys that only ever accompany another one.</summary>
    /// <remarks>
    /// <c>Key.System</c> is not in the list: <see cref="Shortcut.From"/> has already resolved
    /// it to the real key behind Alt by the time this is reached, so an entry for it would
    /// read as though it did something.
    /// </remarks>
    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl
        or Key.RightCtrl
        or Key.LeftShift
        or Key.RightShift
        or Key.LeftAlt
        or Key.RightAlt
        or Key.LWin
        or Key.RWin;

    /// <summary>Carries out a resolved keyboard action.</summary>
    /// <remarks>
    /// The four that are not commands are the four that need an argument or a dialog.
    /// Everything else goes through the same <see cref="ICommand"/> the tab strip and the
    /// context menu use, so a shortcut and a click cannot drift into doing different
    /// things.
    /// </remarks>
    private void Execute(KeyAction action, bool isRepeat)
    {
        switch (action.Command)
        {
            case EtchCommandId.JumpToTab:
                ActivateByIndex(action.Argument);
                return;

            case EtchCommandId.OpenFile:
                OpenFileFromDialog();
                return;

            case EtchCommandId.Save:
                // Not on auto-repeat. Holding Ctrl+S would otherwise walk straight
                // through the overwrite guard: the first press refuses and arms against
                // what is on disk, and the repeat ~30 ms later finds that arm and writes,
                // so a warning nobody had time to read counts as having been read.
                if (!isRepeat)
                {
                    SaveActiveTab();
                }

                return;

            case EtchCommandId.Find:
                OpenFindBar(replaceMode: false);
                return;

            case EtchCommandId.Replace:
                OpenFindBar(replaceMode: true);
                return;

            default:
                var command = CommandFor(action.Command);

                if (command.CanExecute(null))
                {
                    command.Execute(null);
                }

                return;
        }
    }

    /// <summary>Maps an identifier onto the command that carries it out.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An identifier was added to <see cref="EtchCommandId"/> and never wired up. Thrown
    /// rather than ignored: a shortcut that silently does nothing is the failure this
    /// whole arrangement exists to prevent, and <c>KeyMapTests</c> asserts every
    /// identifier reaches a command so this cannot first be discovered by a user.
    /// </exception>
    private ICommand CommandFor(EtchCommandId id) => id switch
    {
        EtchCommandId.NewTab => NewTabCommand,
        EtchCommandId.CloseTab => CloseActiveTabCommand,
        EtchCommandId.ReopenClosed => ReopenClosedCommand,
        EtchCommandId.NextTab => NextTabCommand,
        EtchCommandId.PreviousTab => PreviousTabCommand,
        EtchCommandId.RenameTab => RenameTabCommand,
        EtchCommandId.ToggleEphemeral => ToggleEphemeralCommand,
        EtchCommandId.TogglePinned => TogglePinnedCommand,
        EtchCommandId.OpenPalette => OpenPaletteCommand,
        EtchCommandId.ApplySuggested => ApplySuggestedCommand,
        EtchCommandId.OpenSettings => OpenSettingsCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No command is wired to this identifier."),
    };
}
