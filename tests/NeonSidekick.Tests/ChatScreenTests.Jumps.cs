using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Message jumps on the screen (2026-11-01, feature idea #3): Ctrl+Up under a reply moves the view to the reply's start (the watcher's live
/// editor answers it, as PgUp); a typed slash command's line and the app's own answer are starts, as everything drawn as a message is.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task WithGeometry_CtrlUpDuringAReply_JumpsToTheRepliesStart_AndIsNeverTyped()
    {
        _settings.Update(d => { d.TtsOutput = false; d.TranscriptMarkdown = false; });
        _console.Profile.Height = 10;
        _geometry = new ScreenGeometry(() => null);
        string first = string.Concat(Enumerable.Range(1, 12).Select(i => "row" + i + "\n"));
        _chat.EnqueueText(first, "after\n");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                Scripted().Push(Keys.Ctrl(ConsoleKey.UpArrow));
                await WaitUntilAsync(() => Output.Contains("message 2 of 2", StringComparison.Ordinal));
            }
        };
        LinesWhenIdle("hi", "/exit");

        string output = await RunAsync();

        Assert.Contains("message 2 of 2", output, StringComparison.Ordinal);   // the reply's first row on top: the second message of two
        Assert.Contains("after", output);
        Assert.Equal("hi", UserText(_chat.Requests[0]));
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task WithGeometry_AHelpAnswerAtTheIdleLine_IsTwoMessages_ItsLineAndItsAnswer()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HelpTools = false; });   // the manual answers itself, a block drawn as a reply
        _console.Profile.Height = 10;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(Line("/help claude"), Key(Keys.Ctrl(ConsoleKey.UpArrow)), Key(Keys.Ctrl(ConsoleKey.End)), Line("/exit"));

        string output = await RunAsync();

        // Its answer starts inside the bottom window, so Ctrl+Up goes to the line above it: the first of two. (/help usage until 2026-11-07, when its third form made it taller than the window.)
        Assert.Contains("message 1 of 2", output, StringComparison.Ordinal);
    }
}
