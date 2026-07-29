using Etch.App.Startup;
using Xunit;

namespace Etch.App.Tests.Startup;

/// <summary>
/// The command line is where untrusted input enters Etch, so it is tested for what
/// it refuses at least as thoroughly as for what it accepts.
/// </summary>
public class CommandLineParserTests
{
    [Fact]
    public void No_arguments_opens_an_empty_editor()
    {
        var edit = Assert.IsType<LaunchMode.Edit>(Parse().Mode);

        Assert.Null(edit.FileToOpen);
    }

    [Fact]
    public void Diag_is_accepted_and_does_not_change_the_mode()
    {
        // Main acts on --diag before parsing, so the parser's only job is to not
        // treat it as an unknown option.
        var edit = Assert.IsType<LaunchMode.Edit>(Parse(CommandLineParser.DiagnosticsFlag, "notes.txt").Mode);

        Assert.NotNull(edit.FileToOpen);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void Help_is_recognised_in_every_spelling(string arg)
    {
        Assert.IsType<LaunchMode.ShowHelp>(Parse(arg).Mode);
    }

    [Fact]
    public void A_positional_argument_is_resolved_to_an_absolute_path()
    {
        var edit = Assert.IsType<LaunchMode.Edit>(Parse("notes.txt").Mode);

        Assert.NotNull(edit.FileToOpen);
        Assert.True(Path.IsPathFullyQualified(edit.FileToOpen!));
        Assert.EndsWith("notes.txt", edit.FileToOpen!, StringComparison.Ordinal);
    }

    [Fact]
    public void Traversal_segments_are_normalised_away()
    {
        var edit = Assert.IsType<LaunchMode.Edit>(Parse(@"a\b\..\..\c.txt").Mode);

        Assert.DoesNotContain("..", edit.FileToOpen!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gen_sample_defaults_to_fifty_mebibytes_of_json_without_force()
    {
        var request = Assert.IsType<LaunchMode.GenerateSample>(Parse("--gen-sample", "out.ndjson").Mode);

        Assert.Equal(50, request.Mebibytes);
        Assert.Equal(SampleShape.Json, request.Shape);
        Assert.False(request.Force);
        Assert.True(Path.IsPathFullyQualified(request.Path));
    }

    [Fact]
    public void Gen_sample_accepts_size_shape_and_force()
    {
        var request = Assert.IsType<LaunchMode.GenerateSample>(
            Parse("--gen-sample", "out.txt", "--size", "120", "--shape", "TEXT", "--force").Mode);

        Assert.Equal(120, request.Mebibytes);
        Assert.Equal(SampleShape.Text, request.Shape);
        Assert.True(request.Force);
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("--unknown")]
    public void Unknown_options_are_rejected(string arg)
    {
        AssertRejected(arg);
    }

    [Fact]
    public void Only_one_file_may_be_opened()
    {
        AssertRejected("a.txt", "b.txt");
    }

    [Theory]
    [InlineData("--gen-sample")]
    [InlineData("--size")]
    [InlineData("--shape")]
    public void Options_that_need_a_value_say_so(string arg)
    {
        AssertRejected(arg);
    }

    [Fact]
    public void An_option_is_never_swallowed_as_the_previous_options_value()
    {
        // Without this guard, `--gen-sample --size 100` resolves a file literally
        // named "--size" and then fails on "100" with a baffling message.
        var error = AssertRejected("--gen-sample", "--size", "100");

        Assert.Contains("--size", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2049")]
    [InlineData("abc")]
    [InlineData("50.5")]
    [InlineData("")]
    public void Size_is_bounded_and_must_be_a_whole_number(string size)
    {
        AssertRejected("--gen-sample", "out.bin", "--size", size);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2048")]
    public void Size_accepts_both_ends_of_the_permitted_range(string size)
    {
        var request = Assert.IsType<LaunchMode.GenerateSample>(
            Parse("--gen-sample", "out.bin", "--size", size).Mode);

        Assert.Equal(int.Parse(size, System.Globalization.CultureInfo.InvariantCulture), request.Mebibytes);
    }

    [Theory]
    [InlineData("7")]        // Enum.TryParse accepts numeric strings; IsDefined must catch it
    [InlineData("binary")]
    public void Shape_rejects_anything_that_is_not_a_defined_member(string shape)
    {
        AssertRejected("--gen-sample", "out.bin", "--shape", shape);
    }

    [Fact]
    public void Help_cannot_be_combined_with_other_work()
    {
        AssertRejected("--help", "--gen-sample", "out.bin");
        AssertRejected("--help", "notes.txt");
    }

    [Fact]
    public void Gen_sample_cannot_also_open_a_file()
    {
        AssertRejected("--gen-sample", "out.bin", "notes.txt");
    }

    [Theory]
    [InlineData("--size", "100")]
    [InlineData("--shape", "text")]
    public void Sample_options_without_gen_sample_are_an_error_not_a_silent_no_op(string option, string value)
    {
        var error = AssertRejected(option, value);

        Assert.Contains("--gen-sample", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_without_gen_sample_is_an_error_not_a_silent_no_op()
    {
        // --force only ever means "replace the fixture". Accepting it silently on an
        // ordinary launch would leave the user believing they had disarmed a guard
        // that was never armed.
        var error = AssertRejected("--force", "notes.txt");

        Assert.Contains("--gen-sample", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\System32\config\SAM")]
    public void Device_paths_are_refused(string path)
    {
        var error = AssertRejected(path);

        Assert.Contains("device", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_paths_are_refused(string path)
    {
        AssertRejected("--gen-sample", path);
    }

    [Fact]
    public void Null_arguments_are_a_programming_error_not_a_parse_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => CommandLineParser.TryParse(null!, out _, out _));
    }

    private static CommandLineOptions Parse(params string[] args)
    {
        Assert.True(CommandLineParser.TryParse(args, out var options, out var error), error);

        return options!;
    }

    private static string AssertRejected(params string[] args)
    {
        Assert.False(CommandLineParser.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        return error!;
    }
}
