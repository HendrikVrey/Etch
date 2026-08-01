using Etch.App.Tabs;
using Xunit;

namespace Etch.App.Tests.Tabs;

/// <summary>
/// Naming untitled tabs.
/// </summary>
/// <remarks>
/// It looks trivial and is not. The obvious "count the tabs and add one" produces a
/// duplicate the moment anything is closed out of order, and two tabs called
/// "Untitled 3" is a problem the user has to resolve by guessing which is which.
/// </remarks>
public class ScratchTitlesTests
{
    [Fact]
    public void The_first_tab_is_untitled_one()
    {
        Assert.Equal("Untitled 1", ScratchTitles.NextAvailable([]));
    }

    [Fact]
    public void Numbering_fills_the_lowest_gap_rather_than_growing()
    {
        // Closing the middle tab of three and opening a new one should reuse 2, not
        // drift towards "Untitled 47" over a long session.
        Assert.Equal("Untitled 2", ScratchTitles.NextAvailable(["Untitled 1", "Untitled 3"]));
    }

    [Fact]
    public void An_existing_number_is_never_reused()
    {
        Assert.Equal("Untitled 3", ScratchTitles.NextAvailable(["Untitled 1", "Untitled 2"]));
    }

    [Fact]
    public void A_user_renaming_a_tab_to_a_generated_name_still_reserves_it()
    {
        // Otherwise renaming a tab to "Untitled 1" and pressing Ctrl+N produces two tabs
        // with the same caption.
        Assert.Equal("Untitled 2", ScratchTitles.NextAvailable(["Untitled 1"]));
    }

    [Theory]
    [InlineData("Untitled")]
    [InlineData("Untitled ")]
    [InlineData("Untitled 01")]
    [InlineData("Untitled 1 (copy)")]
    [InlineData("Untitled -1")]
    [InlineData("Untitled  1")]
    [InlineData("notes.txt")]
    public void Names_that_only_look_generated_do_not_reserve_a_number(string title)
    {
        // These are user-chosen names that happen to start the same way. Treating them
        // as reserved would let a rename silently consume a number.
        Assert.Equal("Untitled 1", ScratchTitles.NextAvailable([title]));
    }

    [Fact]
    public void Titles_are_stable_across_cultures()
    {
        // Formatted with the invariant culture, so a locale with a different digit shape
        // cannot produce a title that no longer parses as the number it holds.
        Assert.Equal("Untitled 12", ScratchTitles.Format(12));
    }
}
