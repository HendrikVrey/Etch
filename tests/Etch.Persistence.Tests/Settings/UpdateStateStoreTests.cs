using System.Text;
using Etch.Persistence.Model;
using Etch.Persistence.Settings;
using Xunit;

namespace Etch.Persistence.Tests.Settings;

/// <summary>
/// <see cref="UpdateStateStore"/>, and the settings field that goes with it.
/// </summary>
public class UpdateStateStoreTests
{
    [Fact]
    public async Task What_is_saved_is_what_is_loaded()
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new UpdateStateStore(workspace.Paths);
        var state = new UpdateState(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), "1.0.4");

        await store.SaveAsync(state, TestContext.Current.CancellationToken);

        Assert.Equal(state, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_file_is_the_empty_state()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        var state = await new UpdateStateStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Empty, state);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"lastCheckedUtc\": \"yesterday\"}")]
    public async Task A_broken_file_is_the_empty_state_and_never_throws(string contents)
    {
        using var workspace = TemporaryWorkspace.Create();
        await File.WriteAllTextAsync(workspace.Paths.UpdateFile, contents, TestContext.Current.CancellationToken);

        var state = await new UpdateStateStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateState.Empty, state);
    }

    [Fact]
    public async Task An_implausible_skipped_version_is_dropped()
    {
        using var workspace = TemporaryWorkspace.Create();
        var json = "{\"lastCheckedUtc\": null, \"skippedVersion\": \"" + new string('9', 500) + "\"}";
        await File.WriteAllTextAsync(workspace.Paths.UpdateFile, json, Encoding.UTF8, TestContext.Current.CancellationToken);

        var state = await new UpdateStateStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(state.SkippedVersion);
    }

    [Fact]
    public async Task A_settings_file_from_before_the_question_existed_has_not_answered_it()
    {
        // The field is opt-in: a file with no answer must read as "not asked", never as yes.
        using var workspace = TemporaryWorkspace.Create();
        const string Old = """
            {
              "version": 1,
              "trashRetentionDays": 7,
              "ligatures": true,
              "reducedThresholdBytes": 2097152,
              "plainTextThresholdBytes": 10485760,
              "hardCeilingBytes": 104857600
            }
            """;
        await File.WriteAllTextAsync(workspace.Paths.SettingsFile, Old, TestContext.Current.CancellationToken);

        var loaded = await new SettingsStore(workspace.Paths).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        Assert.Null(loaded.Settings.CheckForUpdates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_answer_survives_a_save(bool allowed)
    {
        using var workspace = TemporaryWorkspace.Create();
        var store = new SettingsStore(workspace.Paths);

        await store.SaveAsync(EtchSettings.Default with { CheckForUpdates = allowed }, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(allowed, loaded.Settings.CheckForUpdates);
    }
}
