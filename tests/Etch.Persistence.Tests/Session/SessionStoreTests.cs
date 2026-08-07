using Etch.Persistence.Model;
using Etch.Persistence.Session;
using Xunit;

namespace Etch.Persistence.Tests.Session;

public class SessionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static BufferRecord Scratch(string title = "Untitled", BufferId? id = null) =>
        new(
            id ?? BufferId.New(),
            BufferKind.Scratch,
            title,
            filePath: null,
            caretOffset: 12,
            firstVisibleLine: 3,
            isPinned: false,
            formatOverride: null,
            lastModifiedUtc: Now);

    [Fact]
    public async Task A_missing_index_is_a_first_launch_not_an_error()
    {
        using var workspace = TemporaryWorkspace.Create();

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Missing, result.Status);
        Assert.Empty(result.Session.Buffers);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task A_session_round_trips()
    {
        using var workspace = TemporaryWorkspace.Create();
        var active = BufferId.New();

        var session = new SessionSnapshot(
            SessionSnapshot.CurrentVersion,
            CleanShutdown: true,
            ActiveBufferId: active,
            Buffers: [Scratch("First", active), Scratch("Second")]);

        await workspace.Sessions.SaveAsync(session, TestContext.Current.CancellationToken);
        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Loaded, result.Status);
        Assert.Equal(2, result.Session.Buffers.Count);
        Assert.Equal(active, result.Session.ActiveBufferId);
        Assert.Equal("First", result.Session.Buffers[0].Title);
        Assert.Equal(12, result.Session.Buffers[0].CaretOffset);
        Assert.Equal(3, result.Session.Buffers[0].FirstVisibleLine);
    }

    [Fact]
    public async Task A_file_buffer_keeps_its_path_and_kind()
    {
        using var workspace = TemporaryWorkspace.Create();

        var record = new BufferRecord(
            BufferId.New(),
            BufferKind.File,
            "appsettings.json",
            filePath: @"C:\project\appsettings.json",
            caretOffset: 0,
            firstVisibleLine: 1,
            isPinned: true,
            formatOverride: "json",
            lastModifiedUtc: Now);

        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(SessionSnapshot.CurrentVersion, true, record.Id, [record]),
            TestContext.Current.CancellationToken);

        var loaded = (await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken)).Session.Buffers[0];

        // The kind is what stops an implicit write-through to somebody's real file,
        // so it has to survive persistence exactly.
        Assert.Equal(BufferKind.File, loaded.Kind);
        Assert.Equal(@"C:\project\appsettings.json", loaded.FilePath);
        Assert.True(loaded.IsPinned);
        Assert.Equal("json", loaded.FormatOverride);
    }

    [Fact]
    public async Task The_index_is_human_readable()
    {
        // Somebody will open this file when something has gone wrong. Enums as
        // numbers and ids as objects would make that harder for no gain.
        using var workspace = TemporaryWorkspace.Create();

        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(SessionSnapshot.CurrentVersion, true, null, [Scratch()]),
            TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(workspace.Paths.SessionFile, TestContext.Current.CancellationToken);

        Assert.Contains("\"Scratch\"", json, StringComparison.Ordinal);
        Assert.Contains("cleanShutdown", json, StringComparison.Ordinal);

        // Indented. Asserting on "\n" rather than Environment.NewLine deliberately —
        // the assertion is "this is not one long line", not a claim about which
        // line ending the serialiser happens to pick on this platform.
        Assert.Contains("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unclean_shutdown_is_reported_but_still_loads()
    {
        // The recovery path and the normal path are the same path. The flag changes
        // what the status bar says and nothing else.
        using var workspace = TemporaryWorkspace.Create();

        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(SessionSnapshot.CurrentVersion, CleanShutdown: false, null, [Scratch()]),
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Loaded, result.Status);
        Assert.False(result.Session.CleanShutdown);
        Assert.Single(result.Session.Buffers);
    }

    [Fact]
    public async Task Malformed_json_is_quarantined_rather_than_deleted()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(workspace.Paths.SessionFile, "{ this is not json", TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Unreadable, result.Status);
        Assert.Empty(result.Session.Buffers);
        Assert.NotNull(result.Notice);

        // Moved aside, not destroyed: the difference between "Etch ate my tab
        // layout" and "Etch put my tab layout over there".
        Assert.False(File.Exists(workspace.Paths.SessionFile));
        Assert.Single(Directory.GetFiles(workspace.Root, "session.quarantined-*.json"));
    }

    [Fact]
    public async Task An_empty_index_file_is_quarantined()
    {
        // The signature of a crash during somebody else's non-atomic write. Etch's
        // own writes cannot produce it.
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(workspace.Paths.SessionFile, string.Empty, TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Unreadable, result.Status);
        Assert.Single(Directory.GetFiles(workspace.Root, "session.quarantined-*.json"));
    }

    [Fact]
    public async Task An_index_from_a_newer_version_is_preserved_not_rewritten()
    {
        // Loading it and saving over it would silently drop every field this build
        // does not know about, which is how you lose your layout by opening the
        // wrong build once.
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SessionFile,
            """
            {
              "version": 99,
              "cleanShutdown": true,
              "activeBufferId": null,
              "buffers": []
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.FromFutureVersion, result.Status);
        Assert.Contains("newer version", result.Notice!, StringComparison.OrdinalIgnoreCase);

        // Left exactly where it is, and not renamed. Moving it aside would lose the
        // newer build's tab layout, which is the harm this branch exists to prevent.
        Assert.True(File.Exists(workspace.Paths.SessionFile));
        Assert.Empty(workspace.Paths.EnumerateQuarantinedSessions());

        // And this build must not save over it.
        Assert.False(result.CanSave);
    }

    [Fact]
    public async Task A_session_with_no_buffers_key_does_not_crash_startup()
    {
        // System.Text.Json passes default — null — for a constructor parameter the
        // payload omits. Before the record normalised it, this five-line file threw a
        // NullReferenceException out of LoadAsync and Etch would not start until the
        // user found and deleted a file they had never heard of.
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(workspace.Paths.SessionFile, """{ "version": 1 }""", TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Session.Buffers);
        Assert.Null(result.Session.ResolveActive());
    }

    [Fact]
    public async Task An_explicit_null_buffers_array_is_survivable_too()
    {
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SessionFile,
            """{ "version": 1, "cleanShutdown": true, "activeBufferId": null, "buffers": null }""",
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Session.Buffers);
    }

    [Fact]
    public async Task A_file_record_with_a_hostile_path_is_rejected()
    {
        // session.json is the untrusted-input boundary and FilePath is the one field
        // in it that names a location outside Etch's own directory. On an explicit
        // save it becomes a write target.
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SessionFile,
            """
            {
              "version": 1,
              "cleanShutdown": true,
              "activeBufferId": null,
              "buffers": [
                {
                  "id": "8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f",
                  "kind": "File",
                  "title": "innocuous.txt",
                  "filePath": "C:\\Users\\Hendrik\\..\\..\\Windows\\System32\\drivers\\etc\\hosts",
                  "caretOffset": 0,
                  "firstVisibleLine": 1,
                  "isPinned": false,
                  "formatOverride": null,
                  "lastModifiedUtc": "2026-07-29T12:00:00+00:00"
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Unreadable, result.Status);
        Assert.Empty(result.Session.Buffers);
    }

    [Fact]
    public async Task A_record_violating_its_invariants_on_disk_does_not_crash_the_load()
    {
        // A file buffer with no path cannot be saved and a scratch buffer with one is
        // a hazard, so the record refuses to exist. Hand-edited files can still
        // contain it, and that must degrade to "recover from disk" not to a crash.
        using var workspace = TemporaryWorkspace.Create();

        await File.WriteAllTextAsync(
            workspace.Paths.SessionFile,
            """
            {
              "version": 1,
              "cleanShutdown": true,
              "activeBufferId": null,
              "buffers": [
                {
                  "id": "8a1d3f2e4c5b7d6a9e0f1a2b3c4d5e6f",
                  "kind": "File",
                  "title": "orphan",
                  "filePath": null,
                  "caretOffset": 0,
                  "firstVisibleLine": 1,
                  "isPinned": false,
                  "formatOverride": null,
                  "lastModifiedUtc": "2026-07-29T12:00:00+00:00"
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLoadStatus.Unreadable, result.Status);
        Assert.Empty(result.Session.Buffers);
    }

    [Fact]
    public async Task Duplicate_entries_are_collapsed()
    {
        // Two tabs sharing one buffer file would mean two editors writing over each
        // other with no way for either to win.
        using var workspace = TemporaryWorkspace.Create();
        var id = BufferId.New();

        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(
                SessionSnapshot.CurrentVersion,
                true,
                id,
                [Scratch("First", id), Scratch("Duplicate", id)]),
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(result.Session.Buffers);
        Assert.Equal("First", result.Session.Buffers[0].Title);
    }

    [Fact]
    public async Task An_active_id_that_names_no_tab_is_dropped()
    {
        using var workspace = TemporaryWorkspace.Create();

        await workspace.Sessions.SaveAsync(
            new SessionSnapshot(
                SessionSnapshot.CurrentVersion,
                true,
                ActiveBufferId: BufferId.New(),
                Buffers: [Scratch()]),
            TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Session.ActiveBufferId);
        Assert.NotNull(result.Session.ResolveActive());
    }

    [Fact]
    public async Task Saving_stamps_the_current_schema_version()
    {
        using var workspace = TemporaryWorkspace.Create();

        await workspace.Sessions.SaveAsync(new SessionSnapshot(0, true, null, []), TestContext.Current.CancellationToken);

        var result = await workspace.Sessions.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionSnapshot.CurrentVersion, result.Session.Version);
    }

    [Fact]
    public async Task Saving_creates_the_directory_tree_if_it_is_missing()
    {
        using var workspace = TemporaryWorkspace.CreateUninitialised();

        await workspace.Sessions.SaveAsync(SessionSnapshot.Empty, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(workspace.Paths.SessionFile));
    }

    [Fact]
    public void Resolve_active_falls_back_to_the_first_tab()
    {
        var first = Scratch("First");
        var session = new SessionSnapshot(1, true, BufferId.New(), [first, Scratch("Second")]);

        Assert.Equal(first, session.ResolveActive());
    }

    [Fact]
    public void Resolve_active_on_an_empty_session_is_null()
    {
        Assert.Null(SessionSnapshot.Empty.ResolveActive());
    }
}
