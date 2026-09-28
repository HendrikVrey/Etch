using System.Windows.Input;

namespace Etch.App.Input;

/// <summary>
/// A key and the modifiers held with it.
/// </summary>
/// <param name="Key">The key, with Alt's <see cref="System.Windows.Input.Key.System"/> indirection already resolved.</param>
/// <param name="Modifiers">Every modifier down at the time, including Windows.</param>
/// <remarks>
/// <para>
/// A value type so the keyboard map can be a dictionary rather than a chain of
/// comparisons. That is not a performance argument, nobody types fast enough for it
/// to matter, it is so the whole map is one literal table that can be read top to
/// bottom and asserted against in a test.
/// </para>
/// <para>
/// <see cref="Modifiers"/> is matched exactly, never masked. Win+Ctrl+T therefore does
/// not resolve to Ctrl+T: a shell chord the user pressed on purpose must not be
/// swallowed by an editor that happened to have focus.
/// </para>
/// </remarks>
internal readonly record struct Shortcut(Key Key, ModifierKeys Modifiers)
{
    /// <summary>Reads the chord out of a key event.</summary>
    /// <remarks>
    /// A key pressed with Alt arrives as <see cref="System.Windows.Input.Key.System"/>
    /// with the real key in <c>SystemKey</c>. Etch binds no Alt chords today, so this
    /// only exists so that an Alt chord added later does not silently fail to match
    /// while looking exactly right in the table.
    /// </remarks>
    public static Shortcut From(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        return new Shortcut(key, e.KeyboardDevice.Modifiers);
    }
}
