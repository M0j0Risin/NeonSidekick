using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Screen;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The screen capture on the screen (2026-10-04): the model's <c>screen_capture</c> under each ask, and <c>/screen</c>.</summary>
public partial class ChatScreenTests
{
    /// <summary>The fake screen the chat screen gets; null (the default) is a screen with no screen layer.</summary>
    private FakeScreenSystem? _screenSystem;

    private string ScreenFolder => Path.Combine(WorkingDirectory.Resolve("", _settings.ProfileDirectory), _settings.Current.ScreenOutputFolder);

    private string[] ScreenFiles() => Directory.Exists(ScreenFolder) ? Directory.GetFiles(ScreenFolder) : [];

    /// <summary>
    /// A model reply that is <paramref name="calls"/> <c>screen_capture</c> calls aimed at <paramref name="target"/>, then
    /// <paramref name="reply"/>; <paramref name="answer"/> pushed at the allow pane's own wait (none under <c>allow</c>).
    /// </summary>
    private void ScreenToolFixture(ConsoleKeyInfo[] answer, string reply, int calls = 1, string target = "window:notepad")
    {
        _screenSystem = new FakeScreenSystem();
        _settings.Update(d => { d.TtsOutput = false; d.ScreenTools = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        for (int i = 1; i <= calls; i++)
        {
            _chat.Enqueue(FakeChatClient.Call("c" + i, ScreenCaptureTool.ToolName, new Dictionary<string, object?> { [ScreenCaptureTool.TargetArgument] = target, [ScreenCaptureTool.PromptArgument] = "To read the error." }));
        }

        _chat.EnqueueText(reply);
        var input = Scripted();
        StepsWhenIdle(Line("what does it say?"), Line("/exit"));
        var idle = input.OnWait!;
        bool answered = false;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                if (!answered)
                {
                    answered = true;
                    input.Push(answer);
                }

                return;
            }

            idle();
        };
    }

    [Fact]
    public async Task ScreenTool_Ask_AllowOnce_SendsTheWindow_TheViewerShowsIt()
    {
        ScreenToolFixture([Keys.Char('o'), Keys.Enter], "It says: file not found.");

        string output = await RunAsync();

        Assert.Contains(ScreenText.AllowTitle, output);
        Assert.Contains("Window \"notes.txt - Notepad\" (Notepad): To read the error.", output);
        string result = (string)Assert.Single(Results(_chat.Requests[^1])).Result!;
        Assert.StartsWith("screenshot of window \"notes.txt - Notepad\" (Notepad) as 640x480, saved as screen_images", result);
        Assert.Single(ScreenFiles());
        Assert.Equal(ScreenFiles(), _shotsShown);
        Assert.Contains(_chat.Requests[^1], m => m.Contents.OfType<DataContent>().Any());
        Assert.Equal(["window 200"], _screenSystem!.Captures);
    }

    [Fact]
    public async Task ScreenTool_Ask_Deny_IsTheModelsAnswer_AndTheTurnsNextCallIsNotAsked()
    {
        ScreenToolFixture([Keys.Enter], "Understood.", calls: 2);

        await RunAsync();

        var results = Results(_chat.Requests[^1]).Select(r => r.Result as string).ToList();
        Assert.Equal([ScreenText.Denied, ScreenText.AlreadyDeclined], results);
        Assert.Empty(ScreenFiles());
        Assert.Empty(_screenSystem!.Captures);
    }

    [Fact]
    public async Task ScreenTool_Ask_AllowForTheSession_TakesTwoWithoutAskingAgain()
    {
        ScreenToolFixture([Keys.Char('s'), Keys.Enter], "Two.", calls: 2);

        await RunAsync();

        Assert.All(Results(_chat.Requests[^1]), r => Assert.StartsWith("screenshot of window", r.Result as string));
        Assert.Equal(2, ScreenFiles().Length);
    }

    [Fact]
    public async Task ScreenTool_Allow_TakesItUnasked_AndABadTargetIsItsSentence()
    {
        ScreenToolFixture([], "Hm.", calls: 1, target: "window:excel");
        _settings.Update(d => { d.ScreenAsk = "allow"; d.ScreenPreview = false; });

        string output = await RunAsync();

        Assert.DoesNotContain(ScreenText.AllowTitle, output);
        Assert.Equal("Error: " + ScreenText.NoSuchWindow("excel"), Assert.Single(Results(_chat.Requests[^1])).Result);
        Assert.Empty(_shotsShown);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ScreenTools_AreOfferedWithTheSetting_AfterTheCamerasPlace(bool setting, bool offered)
    {
        _screenSystem = new FakeScreenSystem();
        _chat.EnqueueText("one");
        _settings.Update(d => { d.TtsOutput = false; d.ScreenTools = setting; });
        _geometry = new ScreenGeometry(() => null);
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var names = (_chat.Options[0]?.Tools ?? []).Select(t => t.Name).ToList();
        Assert.Equal(offered, names.Contains(ScreenCaptureTool.ToolName));
        Assert.Equal(offered, names.Contains(ScreenListTool.ToolName));
    }

    [Fact]
    public async Task ScreenCommand_PutsTheShotOnTheLine_ListsTheTargets_AndABadTargetIsAnError()
    {
        _screenSystem = new FakeScreenSystem();
        PushLine("/screen list");
        PushLine("/screen printer");
        PushLine("/screen all");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("monitor:2  2560x1440 at 1920,0, this app's", output);
        Assert.Contains(ScreenText.BadTarget("printer"), output);
        string shot = Assert.Single(ScreenFiles());
        Assert.Contains(ScreenText.Attached(Path.Combine("screen_images", Path.GetFileName(shot))), output);
        Assert.Equal(["area 0,0 4480x1440"], _screenSystem.Captures);
        Assert.Equal([shot], _shotsShown);
    }

    [Fact]
    public async Task ScreenCommand_WithoutAScreen_SaysSo()
    {
        PushLine("/screen");
        PushLine("/exit");

        Assert.Contains(ScreenText.Unsupported, await RunAsync());
    }
}
