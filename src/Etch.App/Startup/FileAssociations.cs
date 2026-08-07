using System.Runtime.InteropServices;
using Etch.App.Diagnostics;
using Microsoft.Win32;

namespace Etch.App.Startup;

/// <summary>
/// Opt-in, per-user file associations for the handful of extensions a scratchpad is
/// plausibly the right editor for, and the "Open with Etch" right-click verb.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is under <c>HKEY_CURRENT_USER</c>.</b> Nothing writes to
/// <c>HKEY_CLASSES_ROOT</c> or <c>HKEY_LOCAL_MACHINE</c>: those are machine-wide, they
/// need elevation, and an editor that quietly became every account's handler for
/// <c>.json</c> would be the sort of thing people uninstall an application over. Etch
/// ships with an installer, and that installer is per-user too — it asks for no
/// elevation and writes the same keys this file does — so the rule is not a workaround
/// for having no uninstaller, it is the posture of the whole product. Anything written
/// here can be undone from this panel, by the uninstaller, or by hand, without
/// administrative rights and without affecting another account.
/// </para>
/// <para>
/// <b>What this can and cannot do, stated precisely because the UI has to say it.</b>
/// Two things are written per extension. The ProgID is registered and added to
/// <c>OpenWithProgids</c>, which puts Etch in the "Open with" list and always works. The
/// extension's default value under <c>Software\Classes</c> is then pointed at that ProgID
/// — but Windows only consults that when the user has no <c>UserChoice</c> recorded for
/// the extension. <c>UserChoice</c> is protected by a hash Windows verifies, and writing
/// it is both unsupported and a thing malware does; Etch does not go near it. It is
/// <em>read</em>, in <see cref="IsHonoured"/>, because it is the only way to answer
/// truthfully whether the setting the user just ticked will have any effect.
/// </para>
/// <para>
/// Withdrawal restores rather than deletes. <see cref="Register"/> stashes whatever the
/// extension's default value was before Etch overwrote it, and <see cref="Withdraw"/>
/// puts it back — so turning the checkbox off returns the machine to the state it was in,
/// rather than to no association at all.
/// </para>
/// </remarks>
internal static partial class FileAssociations
{
    /// <summary>The extensions Etch is willing to associate itself with.</summary>
    /// <remarks>
    /// A closed list, and deliberately short: these are the three a developer opens in a
    /// scratchpad. Everything else is a file type with a real editor behind it.
    /// <para>
    /// It is also what makes the registry writes below safe. Every key path here is built
    /// by concatenation, and the only variable part is an extension — so the extension has
    /// to come from this list rather than from anything a user or a file could influence.
    /// <see cref="Describe"/>'s exhaustive switch is checked by a test, so an extension
    /// added here cannot silently ship without a label.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Associable { get; } = [".txt", ".json", ".log"];

    private const string ClassesKey = @"Software\Classes";
    private const string FileExtsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const string ProgIdPrefix = "Etch";
    private const string OpenWithProgIds = "OpenWithProgids";

    /// <summary>
    /// The verb key that puts "Open with Etch" on every file's right-click menu.
    /// </summary>
    /// <remarks>
    /// <c>*</c> is the class for "any file", which is what was asked for and what it says:
    /// the entry appears for executables and libraries too. That is the price of "always an
    /// option" and it is the right trade for a scratchpad, whose whole premise is opening
    /// something to look at it.
    /// </remarks>
    private const string OpenWithVerbKey = @"Software\Classes\*\shell\Etch";

    /// <summary>The label Explorer shows for the verb.</summary>
    private const string OpenWithVerbLabel = "Open with Etch";

    /// <summary>
    /// Where the value Etch displaced is kept so that withdrawal can put it back.
    /// </summary>
    /// <remarks>
    /// Stored on Etch's own ProgID key rather than anywhere near the extension's, so that
    /// it disappears with the ProgID and cannot be mistaken by another tool for something
    /// Windows defines.
    /// </remarks>
    private const string DisplacedValueName = "EtchPreviousProgId";

