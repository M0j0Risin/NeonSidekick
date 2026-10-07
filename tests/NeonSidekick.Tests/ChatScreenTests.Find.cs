using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/find</c> on the screen (2026-10-07, the user's ask, phase 5 of the UI round): the transcript's find.</summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Find_WithoutThePane_SaysItNeedsTheScreen()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/find apple");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + ChatScreen.FindNeedsPaneNotice, output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>With the pane: the words find the line already in the transcript, and ESC is back at the idle line, nothing sent.</summary>
    [Fact]
    public async Task Find_WithThePane_FindsInTheTranscript_AndEscIsBackAtTheLine()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(
            Line("/echo the apple tree"),
            input => { PushLine(input, "/find apple"); input.Push(Keys.Escape); },
            Line("/exit"));

        string output = await RunAsync();

        // Found (the typed /echo line and its echo; the /find line itself is left out), counted from the newest.
        Assert.Contains(TranscriptFind.Hint(1, 2), output);
        Assert.DoesNotContain(TranscriptFind.Hint(0, 0), output);
        Assert.Empty(_chat.Requests);
    }
}
