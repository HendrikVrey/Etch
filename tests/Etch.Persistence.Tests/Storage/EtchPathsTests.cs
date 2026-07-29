using Etch.Persistence.Model;
using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Storage;

public class EtchPathsTests
{
    [Fact]
    public void The_layout_matches_the_documented_shape()
    {
        var paths = new EtchPaths(@"C:\data\Etch");

        Assert.Equal(@"C:\data\Etch", paths.Root);
        Assert.Equal(@"C:\data\Etch\buffers", paths.BuffersDirectory);
        Assert.Equal(@"C:\data\Etch\trash", paths.TrashDirectory);
        Assert.Equal(@"C:\data\Etch\session.json", paths.SessionFile);
    }

    [Fact]
    public void A_trailing_separator_is_normalised_away()
    {
        Assert.Equal(
            new EtchPaths(@"C:\data\Etch").Root,
            new EtchPaths(@"C:\data\Etch\").Root);
    }

    [Fact]
    public void A_buffer_file_sits_directly_under_the_buffers_directory()
    {
        var paths = new EtchPaths(@"C:\data\Etch");
        var id = BufferId.New();

        var file = paths.BufferFile(id);

        Assert.Equal(paths.BuffersDirectory, Path.GetDirectoryName(file));
        Assert.Equal(id.FileName, Path.GetFileName(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_root_is_rejected(string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EtchPaths(root!));
    }

    [Theory]
    [InlineData("Etch")]
    [InlineData(@"relative\path")]
    [InlineData("./data")]
    public void A_relative_root_is_rejected(string root)
    {
        // It would resolve against the working directory, which for a shell-launched
        // editor is wherever the user happened to be standing.
        Assert.Throws<ArgumentException>(() => new EtchPaths(root));
    }

    [Fact]
    public void Ensuring_creation_is_idempotent()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        workspace.Paths.EnsureCreated();
        workspace.Paths.EnsureCreated();

        Assert.True(Directory.Exists(workspace.Paths.BuffersDirectory));
        Assert.True(Directory.Exists(workspace.Paths.TrashDirectory));
    }

    [Fact]
    public void The_default_layout_lives_under_local_application_data()
    {
        var paths = EtchPaths.CreateDefault();

        Assert.True(Path.IsPathFullyQualified(paths.Root));
        Assert.Equal("Etch", Path.GetFileName(paths.Root));
    }
}
