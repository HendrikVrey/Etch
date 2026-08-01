using System.Text;
using Etch.App.Startup;
using Xunit;

namespace Etch.App.Tests.Startup;

/// <summary>
/// The hand-off protocol between a second launch of Etch and the instance running.
/// </summary>
/// <remarks>
/// The reader runs inside the process holding the user's unsaved text, and the writer
/// is a different process. So this is tested for what it refuses at least as carefully
/// as for what it accepts: every malformed message has to be ignored, never to throw,
/// and never to reach a file open.
/// </remarks>
public class InstanceRequestTests
{
    private static byte[] Encode(string message) => Encoding.UTF8.GetBytes(message);

    [Fact]
    public void An_activate_request_round_trips()
    {
        Assert.True(InstanceRequest.TryParse(new InstanceRequest.Activate().ToBytes(), out var parsed));
        Assert.IsType<InstanceRequest.Activate>(parsed);
    }

    [Fact]
    public void An_open_request_round_trips_with_its_path()
    {
        var request = new InstanceRequest.Open(@"C:\Users\Test\notes.txt");

        Assert.True(InstanceRequest.TryParse(request.ToBytes(), out var parsed));

        var open = Assert.IsType<InstanceRequest.Open>(parsed);
        Assert.Equal(@"C:\Users\Test\notes.txt", open.Path);
    }

    [Fact]
    public void A_path_containing_spaces_survives_because_it_is_the_rest_of_the_line()
    {
        var request = new InstanceRequest.Open(@"C:\Users\Test\my notes 2026.txt");

        Assert.True(InstanceRequest.TryParse(request.ToBytes(), out var parsed));
        Assert.Equal(@"C:\Users\Test\my notes 2026.txt", Assert.IsType<InstanceRequest.Open>(parsed).Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ACTIVATE")]
    [InlineData("ETCH1")]
    [InlineData("ETCH1 ")]
    [InlineData("ETCH2 ACTIVATE")]
    [InlineData("ETCH1 activate")]
    [InlineData("ETCH1 SHUTDOWN")]
    [InlineData("ETCH1 OPEN")]
    [InlineData("ETCH1 OPEN ")]
    [InlineData("ETCH1 OPENC:\\notes.txt")]
    public void A_message_that_is_not_exactly_right_is_refused(string message)
    {
        Assert.False(InstanceRequest.TryParse(Encode(message), out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData(@"ETCH1 OPEN \\.\PhysicalDrive0")]
    [InlineData(@"ETCH1 OPEN \\?\C:\notes.txt")]
    public void A_device_path_is_refused(string message)
    {
        // These never come from a file dialog and reach things that are not files.
        Assert.False(InstanceRequest.TryParse(Encode(message), out _));
    }

    [Theory]
    [InlineData(@"ETCH1 OPEN ..\..\secrets.txt")]
    [InlineData("ETCH1 OPEN notes.txt")]
    [InlineData(@"ETCH1 OPEN C:\Users\..\Users\Test\notes.txt")]
    [InlineData(@"ETCH1 OPEN C:\Users\Test\notes.txt ")]
    public void A_path_that_is_not_already_canonical_and_absolute_is_refused(string message)
    {
        // Resolution happens against the working directory, and the two processes do not
        // share one — so a relative path that "worked" here would open a different file
        // from the one the sender meant. Trailing spaces are stripped by Windows, which
        // is how a check on one string ends up guarding an open of another.
        Assert.False(InstanceRequest.TryParse(Encode(message), out _));
    }

    [Fact]
    public void An_oversized_message_is_refused_without_being_parsed()
    {
        var message = Encode("ETCH1 OPEN C:\\" + new string('a', InstanceRequest.MaxMessageBytes));

        Assert.False(InstanceRequest.TryParse(message, out _));
    }

    [Fact]
    public void Invalid_utf8_is_refused_rather_than_replaced()
    {
        // Substituting U+FFFD would produce a path that names a different file, and
        // opening the wrong file is worse than ignoring the request.
        byte[] message = [.. Encode("ETCH1 OPEN C:\\"), 0xFF, 0xFE, 0xFD];

        Assert.False(InstanceRequest.TryParse(message, out _));
    }

    [Fact]
    public void A_null_character_in_a_path_is_refused()
    {
        Assert.False(InstanceRequest.TryParse(Encode("ETCH1 OPEN C:\\notes\0.txt"), out _));
    }

    [Fact]
    public void An_empty_message_is_refused()
    {
        Assert.False(InstanceRequest.TryParse([], out _));
    }
}