    /// <summary>Tells the shell that associations changed.</summary>
    private const int ShellAssociationChanged = 0x08000000;

    /// <summary>Interpret the (unused) item pointers as id lists.</summary>
    private const uint ShellNotifyIdList = 0x0000;

    /// <summary>A friendly name for <paramref name="extension"/>, for the settings UI.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="extension"/> is not in <see cref="Associable"/>. Thrown rather
    /// than defaulted: a missing label would ship as a blank row in the settings panel,
    /// and a blank checkbox that changes the user's file associations is unacceptable.
    /// </exception>
    public static string Describe(string extension) => extension switch
    {
        ".txt" => "Text files",
        ".json" => "JSON files",
        ".log" => "Log files",
        _ => throw new ArgumentOutOfRangeException(
            nameof(extension),
            extension,
            "Etch does not offer to associate itself with this extension."),
    };

    /// <summary>
    /// Whether Windows actually opens <paramref name="extension"/> with Etch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not "did Etch write the registry keys" — "will double-clicking one of these open
    /// Etch".</b> Those are different questions on any machine where the user has ever
    /// picked a default for the extension, and answering the first while displaying the
    /// second is how a settings panel comes to show a ticked box for something that does
    /// not happen.
    /// </para>
    /// <para>
    /// <c>UserChoice</c> wins when it exists, so it is checked first. Only when there is
    /// none does the <c>Software\Classes</c> default decide.
    /// </para>
    /// <para>
    /// Returns false rather than throwing when the registry cannot be read. A settings
    /// panel that refused to open because a checkbox could not be filled in would be a far
    /// worse outcome than a checkbox that reads unchecked.
    /// </para>
    /// <para>
    /// <b>Known gap, stated rather than glossed:</b> this stops at the ProgID and does not
    /// check that the ProgID's <c>shell\open\command</c> still names <em>this</em>
    /// executable. A copy of Etch that has since moved therefore reads as ticked while
    /// double-clicking the file would fail. <see cref="IsOpenWithVerbPresent"/> does make
    /// that check, because a verb has no <c>UserChoice</c> arbiter and the command line is
    /// the only thing there is to ask — so the asymmetry is real and is a gap here rather
    /// than a decision. Closing it means comparing against
    /// <see cref="Environment.ProcessPath"/> through <see cref="ExecutableFromCommand"/>,
    /// and wants its own tests before it changes what three existing checkboxes report.
    /// </para>
    /// </remarks>
    public static bool IsHonoured(string extension)
    {
        if (!IsAssociable(extension))
        {
            return false;
        }

        try
        {
            var progId = ProgIdFor(extension);

            if (ReadUserChoice(extension) is { } chosen)
            {
                // Ordinal-ignore-case: ProgIDs are registry key names, which Windows
                // compares case-insensitively.
                return string.Equals(chosen, progId, StringComparison.OrdinalIgnoreCase);
            }

            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{extension}");

            return string.Equals(key?.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Broad on purpose. This is called while building a UI, and there is no
            // registry failure worth refusing to show the settings panel over.
            DiagnosticLog.WriteFailure($"association-read:{extension}", ex);
            return false;
        }
    }

    /// <summary>
    /// Makes Etch the handler for <paramref name="extension"/>, or withdraws it.
    /// </summary>
    /// <param name="extension">One of <see cref="Associable"/>.</param>
    /// <param name="associate">True to register, false to withdraw.</param>
    /// <returns>
    /// What happened, including whether Windows will honour it — see
    /// <see cref="AssociationResult"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="extension"/> is not associable.</exception>
    public static AssociationResult Set(string extension, bool associate)
    {
        if (!IsAssociable(extension))
        {
            throw new ArgumentOutOfRangeException(
                nameof(extension),
                extension,
                "Etch does not offer to associate itself with this extension.");
        }

        try
        {
            if (associate)
            {
                Register(extension);
            }
            else
            {
                Withdraw(extension);
            }

            // Without this, Explorer keeps showing the old icon and the old handler until
            // it is restarted — which looks exactly like the setting not having worked.
            NotifyShell();

            return associate && !IsHonoured(extension)
                ? AssociationResult.OverriddenByWindows
                : AssociationResult.Applied;
        }
        catch (Exception ex)
        {
            // Broad, deliberately, and this is the place for it. Set is called straight
            // from a WPF checkbox handler, which is synchronous and not wrapped — so an
            // exception escaping here reaches DispatcherUnhandledException and takes the
            // window down. RegistryKey.SetValue alone can raise ArgumentException for a
            // value it dislikes; a settings checkbox must not be able to end the process.
            DiagnosticLog.WriteFailure($"association:{extension}", ex);
            return AssociationResult.Failed;
        }
    }

    /// <summary>
    /// Whether "Open with Etch" is on the right-click menu <em>and points at this copy of
    /// Etch</em>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The second half is the part that matters. There is no <c>UserChoice</c> equivalent
    /// arbitrating a shell verb — the key is what decides — so the honest question is not
    /// "did someone write this key" but "does the entry a user would click still run this
    /// executable". A copy of Etch moved, reinstalled elsewhere, or replaced by a portable
    /// build leaves a verb pointing at a path that no longer exists, and a checkbox that
    /// read the key's mere presence would show that as working.
    /// </para>
    /// <para>
    /// So a stale verb reads as absent, and ticking the box repairs it. That is the
    /// behaviour to want: the alternative is a ticked checkbox above a menu entry that
    /// fails.
    /// </para>
    /// <para>
    /// Returns false rather than throwing, for the same reason <see cref="IsHonoured"/>
    /// does: this is called while building the settings panel.
    /// </para>
    /// </remarks>
    public static bool IsOpenWithVerbPresent()
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } executable)
            {
                return false;
            }

            using var command = Registry.CurrentUser.OpenSubKey($@"{OpenWithVerbKey}\command");

            return command?.GetValue(null) is string line
                && ExecutableFromCommand(line) is { } registered
                && string.Equals(registered, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("openwith-read", ex);
            return false;
        }
    }

    /// <summary>Adds or removes the "Open with Etch" right-click verb.</summary>
    /// <param name="wanted">True to add it, false to remove it.</param>
    /// <returns>
    /// <see cref="AssociationResult.Applied"/> when the verb is now in the state asked
    /// for, <see cref="AssociationResult.Failed"/> when the registry could not be written.
    /// <see cref="AssociationResult.OverriddenByWindows"/> is never returned: nothing in
    /// Windows overrides a shell verb the way <c>UserChoice</c> overrides an extension's
    /// default.
    /// </returns>
    /// <remarks>
    /// Broad catch, and the same reasoning as <see cref="Set"/> verbatim: this is called
    /// straight from a synchronous WPF checkbox handler, and an exception escaping here
    /// reaches <c>DispatcherUnhandledException</c> and takes the window down.
    /// </remarks>
    public static AssociationResult SetOpenWithVerb(bool wanted)
    {
        try
        {
            if (wanted)
            {
                RegisterOpenWithVerb();
            }
            else
            {
                // The whole subtree, and that is safe here in a way it is not for
                // OpenWithProgids: this key is Etch's own, named after Etch, and nothing
                // else writes into it. Contrast Withdraw, which deletes one value out of a
                // list every application shares.
                Registry.CurrentUser.DeleteSubKeyTree(OpenWithVerbKey, throwOnMissingSubKey: false);
            }

            NotifyShell();

            return AssociationResult.Applied;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteFailure("openwith", ex);
            return AssociationResult.Failed;
        }
    }

    private static void RegisterOpenWithVerb()
    {
        if (Environment.ProcessPath is not { Length: > 0 } executable)
        {
            throw new IOException("Etch could not determine its own location, so it cannot register itself.");
        }

        using (var verb = Registry.CurrentUser.CreateSubKey(OpenWithVerbKey))
        {
            // Both the default value and MUIVerb. Explorer prefers MUIVerb where it is
            // present, and older shells read the default; writing one and not the other
            // leaves a menu entry labelled "Etch" on whichever of them is reading.
            verb.SetValue(null, OpenWithVerbLabel);
            verb.SetValue("MUIVerb", OpenWithVerbLabel);
            verb.SetValue("Icon", $"{executable},0");
        }

        using var command = Registry.CurrentUser.CreateSubKey($@"{OpenWithVerbKey}\command");

        // Both quoted, for the reasons spelled out in Register: an unquoted path breaks on
        // a space in a folder name, and an unquoted %1 breaks on most documents people name
        // themselves.
        command.SetValue(null, $"\"{executable}\" \"%1\"");
    }

    /// <summary>
    /// The executable a <c>shell\...\command</c> value would run, or null.
    /// </summary>
    /// <remarks>
    /// Only the quoted form is recognised, and deliberately: that is the only form Etch
    /// writes, and an unquoted command line cannot be split reliably anyway — a path with a
    /// space in it is indistinguishable from a path followed by an argument. Anything else
    /// therefore reads as "not ours", which routes to the repair path rather than to a
    /// guess.
    /// </remarks>
    private static string? ExecutableFromCommand(string command)
    {
        if (command.Length == 0 || command[0] != '"')
        {
            return null;
        }

        var end = command.IndexOf('"', startIndex: 1);

        return end > 1 ? command[1..end] : null;
    }

    private static bool IsAssociable(string extension) =>
        Associable.Contains(extension, StringComparer.Ordinal);

    /// <summary>The ProgID Etch registers for <paramref name="extension"/>.</summary>
    /// <remarks>
    /// <para>
    /// One per extension rather than a single shared <c>Etch.Document</c>, so each can
    /// carry its own description — "JSON files (Etch)" is what the Open With dialog shows,
    /// and one generic entry repeated three times is not useful there.
    /// </para>
    /// <para>
    /// <b>This naming rule is duplicated in <c>installer/Etch.iss</c>, which hardcodes
    /// <c>Etch.txt</c>, <c>Etch.json</c> and <c>Etch.log</c>, and their descriptions from
    /// <see cref="Describe"/>.</b> Two writers now touch these keys and nothing can check
    /// at build time that they agree — so if this changes, the <c>.iss</c> changes with
    /// it, or the settings panel will show a state that disagrees with what the installer
    /// wrote.
    /// </para>
    /// </remarks>
    private static string ProgIdFor(string extension) => $"{ProgIdPrefix}{extension}";

    /// <summary>
    /// The ProgID Windows has recorded as the user's own choice, or null if there is none.
    /// </summary>
    /// <remarks>
    /// Read-only, always. This key is hash-protected and writing it is unsupported — see
    /// the type remarks. Reading it is the only way to tell the user the truth about what
    /// their double-click will do.
    /// </remarks>
    private static string? ReadUserChoice(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{FileExtsKey}\{extension}\UserChoice");

        return key?.GetValue("ProgId") as string;
    }

    private static void Register(string extension)
    {
        // Environment.ProcessPath rather than the entry assembly's location: under a
        // single-file publish — which is how Etch ships — the assembly has no path on
        // disk at all, and Assembly.Location returns an empty string.
        if (Environment.ProcessPath is not { Length: > 0 } executable)
        {
            throw new IOException("Etch could not determine its own location, so it cannot register itself.");
        }

        var progId = ProgIdFor(extension);

        using var extensionKey = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{extension}");

        // Read before anything is written. Whatever was here belongs to whoever put it
        // there, and withdrawal has to be able to give it back.
        var displaced = extensionKey.GetValue(null) as string;

        using (var progIdKey = Registry.CurrentUser.CreateSubKey($@"{ClassesKey}\{progId}"))
        {
            progIdKey.SetValue(null, $"{Describe(extension)} (Etch)");

            // Only when it names something else, and only when there is not already one
            // recorded — ticking the box twice must not overwrite the original with
            // Etch's own ProgID and make the restore a no-op.
            if (!string.IsNullOrEmpty(displaced)
                && !string.Equals(displaced, progId, StringComparison.OrdinalIgnoreCase)
                && progIdKey.GetValue(DisplacedValueName) is null)
            {
                progIdKey.SetValue(DisplacedValueName, displaced, RegistryValueKind.String);
            }

            using (var icon = progIdKey.CreateSubKey("DefaultIcon"))
            {
                // Index 0 is the executable's own icon, which is the only one it has.
                icon.SetValue(null, $"{executable},0");
            }

            using var command = progIdKey.CreateSubKey(@"shell\open\command");

            // Both quoted. An unquoted path breaks on the space in "Program Files", and an
            // unquoted %1 breaks on every file name containing a space — which, for a
            // document a person named themselves, is most of them.
            command.SetValue(null, $"\"{executable}\" \"%1\"");
        }

        // OpenWithProgids first, and separately from the default below. This is the half
        // that always works: it puts Etch in the "Open with" list whatever Windows has
        // recorded as the user's chosen default.
        //
        // An empty REG_SZ rather than REG_NONE. Both are accepted by the shell, and the
        // string avoids depending on RegistryKey.SetValue's handling of a kind whose
        // documented data type is unusual.
        using (var openWith = extensionKey.CreateSubKey(OpenWithProgIds))
        {
            openWith.SetValue(progId, string.Empty, RegistryValueKind.String);
        }

        // The half Windows may decline to honour. Never UserChoice — see the type remarks.
        extensionKey.SetValue(null, progId);
    }

    private static void Withdraw(string extension)
    {
        var progId = ProgIdFor(extension);
        var displaced = ReadDisplacedValue(progId);

        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesKey}\{progId}", throwOnMissingSubKey: false);

        using var extensionKey = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{extension}", writable: true);

        if (extensionKey is null)
        {
            return;
        }

        using (var openWith = extensionKey.OpenSubKey(OpenWithProgIds, writable: true))
        {
            // Only Etch's own entry. This key is a shared list — every application that
            // can open the type adds itself to it — so deleting the key would remove every
            // other application's "Open with" entry along with Etch's.
            openWith?.DeleteValue(progId, throwOnMissingValue: false);
        }

        // Touched only while it still names Etch. If the user has since chosen another
        // editor, that choice is theirs and turning this checkbox off must not undo it.
        if (!string.Equals(extensionKey.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (displaced is { Length: > 0 })
        {
            // Put back what Etch displaced rather than leaving the extension with no
            // per-user default at all.
            extensionKey.SetValue(null, displaced);
        }
        else
        {
            extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
        }
    }

    private static string? ReadDisplacedValue(string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey}\{progId}");

        return key?.GetValue(DisplacedValueName) as string;
    }

    /// <summary>
    /// Tells the shell to re-read associations.
    /// </summary>
    /// <remarks>
    /// Best-effort. The registry writes have already happened and are correct whether or
    /// not Explorer notices promptly — a failure here costs a stale icon until the next
    /// logon, which is not worth failing the operation over or telling the user about.
    /// </remarks>
    private static void NotifyShell()
    {
        try
        {
            SHChangeNotify(ShellAssociationChanged, ShellNotifyIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.WriteFailure("association-notify", ex);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

/// <summary>What came of changing an association.</summary>
internal enum AssociationResult
{
    /// <summary>The registry was updated and Windows is honouring it.</summary>
    Applied,

    /// <summary>
    /// The registry was updated, but Windows still routes the extension elsewhere
    /// because the user has an explicit default recorded for it.
    /// </summary>
    /// <remarks>
    /// Not a failure, and it must not be reported as one. Etch is now in the "Open with"
    /// list; the user has to finish the job in Windows' own Default Apps page, which is
    /// the only place that choice can legitimately be changed.
    /// </remarks>
    OverriddenByWindows,

    /// <summary>The registry could not be written.</summary>
    Failed,
}
