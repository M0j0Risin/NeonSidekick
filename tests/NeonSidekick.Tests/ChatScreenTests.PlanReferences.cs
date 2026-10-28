using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan reference pictures on the screen (2026-10-18): a picture pasted while planning is named in the directive, saved beside the plan when it is
/// presented and listed under <c>## References</c>; a fresh-context approve sends it with the plan, and the new conversation's stored row keeps a
/// line naming the file rather than its bytes.
/// </summary>
public partial class ChatScreenTests
{
    [WindowsFact]   // the picture decodes through WIC
    public async Task PlanReferences_APastedPicture_IsSavedBesideThePlan_AndAFreshApproveSendsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string picture = Path.Combine(_settings.ProfileDirectory, "layout.png");
        File.WriteAllBytes(picture, FakeHeadlessBrowser.TinyPng);
        var input = Scripted();
        StepsWhenIdle(
            Line("/plan Add a guide"),
            i => i.PushPaste(picture).Push(Keys.Char(' '), Keys.Char('l'), Keys.Char('i'), Keys.Char('k'), Keys.Char('e'), Keys.Enter),
            Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine: { IsCompleted: false } })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(Keys.Char('f'), Keys.Enter);   // Approve, clear context & run
                }

                return;
            }

            idle();
        };
        _chat.EnqueueText("What should it look like?");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Done.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains(PlanText.ReferencesLine(["ref-1"]), _chat.Requests[1][0].Text);   // the directive names it while planning
        string saved = Path.Combine(_settings.ProfileDirectory, Files.WorkingDirectory.DefaultFolderName, ".neon", "plans", "add-guide", "ref-1.png");
        Assert.True(File.Exists(saved));
        string file = File.ReadAllText(PlanFile);
        Assert.Contains("## References\n- ref-1 (`.neon/plans/add-guide/ref-1.png`): a picture from the conversation", file.Replace("\r\n", "\n", StringComparison.Ordinal));
        // The fresh run: the plan's text and its picture, marked a reference; one dim line says so.
        var user = _chat.Requests[3].Last(m => m.Role == ChatRole.User);
        Assert.StartsWith(PlanText.ExecuteMessage(PlanPath), user.Text);
        var sent = Assert.Single(user.Contents.OfType<DataContent>());
        Assert.Equal(".neon/plans/add-guide/ref-1.png", ConversationHistory.ReferencePath(sent));
        Assert.Contains(PlanText.ReferencesAttachedNotice(1), output);
        // The new conversation's row keeps the stand-in, not the bytes.
        using var store = OpenSessions();
        var newest = store.List(10).OrderByDescending(s => s.Id).First();
        string json = store.Load(newest.Id)!.HistoryJson;
        Assert.Contains(".neon/plans/add-guide/ref-1.png, kept beside the plan", json);
        Assert.DoesNotContain(Convert.ToBase64String(FakeHeadlessBrowser.TinyPng), json);
    }

    [Fact]
    public async Task PlanReferences_APictureInACameraOrScreenFolder_IsNeverReadAgain_AndIsMarked_WhileItsSettingIsOff()
    {
        // 2026-10-27, the review: a Home Assistant still in home-cameras follows Camera keep in sessions as the camera's photos do, and a
        // picture attached after a restart (this run never saw it taken) is marked by its folder.
        string images = _settings.ProfileImagesDirectory;
        string still = Path.Combine(images, NeonSidekick.HomeAssistant.HaCamera.FolderName, "porch-20261027-120000.jpg");
        string photo = Path.Combine(images, Settings.AppSettingsData.DefaultCameraOutputFolder, "photo.jpg");
        string shot = Path.Combine(images, Settings.AppSettingsData.DefaultScreenOutputFolder, "shot.jpg");
        string other = Path.Combine(images, "comfy", "art.png");
        foreach (string file in new[] { still, photo, shot, other })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, FakeHeadlessBrowser.TinyPng);
        }

        var reloads = new List<string?>();
        var keptReloads = new List<string?>();
        IReadOnlyList<Files.ImageAttachment> marked = [];
        StepsWhenIdle(
            input =>
            {
                reloads.AddRange(new[] { still, photo, shot, other }.Select(_running!.PictureToReloadForTests));
                marked = _running!.MarkPicturesForTests([.. new[] { still, photo, shot, other }.Select(p => new Files.ImageAttachment(p, FakeHeadlessBrowser.TinyPng, "image/png", 1, 1))]);
                _settings.Update(d => { d.CameraKeepInSessions = true; d.ScreenKeepInSessions = true; });
                keptReloads.AddRange(new[] { still, photo, shot }.Select(_running!.PictureToReloadForTests));
                PushLine(input, "/exit");
            });

        await RunAsync();

        Assert.Equal([null, null, null, other], reloads);
        Assert.Equal([still, photo, shot], keptReloads);
        Assert.Equal([true, true, false, false], marked.Select(i => i.Camera));
        Assert.Equal([false, false, true, false], marked.Select(i => i.Screen));
    }
}
