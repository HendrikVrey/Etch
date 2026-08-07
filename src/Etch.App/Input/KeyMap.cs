using System.Collections.Frozen;
using System.Windows.Input;

namespace Etch.App.Input;

/// <summary>An action a chord resolves to, and its argument where it takes one.</summary>
/// <param name="Command">What to do.</param>
/// <param name="Argument">
/// The zero-based tab index for <see cref="EtchCommandId.JumpToTab"/>. Zero, and ignored,
/// for everything else.
/// </param>
internal readonly record struct KeyAction(EtchCommandId Command, int Argument = 0);

/// <summary>
/// Etch's keyboard map. One table, and the only one.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the map used to live in four places at once — key bindings in
/// XAML, a loop in the window's constructor, command bindings riding on
/// <c>ApplicationCommands</c>' default gestures, and per-control key handlers — and no
/// single place said what the keyboard did. That is how <c>Ctrl+T</c> came to be missing
/// while every individual mechanism looked correct.
/// </para>
/// <para>
/// The table is also the reason the shortcuts can be resolved during the window's
/// <em>tunnelling</em> key pass rather than through WPF's input bindings. Input bindings
/// are matched in <c>PostProcessInput</c>, which is after the focused control has had the
/// key and after <c>KeyboardNavigation</c> has had a look at Tab — so <c>Ctrl+Tab</c> and
/// <c>Ctrl+Enter</c> were both at the mercy of ordering Etch does not control. Resolving
/// against this table on the way down makes that ordering irrelevant.
/// </para>
/// <para>
/// Chords deliberately absent: everything AvalonEdit's text area owns — cut, copy, paste,
/// undo, redo, select-all, the caret and selection movement keys, Tab and Shift+Tab for
/// indentation, and Enter. Taking any of those here would break editing.
/// </para>
/// </remarks>
internal static class KeyMap
{
    /// <summary>How many tabs <c>Ctrl+1</c> through <c>Ctrl+9</c> can reach.</summary>
    public const int DirectTabCount = 9;

    /// <summary>
    /// The key that starts a two-key sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pinning needed a chord and every single-key candidate was bad. <c>Ctrl+Shift+Enter</c>
    /// sits next to <c>Ctrl+Enter</c>, which rewrites the buffer without asking;
    /// <c>Ctrl+Alt+P</c> is AltGr on European layouts, where it would fire while someone was
    /// typing an ordinary character; <c>Ctrl+P</c> is too loaded to take. A sequence
    /// sidesteps the whole search — <c>Ctrl+K</c> is unclaimed by Etch and by AvalonEdit,
    /// and it is where an editor user already expects the second-tier commands to live.
    /// </para>
    /// <para>
    /// One prefix, not a set of them. A second would mean the window had to explain which
    /// sequence was in progress, and there is nothing else waiting for one.
    /// </para>
    /// </remarks>
    public static readonly Shortcut ChordPrefix = new(Key.K, ModifierKeys.Control);

    private static readonly FrozenDictionary<Shortcut, KeyAction> Table = Build();
    private static readonly FrozenDictionary<Shortcut, KeyAction> ChordTable = BuildChords();

    /// <summary>The single-key map, for tests and for anything that wants to print it.</summary>
    public static IReadOnlyDictionary<Shortcut, KeyAction> Bindings => Table;

    /// <summary>
    /// What each second key of a <see cref="ChordPrefix"/> sequence means.
    /// </summary>
    public static IReadOnlyDictionary<Shortcut, KeyAction> ChordBindings => ChordTable;

    /// <summary>Finds the action a chord means, if it means one.</summary>
    public static bool TryResolve(Shortcut shortcut, out KeyAction action) =>
        Table.TryGetValue(shortcut, out action);

    /// <summary>Finds what a key means as the second half of a sequence.</summary>
    public static bool TryResolveChord(Shortcut second, out KeyAction action) =>
        ChordTable.TryGetValue(second, out action);

