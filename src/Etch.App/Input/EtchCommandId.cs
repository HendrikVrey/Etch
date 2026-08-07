namespace Etch.App.Input;

/// <summary>
/// Everything the keyboard can ask Etch to do.
/// </summary>
/// <remarks>
/// <para>
/// The vocabulary shared by <see cref="KeyMap"/>, which says which chord means which
/// action, and the window, which says what each action does. Splitting the two is what
/// lets the map be a table of data rather than a pile of XAML bindings — and a table can
/// be printed, diffed against the README, and asserted in a test, which is how a missing
/// binding gets noticed before a user finds it.
/// </para>
/// <para>
/// <b>Public, unlike everything around it, and only because of that last clause.</b>
/// <c>KeyMapTests</c> takes it as a parameter of a <c>[Theory]</c>, xunit requires test
/// methods to be public, and a public method may not have a less-accessible parameter type —
/// <c>InternalsVisibleTo</c> grants access but does not change declared accessibility, so
/// the consistency rule still fires (CS0051). Etch.App is a <c>WinExe</c> with no API
/// surface to protect, which makes this the cheaper of the two ways out; the alternative was
/// to have the test compare integers and lose the names in its failure messages.
/// </para>
/// </remarks>
public enum EtchCommandId
{
    /// <summary>Create an empty scratch tab.</summary>
    NewTab,

    /// <summary>Close the tab in front. Never destructive.</summary>
    CloseTab,

    /// <summary>Bring back the most recently closed tab.</summary>
    ReopenClosed,

    /// <summary>Move one tab to the right, wrapping.</summary>
    NextTab,

    /// <summary>Move one tab to the left, wrapping.</summary>
    PreviousTab,

    /// <summary>Jump straight to the tab at <c>Argument</c>, zero-based.</summary>
    JumpToTab,

    /// <summary>Edit the front tab's caption in place.</summary>
    RenameTab,

    /// <summary>Mark the front tab as never written to disk, or lift the mark.</summary>
    ToggleEphemeral,

    /// <summary>Pin the front tab to the left of the strip, or unpin it.</summary>
    TogglePinned,

    /// <summary>Open a file from disk.</summary>
    OpenFile,

    /// <summary>Write a file tab through, or give a scratch tab a home.</summary>
    Save,

    /// <summary>Open the find bar.</summary>
    Find,

    /// <summary>Open the find bar with replace showing.</summary>
    Replace,

    /// <summary>Open the command palette.</summary>
    OpenPalette,

    /// <summary>Apply the transform the buffer suggests, without opening anything.</summary>
    ApplySuggested,

    /// <summary>Open the settings panel.</summary>
    OpenSettings,
}
