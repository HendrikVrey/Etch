using System.Text;
using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Storage;

public class AtomicFileTests
{
    [Fact]
    public async Task Writes_a_new_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "new.txt");

        await AtomicFile.WriteAllTextAsync(path, "hello", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replaces_an_existing_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "existing.txt");

        await File.WriteAllTextAsync(path, "old contents that are much longer than the new ones", TestContext.Current.CancellationToken);
        await AtomicFile.WriteAllTextAsync(path, "new", cancellationToken: TestContext.Current.CancellationToken);

        // Not merely "starts with": a truncating write that failed to shorten the
        // file would leave the tail of the old contents behind.
        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writes_utf8_without_a_byte_order_mark()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "encoding.txt");

        await AtomicFile.WriteAllTextAsync(path, "héllo — ünïcode ✓", cancellationToken: TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Equal("héllo — ünïcode ✓", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Writes_an_empty_file_rather_than_skipping_it()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "empty.txt");

        await File.WriteAllTextAsync(path, "not empty", TestContext.Current.CancellationToken);
        await AtomicFile.WriteAllTextAsync(path, string.Empty, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_no_temporary_file_behind_on_success()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "clean.txt");

        await AtomicFile.WriteAllTextAsync(path, "contents", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
        Assert.Single(Directory.GetFiles(workspace.Root));
    }

    [Fact]
    public async Task Leaves_the_original_intact_when_the_write_is_cancelled()
    {
        // The property that matters most: a write that does not finish must not be
        // able to damage what was already on disk.
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "survivor.txt");

        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtomicFile.WriteAllTextAsync(path, "replacement", backupPath: null, cancelled.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public async Task Fails_without_damaging_the_original_when_the_directory_is_missing()
    {
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "absent", "file.txt");

        await Assert.ThrowsAnyAsync<IOException>(() => AtomicFile.WriteAllTextAsync(path, "contents", cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_interleave_into_one_temporary_file()
    {
        // Temporary names are unique per write, not "<destination>.tmp". With a
        // shared name two writers would append into the same scratch file and the
        // rename would publish the mixture.
        using var workspace = TemporaryWorkspace.Create();
        var path = Path.Combine(workspace.Root, "contended.txt");

        var first = new string('a', 200_000);
        var second = new string('b', 200_000);

        await Task.WhenAll(
            AtomicFile.WriteAllTextAsync(path, first, cancellationToken: TestContext.Current.CancellationToken),
            AtomicFile.WriteAllTextAsync(path, second, cancellationToken: TestContext.Current.CancellationToken));

        var written = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            written == first || written == second,
            "The published file must be exactly one of the two writes, never a blend of both.");
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void Sweeping_removes_orphaned_temporary_files_only()
    {
        using var workspace = TemporaryWorkspace.Create();

        File.WriteAllText(Path.Combine(workspace.Root, "buffer.txt"), "keep me");
        File.WriteAllText(Path.Combine(workspace.Root, "buffer.txt.abcd1234.tmp"), "orphan");
        File.WriteAllText(Path.Combine(workspace.Root, "session.json"), "keep me too");

        var swept = AtomicFile.SweepTemporaryFiles(workspace.Root);

        Assert.Equal(1, swept);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "buffer.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.Root, "session.json")));
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    [Fact]
    public void Sweeping_a_missing_directory_is_not_an_error()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        Assert.Equal(0, AtomicFile.SweepTemporaryFiles(Path.Combine(workspace.Root, "never-created")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_path_is_rejected(string? path)
    {
        // ThrowsAny, not Throws: a null path raises ArgumentNullException, which is a
        // subclass, and the exact-type overload would fail on it.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => AtomicFile.WriteAllTextAsync(path!, "contents", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Null_contents_are_rejected()
    {
        using var workspace = TemporaryWorkspace.Create();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => AtomicFile.WriteAllTextAsync(Path.Combine(workspace.Root, "x.txt"), null!, cancellationToken: TestContext.Current.CancellationToken));
    }
}