    private static FrozenDictionary<Shortcut, KeyAction> Build()
    {
        const ModifierKeys Ctrl = ModifierKeys.Control;
        const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        // Built through Add rather than the indexer, deliberately: Add throws on a
        // duplicate chord and names it, where the indexer would let whichever line came
        // last quietly win. A shortcut that stopped working because a later entry shadowed
        // it is precisely the failure this type exists to make impossible.
        var map = new Dictionary<Shortcut, KeyAction>
        {
            // Tabs. Ctrl+T is the primary — it is what every browser and every editor has
            // trained people to press — and Ctrl+N stays as an alias rather than being
            // taken away from anyone who already has it in their fingers.
            { new Shortcut(Key.T, Ctrl), new(EtchCommandId.NewTab) },
            { new Shortcut(Key.N, Ctrl), new(EtchCommandId.NewTab) },
            { new Shortcut(Key.W, Ctrl), new(EtchCommandId.CloseTab) },
            { new Shortcut(Key.T, CtrlShift), new(EtchCommandId.ReopenClosed) },

            { new Shortcut(Key.Tab, Ctrl), new(EtchCommandId.NextTab) },
            { new Shortcut(Key.Tab, CtrlShift), new(EtchCommandId.PreviousTab) },

            // The browser aliases. AvalonEdit owns PageUp and PageDown unmodified and with
            // Shift, so only the Ctrl forms are free — which is exactly the pair every
            // tabbed application uses.
            { new Shortcut(Key.PageDown, Ctrl), new(EtchCommandId.NextTab) },
            { new Shortcut(Key.PageUp, Ctrl), new(EtchCommandId.PreviousTab) },

            { new Shortcut(Key.F2, ModifierKeys.None), new(EtchCommandId.RenameTab) },
            { new Shortcut(Key.E, CtrlShift), new(EtchCommandId.ToggleEphemeral) },

            // Files. These used to ride on ApplicationCommands' default gestures through
            // command bindings; they are here now so that the map is complete and there is
            // exactly one place to look when a key does nothing.
            { new Shortcut(Key.O, Ctrl), new(EtchCommandId.OpenFile) },
            { new Shortcut(Key.S, Ctrl), new(EtchCommandId.Save) },
            { new Shortcut(Key.F, Ctrl), new(EtchCommandId.Find) },
            { new Shortcut(Key.H, Ctrl), new(EtchCommandId.Replace) },

            // The two keys the product is an argument for.
            { new Shortcut(Key.P, CtrlShift), new(EtchCommandId.OpenPalette) },
            { new Shortcut(Key.Return, Ctrl), new(EtchCommandId.ApplySuggested) },

            // Settings. Key.OemComma alone is correct on every layout, not just this
            // author's: Windows defines VK_OEM_COMMA as "the ',' key" for any country or
            // region, so the layout that puts the comma somewhere else — AZERTY, where it
            // is under QWERTY's M — still reports it here.
            { new Shortcut(Key.OemComma, Ctrl), new(EtchCommandId.OpenSettings) },
        };

        // Ctrl+1..9, from both the number row and the numeric keypad. Built in a loop
        // because eighteen near-identical lines are eighteen chances for a transposed
        // digit that nothing would ever catch.
        for (var index = 0; index < DirectTabCount; index++)
        {
            var action = new KeyAction(EtchCommandId.JumpToTab, index);

            map.Add(new Shortcut(Key.D1 + index, Ctrl), action);
            map.Add(new Shortcut(Key.NumPad1 + index, Ctrl), action);
        }

        // The prefix must not also be an action. It is checked here rather than trusted,
        // because a single-key binding on Ctrl+K would fire before the sequence ever got its
        // second key and the sequence would simply stop working.
        if (map.ContainsKey(ChordPrefix))
        {
            throw new InvalidOperationException($"{ChordPrefix} starts a sequence and cannot also be a shortcut.");
        }

        return map.ToFrozenDictionary();
    }

    /// <summary>The second half of every <see cref="ChordPrefix"/> sequence.</summary>
    /// <remarks>
    /// Both forms of the second key are bound: <c>P</c> on its own, and <c>Ctrl+P</c> for
    /// the very common case of not letting go of Ctrl between the two presses. Requiring one
    /// and rejecting the other makes a shortcut that works for some people and mysteriously
    /// does not for others.
    /// </remarks>
    private static FrozenDictionary<Shortcut, KeyAction> BuildChords()
    {
        var map = new Dictionary<Shortcut, KeyAction>
        {
            { new Shortcut(Key.P, ModifierKeys.None), new(EtchCommandId.TogglePinned) },
            { new Shortcut(Key.P, ModifierKeys.Control), new(EtchCommandId.TogglePinned) },
        };

        return map.ToFrozenDictionary();
    }
}
