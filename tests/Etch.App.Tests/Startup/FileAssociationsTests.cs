using Etch.App.Startup;
using Xunit;

namespace Etch.App.Tests.Startup;

/// <summary>
/// The parts of <see cref="FileAssociations"/> that can be asserted without writing to
/// the registry of whoever is running the suite.
/// </summary>
/// <remarks>
/// Nothing here calls <c>Set</c>. That method changes machine state outside Etch's own
/// data directory, and a test suite that quietly re-associated a developer's <c>.json</c>
/// files would be indefensible however carefully it cleaned up afterwards. What is
/// covered is the whitelist and the labels, which is where the actual bugs would be,
/// because everything else is a registry call whose behaviour belongs to Windows.
/// </remarks>
public class FileAssociationsTests
{
    [Fact]
    public void Every_associable_extension_has_a_label_of_its_own()
    {
        // Describe throws on an unknown extension, so an extension added to Associable
        // without a label fails here rather than shipping as a blank row in the settings
        // panel: a blank checkbox that changes the user's file associations.
        var labels = FileAssociations.Associable.Select(FileAssociations.Describe).ToArray();

        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_whitelist_is_the_three_a_scratchpad_is_plausibly_right_for()
    {
        // Written out rather than derived, so widening it is a deliberate act. Every
        // registry path in this type is built by concatenation with an extension in it,
        // and this list is what makes that safe, so growing it should require editing a
        // test that says why it is short.
        Assert.Equal(new[] { ".txt", ".json", ".log" }, FileAssociations.Associable);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".cs")]
    [InlineData("txt")]
    [InlineData(".TXT")]
    [InlineData("")]
    [InlineData(@"..\..\Windows")]
    public void An_extension_outside_the_whitelist_is_refused(string extension)
    {
        // Case included deliberately: the whitelist is compared ordinally, so ".TXT" is
        // not ".txt". That is the stricter reading and the right one here: the key path
        // written to the registry should be the exact string in the list, not whatever
        // casing arrived.
        Assert.Throws<ArgumentOutOfRangeException>(() => FileAssociations.Set(extension, associate: true));

        // IsHonoured is the lenient half of the pair on purpose: it is asked by a settings
        // panel that must open regardless, so it answers false rather than throwing.
        Assert.False(FileAssociations.IsHonoured(extension));
    }

    [Fact]
    public void Describe_refuses_an_extension_it_has_no_label_for()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileAssociations.Describe(".md"));
    }
}
