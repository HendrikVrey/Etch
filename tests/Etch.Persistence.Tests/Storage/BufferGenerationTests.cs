using Etch.Persistence.Model;
using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Storage;

/// <summary>
/// The retained previous generation of a buffer.
/// </summary>
/// <remarks>
/// The journal is an unattended overwrite loop with no human ever confirming a save,
/// so a single bad call from the layer above — an empty text change raised before a
/// tab has hydrated is the realistic one — would otherwise replace someone's notes with
/// nothing, permanently. These tests pin the four behaviours that make that
/// recoverable, and the three that stop the extra copy becoming a liability of its own.
/// </remarks>
public sealed class BufferGenerationTests
{
    [Fact]
    public async Task The_first_write_leaves_no_generation_because_there_was_nothing_to_keep()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "first");

        Assert.False(File.Exists(workspace.Paths.BufferBackupFile(id)));
    }

    [Fact]
    public async Task A_bad_write_over_good_text_is_recoverable_from_the_generation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "a long note the user cares about");

        // The failure this exists for: something upstream enqueues an empty document.
        await workspace.Buffers.WriteAsync(id, string.Empty);

        Assert.Equal(
            "a long note the user cares about",
            await File.ReadAllTextAsync(workspace.Paths.BufferBackupFile(id)));
    }

    [Fact]
    public async Task A_missing_live_file_falls_back_to_the_generation()
    {
        // The signature of a process that died between the rotate and the publish.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "before the crash");
        await workspace.Buffers.WriteAsync(id, "after the crash");

        File.Delete(workspace.Paths.BufferFile(id));

        var stored = await workspace.Buffers.ReadAsync(id);

        Assert.NotNull(stored);
        Assert.Equal("before the crash", stored!.Value.Text);
        Assert.True(stored.Value.RecoveredFromBackup);
    }

    [Fact]
    public async Task A_live_file_always_wins_over_the_generation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "old");
        await workspace.Buffers.WriteAsync(id, "new");

        var stored = await workspace.Buffers.ReadAsync(id);

        Assert.Equal("new", stored!.Value.Text);
        Assert.False(stored.Value.RecoveredFromBackup);
    }

    [Fact]
    public async Task A_generation_with_no_live_file_is_still_enumerated_as_a_buffer()
    {
        // Otherwise the restore would not know the buffer exists, and the fallback above
        // would never be reached for a tab the session index had also lost.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "one");
        await workspace.Buffers.WriteAsync(id, "two");
        File.Delete(workspace.Paths.BufferFile(id));

        Assert.Equal(id, Assert.Single(workspace.Buffers.EnumerateLive()));
        Assert.True(workspace.Buffers.Exists(id));
    }

    [Fact]
    public async Task Trashing_a_buffer_takes_its_generation_with_it()
    {
        // A generation left behind would be adopted as a recovered tab on the next
        // launch, resurrecting a tab the user closed — with text one revision stale.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "one");
        await workspace.Buffers.WriteAsync(id, "two");

        Assert.True(workspace.Buffers.Trash(id, RetentionPolicy.Default, DateTimeOffset.UtcNow));

        Assert.False(File.Exists(workspace.Paths.BufferBackupFile(id)));
        Assert.Empty(workspace.Buffers.EnumerateLive());
    }

    [Fact]
    public async Task Trashing_falls_back_to_the_generation_when_the_live_file_is_gone()
    {
        // Closing a tab after a crash inside the rename window still has to be
        // reopenable — that is precisely when the user most needs it to be.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "one");
        await workspace.Buffers.WriteAsync(id, "two");
        File.Delete(workspace.Paths.BufferFile(id));

        Assert.True(workspace.Buffers.Trash(id, RetentionPolicy.Default, DateTimeOffset.UtcNow));

        var trashed = await workspace.Buffers.ReadTrashedAsync(id);
        Assert.Equal("one", trashed!.Value.Text);
    }

    [Fact]
    public async Task Deleting_live_text_deletes_the_generation_too()
    {
        // DeleteLive backs the ephemeral toggle and the undo of a write that lost a race
        // with a close. Leaving a full prior revision behind would defeat both.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "one");
        await workspace.Buffers.WriteAsync(id, "two");

        Assert.True(workspace.Buffers.DeleteLive(id));

        Assert.False(File.Exists(workspace.Paths.BufferFile(id)));
        Assert.False(File.Exists(workspace.Paths.BufferBackupFile(id)));
    }

    [Fact]
    public async Task Wiping_removes_the_generation_as_well_as_the_live_text()
    {
        // The copy a user would never think to look for is exactly the one a wipe has to
        // find. It holds a complete prior revision of the same secret.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "an api key");
        await workspace.Buffers.WriteAsync(id, "an api key, edited");

        var result = workspace.Buffers.WipeAll();

        Assert.True(result.IsComplete);
        Assert.False(File.Exists(workspace.Paths.BufferBackupFile(id)));
        Assert.Empty(Directory.GetFiles(workspace.Paths.BuffersDirectory));
    }

    [Fact]
    public void A_backup_file_name_round_trips_and_rejects_near_misses()
    {
        var id = BufferId.New();

        Assert.True(BufferId.TryParseBackupFileName(id.BackupFileName, out var parsed));
        Assert.Equal(id, parsed);

        // The same strictness the live name gets: uppercase and leading whitespace are
        // both legal NTFS names that would rebuild a different path.
        Assert.False(BufferId.TryParseBackupFileName(id.BackupFileName.ToUpperInvariant(), out _));
        Assert.False(BufferId.TryParseBackupFileName(" " + id.BackupFileName, out _));
        Assert.False(BufferId.TryParseBackupFileName(id.FileName, out _));
        Assert.False(BufferId.TryParseFileName(id.BackupFileName, out _));
    }
}
