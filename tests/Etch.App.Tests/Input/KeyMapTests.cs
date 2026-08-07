using System.Windows.Input;
using Etch.App.Input;
using Xunit;

namespace Etch.App.Tests.Input;

/// <summary>
/// The keyboard map, asserted rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because <c>Ctrl+T</c> was missing for the whole of M1 and M2 without
/// anything noticing. Nothing was broken in the ordinary sense — every mechanism that
/// existed worked — the map was simply spread across XAML key bindings, a loop in the
/// window's constructor, and the default gestures riding on <c>ApplicationCommands</c>,
/// so no single artefact said what the keyboard did and no reviewer could see the gap.
/// </para>
/// <para>
/// The table below is deliberately a second, hand-written copy of
/// <see cref="KeyMap"/>'s. A test that derived its expectations from the code would agree
/// with any change, including a wrong one. This one has to be edited on purpose, which is
/// the same act as editing the README's shortcut table — and those two staying in step is
/// the actual thing being protected.
/// </para>
/// </remarks>
public class KeyMapTests
{
    private const ModifierKeys Ctrl = ModifierKeys.Control;
    private const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

    [Theory]
    // Tabs.
    [InlineData(Key.T, Ctrl, EtchCommandId.NewTab)]
    [InlineData(Key.N, Ctrl, EtchCommandId.NewTab)]
    [InlineData(Key.W, Ctrl, EtchCommandId.CloseTab)]
    [InlineData(Key.T, CtrlShift, EtchCommandId.ReopenClosed)]
    [InlineData(Key.Tab, Ctrl, EtchCommandId.NextTab)]
    [InlineData(Key.Tab, CtrlShift, EtchCommandId.PreviousTab)]
    [InlineData(Key.PageDown, Ctrl, EtchCommandId.NextTab)]
    [InlineData(Key.PageUp, Ctrl, EtchCommandId.PreviousTab)]
    [InlineData(Key.F2, ModifierKeys.None, EtchCommandId.RenameTab)]
    [InlineData(Key.E, CtrlShift, EtchCommandId.ToggleEphemeral)]
    // Files.
    [InlineData(Key.O, Ctrl, EtchCommandId.OpenFile)]
    [InlineData(Key.S, Ctrl, EtchCommandId.Save)]
    [InlineData(Key.F, Ctrl, EtchCommandId.Find)]
    [InlineData(Key.H, Ctrl, EtchCommandId.Replace)]
    // The palette.
    [InlineData(Key.P, CtrlShift, EtchCommandId.OpenPalette)]
    [InlineData(Key.Return, Ctrl, EtchCommandId.ApplySuggested)]
    // Settings.
    [InlineData(Key.OemComma, Ctrl, EtchCommandId.OpenSettings)]
    public void The_documented_chords_resolve_to_the_documented_commands(
        Key key,
        ModifierKeys modifiers,
        EtchCommandId expected)
    {
        Assert.True(KeyMap.TryResolve(new Shortcut(key, modifiers), out var action));
        Assert.Equal(expected, action.Command);
    }

    [Fact]
    public void Ctrl_T_opens_a_new_tab()
    {
        // The regression this whole file is named after. Ctrl+N is the alias, not the
        // other way round: Ctrl+T is what a browser has trained everyone to press, and it
        // resolving to anything other than a new tab is the bug being locked out.
        Assert.True(KeyMap.TryResolve(new Shortcut(Key.T, Ctrl), out var pressed));
        Assert.Equal(EtchCommandId.NewTab, pressed.Command);

        Assert.True(KeyMap.TryResolve(new Shortcut(Key.N, Ctrl), out var alias));
        Assert.Equal(EtchCommandId.NewTab, alias.Command);
    }

    [Fact]
    public void Every_command_can_be_reached_from_the_keyboard()
    {
        // Etch has no menu bar and no toolbar by design, so a command with no chord is a
        // command with no way in at all. Both tables count: pinning is reachable only as the
        // second half of a sequence, and it is no less reachable for that.
        var reachable = KeyMap.Bindings.Values
            .Concat(KeyMap.ChordBindings.Values)
            .Select(static action => action.Command)
            .ToHashSet();

        var unreachable = Enum.GetValues<EtchCommandId>().Where(id => !reachable.Contains(id)).ToArray();

        Assert.True(unreachable.Length == 0, $"No chord reaches: {string.Join(", ", unreachable)}");
    }

    [Fact]
    public void Ctrl_K_then_P_pins_the_tab_whether_or_not_ctrl_was_released()
    {
        // Both spellings are bound. Requiring one and rejecting the other produces a
        // shortcut that works for the people who let go of Ctrl and mysteriously does not
        // for the people who do not.
        Assert.True(KeyMap.TryResolveChord(new Shortcut(Key.P, ModifierKeys.None), out var released));
        Assert.Equal(EtchCommandId.TogglePinned, released.Command);

        Assert.True(KeyMap.TryResolveChord(new Shortcut(Key.P, Ctrl), out var held));
        Assert.Equal(EtchCommandId.TogglePinned, held.Command);
    }

