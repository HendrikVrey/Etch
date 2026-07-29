using Etch.Persistence.Model;
using Xunit;

namespace Etch.Persistence.Tests.Model;

public class BufferIdTests
{
    [Fact]
    public void A_new_id_is_not_empty_and_names_a_file()
    {
        var id = BufferId.New();

        Assert.False(id.IsEmpty);
        Assert.EndsWith(".txt", id.FileName, StringComparison.Ordinal);
        Assert.Equal(36, id.FileName.Length);
    }

    [Fact]
    public void Ids_are_unique()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => BufferId.New()).ToHashSet();

        Assert.Equal(1_000, ids.Count);
    }

    [Fact]
    public void A_file_name_round_trips()
    {
        var original = BufferId.New();

        Assert.True(BufferId.TryParseFileName(original.FileName, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void The_default_value_has_no_file_name()
    {
        var empty = default(BufferId);

        Assert.True(empty.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => empty.FileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notaguid.txt")]
    [InlineData("readme.md")]
    [InlineData("buffer.txt")]
    // A real GUID in the wrong format: round-tripping it through FileName would
    // rename the file, so it is refused rather than normalised.
    [InlineData("8a1d3f2e-4c5b-7d6a-9e0f-1a2b3c4d5e6f.txt")]
    [InlineData("{8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f}.txt")]
    // The all-zero GUID never identifies a buffer.
    [InlineData("00000000000000000000000000000000.txt")]
    // Right shape, wrong extension.
    [InlineData("8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f.json")]
    [InlineData("8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f")]
    // Traversal attempts, which the type exists to make unrepresentable.
    [InlineData("../../etc/passwd.txt")]
    [InlineData("..\\..\\windows\\system32\\config.txt")]
    public void Anything_Etch_did_not_write_is_refused(string? fileName)
    {
        Assert.False(BufferId.TryParseFileName(fileName, out var id));
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public void A_file_name_can_never_escape_its_directory()
    {
        // The security property the whole type exists for: the file name is 32 hex
        // characters plus an extension, so no amount of ingenuity puts a separator
        // or a traversal segment into a path built from it.
        for (var i = 0; i < 500; i++)
        {
            var fileName = BufferId.New().FileName;

            Assert.Equal(fileName, Path.GetFileName(fileName));
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), fileName, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), fileName, StringComparison.Ordinal);
            Assert.DoesNotContain("..", fileName, StringComparison.Ordinal);
            Assert.Equal(-1, fileName.IndexOfAny(Path.GetInvalidFileNameChars()));
        }
    }

    [Theory]
    // Guid.TryParseExact trims before parsing, and both of these are legal NTFS
    // names. Accepting them would produce an id whose FileName is a *different*
    // string, so the trash sweep would delete a file it never looked at and leave
    // the one it did.
    [InlineData(" 8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f.txt")]
    [InlineData("8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f .txt")]
    [InlineData(" 8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f.txt")]
    [InlineData("\t8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f.txt")]
    // Uppercase hex: the same file on Windows, a different one on a case-sensitive
    // directory, and a different string either way.
    [InlineData("8A1D3F2E4C5B7D6A9E0F1A2B3C4D5E6F.txt")]
    public void A_name_that_would_not_round_trip_byte_for_byte_is_refused(string fileName)
    {
        Assert.False(BufferId.TryParseFileName(fileName, out _));
    }

    [Fact]
    public void Every_accepted_name_round_trips_byte_for_byte()
    {
        // The invariant every caller relies on: parse a name from a directory
        // listing, then act on the path rebuilt from the id, and be certain it is
        // the same file.
        for (var i = 0; i < 200; i++)
        {
            var fileName = BufferId.New().FileName;

            Assert.True(BufferId.TryParseFileName(fileName, out var parsed));
            Assert.Equal(fileName, parsed.FileName, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Wrapping_an_empty_guid_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BufferId.FromGuid(Guid.Empty));
    }

    [Fact]
    public void Wrapping_a_real_guid_preserves_it()
    {
        var value = Guid.CreateVersion7();

        Assert.Equal(value, BufferId.FromGuid(value).Value);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var value = Guid.CreateVersion7();

        Assert.Equal(BufferId.FromGuid(value), BufferId.FromGuid(value));
        Assert.NotEqual(BufferId.New(), BufferId.New());
    }
}
