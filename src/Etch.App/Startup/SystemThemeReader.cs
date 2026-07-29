using Microsoft.Win32;

namespace Etch.App.Startup;

/// <summary>Whether Windows is currently in light or dark mode.</summary>
internal enum SystemThemeMode
{
    /// <summary>Windows apps are using the dark colour scheme.</summary>
    Dark,

    /// <summary>Windows apps are using the light colour scheme.</summary>
    Light,
}

/// <summary>Reads the current Windows app theme.</summary>
/// <remarks>
/// Deliberately a direct registry read rather than a library call. It runs on the
/// startup path before the theme is applied, it costs microseconds, and knowing the
/// answer early is what lets Etch avoid re-merging its resource dictionaries when
/// the compiled default already matches.
/// <para>
/// Named <c>SystemThemeReader</c> rather than <c>SystemTheme</c> because
/// <c>Wpf.Ui.Appearance</c> already exports a public <c>SystemTheme</c> enum, and
/// any file importing both namespaces would be ambiguous.
/// </para>
/// </remarks>
internal static class SystemThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// Reads the system theme, defaulting to light when the value is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// Light is Windows' own default when <c>AppsUseLightTheme</c> is absent, so
    /// that is what Etch defaults to. Matching the user's system is worth more than
    /// the dictionary swap it costs — optimising the other way would render a
    /// group-policy-locked machine dark against its owner's setting.
    /// </remarks>
    public static SystemThemeMode Detect()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(AppsUseLightThemeValue) is int appsUseLightTheme)
            {
                return appsUseLightTheme == 0 ? SystemThemeMode.Dark : SystemThemeMode.Light;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Group policy can lock this key down. Fall through to the default.
        }

        return SystemThemeMode.Light;
    }
}
