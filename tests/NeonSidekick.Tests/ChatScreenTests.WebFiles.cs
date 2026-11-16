using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Files tab on the screen (2026-11-16, plan lucid-roaming-lantern #41): a turn's <c>write_file</c> listed with checkpoints off,
/// its folder joined with '/', its url the server's; <c>/clear</c> and <c>/new</c> empty the list; a restored session lists its own; a
/// <i>Working directory</i> change hides the row and 404s its url until changed back; the files tag moves with each.
/// </summary>
public partial class ChatScreenTests
{
    private string FilesRoot() => WorkingDirectory.Resolve("", _settings.ProfileDirectory);

    private void WriteTurn(string callId, string path, string content = "made")
    {
        _chat.Enqueue(FakeChatClient.Call(callId, WriteFileTool.ToolName, new Dictionary<string, object?> { ["path"] = path, [WriteFileTool.ContentArgument] = content }));
        _chat.EnqueueText("Written.");
    }

    [Fact]
    public async Task AWriteFileTurn_IsListed_WithCheckpointsOff_AndClearEmptiesTheList()
    {
        _settings.Update(d => { d.TtsOutput = false; d.FileTools = true; d.FileCheckpoints = false; });
        WriteTurn("c1", "out/sub/plan #2.md");
        WebFilesView? made = null, cleared = null;
        string? tagBefore = null, tagAfter = null, served = null;
        StepsWhenIdle(
            i =>
            {
                tagBefore = Web.Snapshot().State!.FilesTag;
                PushLine(i, "write the plan");
            },
            i =>
            {
                made = Web.Files();
                tagAfter = Web.Snapshot().State!.FilesTag;
                string url = made!.Rows.Single().Url;
                served = Web.MadeFilePath(url[WebUiHost.MadeFilePrefix.Length..url.LastIndexOf('/')]);
                PushLine(i, "/clear");
            },
            i =>
            {
                cleared = Web.Files();
                PushLine(i, "/exit");
            });

        await RunAsync();

        var row = Assert.Single(made!.Rows);
        Assert.Equal(("plan #2.md", "out/sub", "text", "📃"), (row.Name, row.Folder, row.Kind, row.Glyph));
        Assert.EndsWith("/plan%20%232.md", row.Url, StringComparison.Ordinal);
        Assert.Matches("^/api/files/[0-9a-f]{32}/", row.Url);
        Assert.Equal(Path.Combine(FilesRoot(), "out", "sub", "plan #2.md"), served);
        Assert.NotEqual(tagBefore, tagAfter);
        Assert.Empty(cleared!.Rows);
    }

    [Fact]
    public async Task NewEmptiesTheList_AndARestoredSession_ListsItsOwn()
    {
        _settings.Update(d => { d.TtsOutput = false; d.FileTools = true; });
        WriteTurn("c1", "kept.txt");
        WebFilesView? afterNew = null, restored = null;
        StepsWhenIdle(
            Line("write it"),
            Line("/new"),
            i =>
            {
                afterNew = Web.Files();
                using var store = OpenSessions();
                PushLine(i, "/sessions " + store.List(0).Single(s => s.Turns > 0).Id);
            },
            i =>
            {
                restored = Web.Files();
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Empty(afterNew!.Rows);
        Assert.Equal("kept.txt", Assert.Single(restored!.Rows).Name);
    }

    [Fact]
    public async Task AWorkingDirectoryChange_HidesTheRow_And404sItsUrl_UntilChangedBack()
    {
        _settings.Update(d => { d.TtsOutput = false; d.FileTools = true; });
        string elsewhere = Path.Combine(_settings.ProfileDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        WriteTurn("c1", "a.txt");
        string? token = null, servedAway = null, servedBack = null, tagHome = null, tagAway = null;
        WebFilesView? away = null, back = null;
        StepsWhenIdle(
            Line("write it"),
            i =>
            {
                string url = Web.Files()!.Rows.Single().Url;
                token = url[WebUiHost.MadeFilePrefix.Length..url.LastIndexOf('/')];
                tagHome = Web.Snapshot().State!.FilesTag;
                _settings.Update(d => d.WorkingDirectory = elsewhere);
                away = Web.Files();
                servedAway = Web.MadeFilePath(token);
                tagAway = Web.Snapshot().State!.FilesTag;
                _settings.Update(d => d.WorkingDirectory = "");
                back = Web.Files();
                servedBack = Web.MadeFilePath(token);
                PushLine(i, "/exit");
            });

        await RunAsync();

        Assert.Empty(away!.Rows);
        Assert.Null(servedAway);
        Assert.NotEqual(tagHome, tagAway);
        Assert.Equal("a.txt", Assert.Single(back!.Rows).Name);
        Assert.Equal(Path.Combine(FilesRoot(), "a.txt"), servedBack);
    }

    /// <summary>A listed file whose folder became a junction out of the root afterwards: refused at the serve check, as an unknown token is.</summary>
    [WindowsFact]
    public async Task AFolderSwappedForAJunctionOut_IsRefused_AtTheServeCheck()
    {
        _settings.Update(d => { d.TtsOutput = false; d.FileTools = true; });
        string outside = Path.Combine(Path.GetTempPath(), "neon-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "a.txt"), "outside");
        WriteTurn("c1", "out/a.txt");
        string? servedBefore = null, servedAfter = null;
        WebFilesView? after = null;
        bool linked = false;
        try
        {
            StepsWhenIdle(
                Line("write it"),
                i =>
                {
                    string url = Web.Files()!.Rows.Single().Url;
                    string token = url[WebUiHost.MadeFilePrefix.Length..url.LastIndexOf('/')];
                    servedBefore = Web.MadeFilePath(token);
                    string folder = Path.Combine(FilesRoot(), "out");
                    Directory.Delete(folder, recursive: true);
                    try
                    {
                        Directory.CreateSymbolicLink(folder, outside);
                        linked = true;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // no right to make a link here (no Developer Mode): nothing to check
                    }

                    servedAfter = Web.MadeFilePath(token);
                    after = Web.Files();
                    PushLine(i, "/exit");
                });

            await RunAsync();
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }

        Assert.NotNull(servedBefore);
        if (!linked)
        {
            return;
        }

        Assert.Null(servedAfter);
        Assert.Empty(after!.Rows);
    }
}
