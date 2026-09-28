using System.Text;
using Etch.Persistence.Model;
using Etch.Persistence.Settings;
using Xunit;

namespace Etch.Persistence.Tests.Settings;

/// <summary>
/// <see cref="SettingsStore"/>, whose contract is that it never throws: it runs on the
/// startup path, and an exception escaping it is an Etch that will not start until the
/// user finds and deletes a file they have never heard of.
/// </summary>
public class SettingsStoreTests
{
    [Fact]
    public async Task A_missing_file_is_not_an_error()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.Equal(EtchSettings.Default, result.Settings);

        // First launch is the common case. A message about it would be noise on every
        // machine that has never opened the settings panel.
        Assert.Null(result.Notice);
        Assert.True(result.CanSave);
    }

    [Fact]
    public async Task A_missing_directory_is_not_an_error_either()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task A_saved_file_round_trips()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new SettingsStore(workspace.Paths);

        var saved = EtchSettings.Default with
        {
            TrashRetentionDays = 0,
            Ligatures = false,
            ReducedThresholdBytes = 512 * 1024,
        };

        await store.SaveAsync(saved, TestContext.Current.CancellationToken);

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(saved, result.Settings);
    }

    [Fact]
    public async Task Saving_creates_the_directory_tree()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        await new SettingsStore(workspace.Paths).SaveAsync(
            EtchSettings.Default,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(workspace.Paths.SettingsFile));
    }

    [Fact]
    public async Task Malformed_json_falls_back_to_the_defaults_and_leaves_the_file_alone()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            "{ this is not json",
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Unusable, result.Status);
        Assert.Equal(EtchSettings.Default, result.Settings);
        Assert.NotNull(result.Notice);

        // Unlike the session index, this is NOT quarantined. The likeliest cause is a
        // typo in a file the user was invited to edit, and the fix is to correct it:
        // renaming it out from under them turns a misplaced comma into lost preferences.
        Assert.True(File.Exists(workspace.Paths.SettingsFile));
    }

    [Fact]
    public async Task Values_out_of_range_are_clamped_rather_than_rejected()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            """
            {
              "version": 1,
              "trashRetentionDays": -9,
              "ligatures": false,
              "reducedThresholdBytes": 2097152,
              "plainTextThresholdBytes": 10485760,
              "hardCeilingBytes": 104857600
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);

        // The bad field falls back; the good one beside it survives. That is the whole
        // point of clamping rather than rejecting the file.
        Assert.Equal(EtchSettings.Default.TrashRetentionDays, result.Settings.TrashRetentionDays);
        Assert.False(result.Settings.Ligatures);
    }

    [Fact]
    public async Task A_hand_edited_file_may_have_comments_and_a_trailing_comma()
    {
        using var workspace = TemporaryWorkspace.Create();

        // The file is documented as editable, so it will acquire both. Rejecting it for
        // either would be a poor answer to an invitation Etch itself extended.
        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            """
            {
              // Keep nothing after I close a tab.
              "version": 1,
              "trashRetentionDays": 0,
              "ligatures": true,
              "reducedThresholdBytes": 2097152,
              "plainTextThresholdBytes": 10485760,
              "hardCeilingBytes": 104857600,
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(0, result.Settings.TrashRetentionDays);
    }

    [Fact]
    public async Task A_byte_order_mark_is_skipped()
    {
        using var workspace = TemporaryWorkspace.Create();

        // What somebody gets for opening settings.json in Notepad to see what is in
        // there and pressing save. The parser rejects a BOM outright.
        var json = """
            {
              "version": 1,
              "trashRetentionDays": 3,
              "ligatures": true,
              "reducedThresholdBytes": 2097152,
              "plainTextThresholdBytes": 10485760,
              "hardCeilingBytes": 104857600
            }
            """;

        await File.WriteAllBytesAsync(
            workspace.Paths.SettingsFile,
            [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(json)],
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(3, result.Settings.TrashRetentionDays);
    }

    [Fact]
    public async Task A_file_from_a_newer_build_is_used_read_only()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            $$"""
            {
              "version": {{EtchSettings.CurrentVersion + 1}},
              "trashRetentionDays": 30,
              "ligatures": false,
              "reducedThresholdBytes": 2097152,
              "plainTextThresholdBytes": 10485760,
              "hardCeilingBytes": 104857600
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.FromFutureVersion, result.Status);
        Assert.Equal(EtchSettings.Default, result.Settings);
        Assert.NotNull(result.Notice);

        // The point of the whole branch: this build must not write over settings it does
        // not understand, because it would silently drop every field it has no name for.
        Assert.False(result.CanSave);
    }

    [Fact]
    public async Task An_absurdly_large_file_is_refused_without_being_parsed()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            new string('x', 512 * 1024),
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Unusable, result.Status);
        Assert.Equal(EtchSettings.Default, result.Settings);
    }

    [Fact]
    public async Task An_empty_file_reads_as_missing()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            string.Empty,
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        // An empty file and no file mean the same thing to the user, and the next save
        // replaces it either way. A notice here would be alarming and useless.
        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Json_null_reads_as_missing()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SettingsFile,
            "null",
            TestContext.Current.CancellationToken);

        var result = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Missing, result.Status);
    }

    [Fact]
    public async Task Saving_sanitises_so_a_written_file_can_always_be_read_back()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new SettingsStore(workspace.Paths);

        // A caller that skipped Sanitised. The store must not write a file it would then
        // refuse to load.
        await store.SaveAsync(
            EtchSettings.Default with { TrashRetentionDays = -1, HardCeilingBytes = 1 },
            TestContext.Current.CancellationToken);

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(EtchSettings.Default, result.Settings);
    }

    [Fact]
    public async Task Settings_are_written_beside_the_session_but_are_not_the_session()
    {
        using var workspace = TemporaryWorkspace.Create();

        await new SettingsStore(workspace.Paths).SaveAsync(
            EtchSettings.Default,
            TestContext.Current.CancellationToken);

        // Two distinct files under one root. The wipe deletes one of them and not the
        // other, so them being the same path would be a quiet disaster.
        Assert.NotEqual(workspace.Paths.SessionFile, workspace.Paths.SettingsFile);
        Assert.False(File.Exists(workspace.Paths.SessionFile));
    }

    [Fact]
    public async Task Wiping_scratch_data_keeps_the_settings()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new SettingsStore(workspace.Paths);

        var chosen = EtchSettings.Default with { TrashRetentionDays = 0, Ligatures = false };

        await store.SaveAsync(chosen, TestContext.Current.CancellationToken);

        // A buffer, so the wipe has something to actually do.
        var id = BufferId.New();
        await workspace.Buffers.WriteAsync(id, "secret", TestContext.Current.CancellationToken);

        var wiped = workspace.Buffers.WipeAll();

        Assert.True(wiped.Deleted > 0);
        Assert.False(File.Exists(workspace.Paths.BufferFile(id)));

        // The wipe is about buffer text, not preferences. Resetting retention to seven
        // days here would be especially perverse: the user who reaches for this button
        // is the one most likely to have set it to zero.
        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(chosen, result.Settings);
    }
}
