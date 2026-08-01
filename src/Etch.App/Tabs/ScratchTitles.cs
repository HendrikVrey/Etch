using System.Globalization;

namespace Etch.App.Tabs;

/// <summary>
/// Names untitled scratch tabs.
/// </summary>
/// <remarks>
/// Pure and separated from the tab model so the numbering rules can be tested
/// without constructing a workspace. They look trivial and are not: the obvious
/// "count the tabs and add one" reuses a number the moment anything is closed out of
/// order, and two tabs called "Untitled 3" is a bug the user has to resolve by
/// guessing.
/// </remarks>
internal static class ScratchTitles
{
    private const string Prefix = "Untitled ";

    /// <summary>
    /// The lowest <c>Untitled N</c> not already in <paramref name="existingTitles"/>.
    /// </summary>
    /// <remarks>
    /// Lowest-unused rather than highest-plus-one, so a long session of opening and
    /// closing tabs does not drift into "Untitled 47" while three low numbers sit
    /// free. Titles the user has renamed are simply not in the set, which is the
    /// correct behaviour: renaming a tab to "Untitled 2" and then creating a new one
    /// should not produce a duplicate.
    /// </remarks>
    public static string NextAvailable(IEnumerable<string> existingTitles)
    {
        ArgumentNullException.ThrowIfNull(existingTitles);

        var taken = new HashSet<int>();

        foreach (var title in existingTitles)
        {
            if (TryParseOrdinal(title, out var ordinal))
            {
                taken.Add(ordinal);
            }
        }

        var candidate = 1;

        while (taken.Contains(candidate))
        {
            candidate++;
        }

        return Format(candidate);
    }

    /// <summary>Formats the title for a given ordinal.</summary>
    public static string Format(int ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{ordinal}");

    /// <summary>Reads the ordinal back out of a generated title.</summary>
    /// <remarks>
    /// Strict: only the exact shape this class produces counts. "Untitled" alone,
    /// "Untitled 007" and "Untitled 2 (copy)" are all user-chosen names that happen to
    /// start the same way, and treating them as reserved ordinals would let a rename
    /// silently consume a number.
    /// </remarks>
    private static bool TryParseOrdinal(string? title, out int ordinal)
    {
        ordinal = 0;

        if (title is null || !title.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = title.AsSpan(Prefix.Length);

        if (digits.Length == 0 || digits[0] == '0')
        {
            return false;
        }

        // NumberStyles.None: no sign, no whitespace, no thousands separators. Anything
        // else is not a title this class wrote.
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);
    }
}
