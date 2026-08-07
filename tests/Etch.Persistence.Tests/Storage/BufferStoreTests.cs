using Etch.Persistence.Model;
using Etch.Persistence.Storage;
using Xunit;

namespace Etch.Persistence.Tests.Storage;

public class BufferStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Text_survives_a_write_and_read()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "scratch contents", TestContext.Current.CancellationToken);
        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.NotNull(stored);
        Assert.Equal("scratch contents", stored!.Value.Text);
        Assert.False(stored.Value.WasTruncated);
    }

    [Fact]
    public async Task Reading_a_buffer_that_is_not_there_returns_null_rather_than_throwing()
    {
        // Normal after a crash between the index write and the buffer write, so it
        // must not be an exception path.
        using var workspace = TemporaryWorkspace.Create();

        Assert.Null(await workspace.Buffers.ReadAsync(BufferId.New(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Line_endings_and_unicode_round_trip_unchanged()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();
        const string Text = "crlf\r\nlf\nemoji 🜃\ttab\ttrailing   ";

        await workspace.Buffers.WriteAsync(id, Text, TestContext.Current.CancellationToken);
        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(Text, stored!.Value.Text);
    }

    [Fact]
    public async Task A_leading_zero_width_no_break_space_is_not_eaten()
    {
        // Byte-order-mark detection on read would strip it, the journal would write
        // the stripped text back, and one character of the user's text would be gone
        // permanently across a restart.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();
        const string Text = "﻿this begins with a real U+FEFF";

        await workspace.Buffers.WriteAsync(id, Text, TestContext.Current.CancellationToken);
        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(Text, stored!.Value.Text);
    }

    [Fact]
    public async Task Enumerating_finds_written_buffers_and_ignores_foreign_files()
    {
        using var workspace = TemporaryWorkspace.Create();
        var first = BufferId.New();
        var second = BufferId.New();

        await workspace.Buffers.WriteAsync(first, "one", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(second, "two", TestContext.Current.CancellationToken);

        // Things Etch did not write must be invisible to it: the enumeration feeds a
        // sweep that deletes, and a delete primitive pointed at arbitrary files is
        // how a scratchpad becomes a liability.
        await File.WriteAllTextAsync(Path.Combine(workspace.Paths.BuffersDirectory, "notes.txt"), "not mine", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Paths.BuffersDirectory, "readme.md"), "not mine", TestContext.Current.CancellationToken);

        var live = workspace.Buffers.EnumerateLive();

        Assert.Equal(2, live.Count);
        Assert.Contains(first, live);
        Assert.Contains(second, live);
    }

    [Fact]
    public async Task A_non_canonical_file_name_is_not_adopted()
    {
        // Guid.TryParseExact trims whitespace and accepts uppercase, and both are
        // legal in an NTFS name. If such a file were adopted, its id would rebuild a
        // *different* canonical path — so the prune would delete a file it never
        // looked at and leave the one it did.
        using var workspace = TemporaryWorkspace.Create();
        var real = BufferId.New();
        var stem = real.ToString();

        await workspace.Buffers.WriteAsync(real, "the genuine article", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Paths.BuffersDirectory, $" {stem}.txt"), "impostor", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Paths.BuffersDirectory, $"{stem.ToUpperInvariant()}.txt"),
            "impostor",
            TestContext.Current.CancellationToken);

        var live = workspace.Buffers.EnumerateLive();

        Assert.Equal(real, Assert.Single(live));
    }

    [Fact]
    public async Task Closing_a_tab_moves_it_to_the_trash_rather_than_deleting_it()
    {
        // This is what makes "no confirmation dialogs" safe. Close has to be
        // reversible or the promise is reckless rather than convenient.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "closed by accident", TestContext.Current.CancellationToken);

        Assert.True(workspace.Buffers.Trash(id, RetentionPolicy.Default, Now));
        Assert.False(workspace.Buffers.Exists(id));

        var trashed = await workspace.Buffers.ReadTrashedAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal("closed by accident", trashed!.Value.Text);
    }

    [Fact]
    public async Task Retention_starts_at_the_close_not_at_the_last_edit()
    {
        // The bug this guards: File.Move preserves the last-write time, so a note
        // last typed in nine days ago would be expired the instant it was closed and
        // deleted on the next launch. The tabs with the most time invested in them
        // would get the least protection — precisely backwards.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "a reference note I have kept for weeks", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(workspace.Paths.BufferFile(id), Now.AddDays(-30).UtcDateTime);

        workspace.Buffers.Trash(id, RetentionPolicy.Default, Now);

        Assert.Equal(0, workspace.Buffers.PruneTrash(RetentionPolicy.Default, Now));
        Assert.NotNull(await workspace.Buffers.ReadTrashedAsync(id, TestContext.Current.CancellationToken));

        // And it still expires on schedule, counted from the close.
        Assert.Equal(1, workspace.Buffers.PruneTrash(RetentionPolicy.Default, Now.AddDays(8)));
    }

    [Fact]
    public async Task A_trashed_buffer_can_be_restored()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "reopen me", TestContext.Current.CancellationToken);
        workspace.Buffers.Trash(id, RetentionPolicy.Default, Now);

        Assert.True(workspace.Buffers.Restore(id));
        Assert.True(workspace.Buffers.Exists(id));

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("reopen me", stored!.Value.Text);
    }

    [Fact]
    public async Task Restoring_over_a_live_buffer_refuses_rather_than_overwriting_it()
    {
        // The id is already open and has been edited since. Replacing it with the
        // trashed copy would silently discard whatever was typed in between.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "trashed version", TestContext.Current.CancellationToken);
        workspace.Buffers.Trash(id, RetentionPolicy.Default, Now);
        await workspace.Buffers.WriteAsync(id, "live version", TestContext.Current.CancellationToken);

        Assert.False(workspace.Buffers.Restore(id));

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("live version", stored!.Value.Text);
    }

    [Fact]
    public async Task Zero_retention_deletes_on_close_and_says_it_is_not_recoverable()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "secret token", TestContext.Current.CancellationToken);

        // False, so the UI does not offer a reopen that would do nothing.
        Assert.False(workspace.Buffers.Trash(id, RetentionPolicy.DeleteImmediately, Now));
        Assert.False(workspace.Buffers.Exists(id));
        Assert.Null(await workspace.Buffers.ReadTrashedAsync(id, TestContext.Current.CancellationToken));
        Assert.Empty(workspace.Buffers.EnumerateTrash());
    }

    [Fact]
    public void Trashing_a_buffer_that_does_not_exist_reports_false()
    {
        using var workspace = TemporaryWorkspace.Create();

        Assert.False(workspace.Buffers.Trash(BufferId.New(), RetentionPolicy.Default, Now));
    }

    [Fact]
    public async Task Expired_trash_is_pruned_and_fresh_trash_is_kept()
    {
        using var workspace = TemporaryWorkspace.Create();
        var stale = BufferId.New();
        var fresh = BufferId.New();

        await workspace.Buffers.WriteAsync(stale, "old", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(fresh, "recent", TestContext.Current.CancellationToken);

        workspace.Buffers.Trash(stale, RetentionPolicy.Default, Now.AddDays(-30));
        workspace.Buffers.Trash(fresh, RetentionPolicy.Default, Now.AddHours(-1));

        var pruned = workspace.Buffers.PruneTrash(RetentionPolicy.Default, Now);

        Assert.Equal(1, pruned);
        Assert.Null(await workspace.Buffers.ReadTrashedAsync(stale, TestContext.Current.CancellationToken));
        Assert.NotNull(await workspace.Buffers.ReadTrashedAsync(fresh, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_live_text_removes_it_without_trashing_it()
    {
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Buffers.WriteAsync(id, "written by a write that lost a race", TestContext.Current.CancellationToken);

        Assert.True(workspace.Buffers.DeleteLive(id));
        Assert.False(workspace.Buffers.Exists(id));
        Assert.Empty(workspace.Buffers.EnumerateTrash());
    }

    [Fact]
    public async Task Wiping_removes_buffers_trash_and_the_session_index()
    {
        // The privacy affordance. Leaving session.json behind would defeat it: it
        // holds tab titles and, for file buffers, full paths — a tab called
        // "prod-db-password" surviving a wipe is the whole problem.
        using var workspace = TemporaryWorkspace.Create();
        var live = BufferId.New();
        var trashed = BufferId.New();

        await workspace.Buffers.WriteAsync(live, "live", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(trashed, "trashed", TestContext.Current.CancellationToken);
        workspace.Buffers.Trash(trashed, RetentionPolicy.Default, Now);
        await File.WriteAllTextAsync(workspace.Paths.SessionFile, "{}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            workspace.Paths.QuarantinedSessionFile(Now, "abcd"),
            "{ an older index nothing else ever deletes }",
            TestContext.Current.CancellationToken);

        var result = workspace.Buffers.WipeAll();

        Assert.True(result.IsComplete);
        Assert.Equal(4, result.Deleted);
        Assert.Empty(workspace.Buffers.EnumerateLive());
        Assert.Empty(workspace.Buffers.EnumerateTrash());
        Assert.False(File.Exists(workspace.Paths.SessionFile));
        Assert.Empty(workspace.Paths.EnumerateQuarantinedSessions());
    }

    [Fact]
    public void Initialising_creates_the_tree_and_clears_partial_writes()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        Assert.False(Directory.Exists(workspace.Paths.BuffersDirectory));

        workspace.Buffers.Initialise();

        Assert.True(Directory.Exists(workspace.Paths.BuffersDirectory));
        Assert.True(Directory.Exists(workspace.Paths.TrashDirectory));

        File.WriteAllText(Path.Combine(workspace.Paths.BuffersDirectory, "abc.txt.zzzz1111.tmp"), "partial");

        Assert.Equal(1, workspace.Buffers.Initialise());
        Assert.Empty(Directory.GetFiles(workspace.Paths.BuffersDirectory, "*.tmp"));
    }

    [Fact]
    public void Enumerating_an_uninitialised_store_returns_nothing_rather_than_throwing()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        Assert.Empty(workspace.Buffers.EnumerateLive());
        Assert.Empty(workspace.Buffers.EnumerateTrash());
    }

    [Fact]
    public async Task Trash_is_listed_newest_first()
    {
        using var workspace = TemporaryWorkspace.Create();
        var older = BufferId.New();
        var newer = BufferId.New();

        await workspace.Buffers.WriteAsync(older, "older", TestContext.Current.CancellationToken);
        await workspace.Buffers.WriteAsync(newer, "newer", TestContext.Current.CancellationToken);

        workspace.Buffers.Trash(older, RetentionPolicy.Default, Now.AddHours(-3));
        workspace.Buffers.Trash(newer, RetentionPolicy.Default, Now.AddHours(-1));

        var trash = workspace.Buffers.EnumerateTrash();

        // Reopen-last-closed depends on this order.
        Assert.Equal(newer, trash[0].Id);
        Assert.Equal(older, trash[1].Id);
    }

    [Fact]
    public async Task Writing_the_same_buffer_repeatedly_leaves_the_live_file_and_one_generation()
    {
        // Twenty writes must not leave twenty files. Exactly two: the current text and
        // the single retained previous revision that makes a bad write recoverable.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        for (var i = 0; i < 20; i++)
        {
            await workspace.Buffers.WriteAsync(id, $"revision {i}", TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, Directory.GetFiles(workspace.Paths.BuffersDirectory).Length);
        Assert.True(File.Exists(workspace.Paths.BufferBackupFile(id)));

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("revision 19", stored!.Value.Text);
        Assert.False(stored.Value.RecoveredFromBackup);

        Assert.Equal("revision 18", await File.ReadAllTextAsync(workspace.Paths.BufferBackupFile(id), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_open_read_does_not_block_the_journal_from_replacing_the_file()
    {
        // Without FileShare.Delete on the read, the rename that publishes a new
        // revision fails with a sharing violation and the journal reports a write
        // failure for a completely ordinary interleaving.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();
        var path = workspace.Paths.BufferFile(id);

        await workspace.Buffers.WriteAsync(id, "first revision", TestContext.Current.CancellationToken);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await workspace.Buffers.WriteAsync(id, "second revision", TestContext.Current.CancellationToken);
        }

        var stored = await workspace.Buffers.ReadAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("second revision", stored!.Value.Text);
    }
}