    [Fact]
    public void The_sequence_prefix_is_not_also_a_shortcut_of_its_own()
    {
        // A single-key binding on Ctrl+K would fire before the sequence ever got its second
        // key, and the sequence would simply stop working. KeyMap throws while building if
        // this is ever violated, so merely touching the table proves it — the assertion is
        // here so the reason is written down next to the rule.
        Assert.False(KeyMap.TryResolve(KeyMap.ChordPrefix, out _));
        Assert.Equal(new Shortcut(Key.K, Ctrl), KeyMap.ChordPrefix);
    }

    [Fact]
    public void An_unbound_second_key_means_nothing_rather_than_something_else()
    {
        // The window swallows it and says so. What must not happen is a second key falling
        // through to a single-key binding — Ctrl+K then Ctrl+W would close the tab, which is
        // not what anyone who started a sequence was asking for.
        Assert.False(KeyMap.TryResolveChord(new Shortcut(Key.W, Ctrl), out _));
        Assert.False(KeyMap.TryResolveChord(new Shortcut(Key.T, Ctrl), out _));
        Assert.False(KeyMap.TryResolveChord(new Shortcut(Key.P, CtrlShift), out _));
    }

    [Fact]
    public void Ctrl_1_to_9_address_the_first_nine_tabs_from_both_number_rows()
    {
        for (var index = 0; index < KeyMap.DirectTabCount; index++)
        {
            Assert.True(KeyMap.TryResolve(new Shortcut(Key.D1 + index, Ctrl), out var digit));
            Assert.Equal(new KeyAction(EtchCommandId.JumpToTab, index), digit);

            Assert.True(KeyMap.TryResolve(new Shortcut(Key.NumPad1 + index, Ctrl), out var numpad));
            Assert.Equal(new KeyAction(EtchCommandId.JumpToTab, index), numpad);
        }

        // Ctrl+0 is not "the tenth tab" and must not silently be one. It is unbound.
        Assert.False(KeyMap.TryResolve(new Shortcut(Key.D0, Ctrl), out _));
    }

    [Theory]
    // The clipboard and history keys AvalonEdit's text area owns.
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.V, ModifierKeys.Control)]
    [InlineData(Key.Z, ModifierKeys.Control)]
    [InlineData(Key.Y, ModifierKeys.Control)]
    [InlineData(Key.A, ModifierKeys.Control)]
    // Indentation and newlines.
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.Tab, ModifierKeys.Shift)]
    [InlineData(Key.Return, ModifierKeys.None)]
    [InlineData(Key.Return, ModifierKeys.Shift)]
    // Caret movement, including the word and document jumps.
    [InlineData(Key.Left, ModifierKeys.Control)]
    [InlineData(Key.Right, ModifierKeys.Control)]
    [InlineData(Key.Home, ModifierKeys.Control)]
    [InlineData(Key.End, ModifierKeys.Control)]
    [InlineData(Key.PageUp, ModifierKeys.None)]
    [InlineData(Key.PageDown, ModifierKeys.None)]
    [InlineData(Key.PageUp, ModifierKeys.Shift)]
    [InlineData(Key.PageDown, ModifierKeys.Shift)]
    // Deletion.
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.Control)]
    [InlineData(Key.Back, ModifierKeys.Control)]
    public void Nothing_the_editor_owns_is_claimed(Key key, ModifierKeys modifiers)
    {
        // Chords are resolved on the window's tunnelling pass, which means a shortcut
        // added here wins outright over the text area beneath it. Taking any of these
        // would break editing itself, and it would do so silently — the key would simply
        // stop working, with no error anywhere.
        Assert.False(KeyMap.TryResolve(new Shortcut(key, modifiers), out _));
    }

    [Fact]
    public void An_extra_modifier_is_a_different_chord()
    {
        // Win+Ctrl+T is a shell gesture the user pressed on purpose. Matching modifiers
        // exactly rather than masking for Ctrl is what stops an editor that happens to
        // have focus from swallowing it.
        Assert.False(KeyMap.TryResolve(new Shortcut(Key.T, Ctrl | ModifierKeys.Windows), out _));
        Assert.False(KeyMap.TryResolve(new Shortcut(Key.T, Ctrl | ModifierKeys.Alt), out _));
        Assert.False(KeyMap.TryResolve(new Shortcut(Key.T, ModifierKeys.None), out _));
    }

    [Fact]
    public void The_map_is_built_without_a_duplicate_chord()
    {
        // KeyMap builds through Dictionary.Add, which throws on a duplicate, so simply
        // touching the table proves no entry was shadowed by a later one. Asserting the
        // count as well makes an accidental deletion visible rather than merely quiet.
        Assert.Equal(17 + (KeyMap.DirectTabCount * 2), KeyMap.Bindings.Count);

        // Two entries, one command: P and Ctrl+P.
        Assert.Equal(2, KeyMap.ChordBindings.Count);
    }

    [Fact]
    public void No_single_key_shortcut_uses_K()
    {
        // The prefix has to be free in Etch's own table or the sequence never gets its
        // second key — KeyMap throws while building if it is not, and this says why.
        //
        // Whether AvalonEdit claims it is a separate question that no assertion here can
        // answer; it was checked against the library's own command handlers and the answer is
        // recorded on ChordPrefix in KeyMap.cs, where it is a decision rather than a test.
        Assert.DoesNotContain(KeyMap.Bindings.Keys, shortcut => shortcut.Key is Key.K);
    }
}
