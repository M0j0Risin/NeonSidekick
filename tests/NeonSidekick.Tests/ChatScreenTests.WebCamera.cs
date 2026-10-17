using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Camera;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The camera's shutter on the Web UI's page (2026-10-17, the user's question: a turn from the page stopped at the PC's shutter pane with
/// nothing to answer): under the <c>user</c> shutter a turn's <c>camera_capture</c> puts a <c>camera</c> card on the page beside the PC's pane;
/// Take and send shoots with the PC's camera and the model gets the photo, the PC's pane closing; Cancel is a decline, as the pane's.
/// The widened safety net records nothing: the card is the page's ask.
/// </summary>
public partial class ChatScreenTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CameraTool_UserShutter_FromThePage_TakeShoots_CancelDeclines(bool take)
    {
        CameraPane();
        _settings.Update(d => d.CameraTools = true);
        _chat.Enqueue(FakeChatClient.Call("c1", CameraCaptureTool.ToolName, new Dictionary<string, object?> { [CameraCaptureTool.PromptArgument] = "Show me the label." }));
        _chat.EnqueueText("Done.");
        WebAskSpec? seen = null;
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (_running is null)
            {
                return;
            }

            _running.WebPaneWithoutAskForTests ??= title => { lock (_pcOnlyPanes) { _pcOnlyPanes.Add(title); } };
            if (seen is null && Web.Asks.OpenAsks() is [var ask, ..] && ask.Spec.Kind == "camera")
            {
                seen = ask.Spec;
                Web.Answer(new WebAnswerRequest { AskId = ask.Id, Choice = take ? "take" : "decline" }, IPAddress.Loopback);
                return;
            }

            switch (step)
            {
                case 0: step++; SendFromPage("what is this?"); break;
                case 1:
                    if (Output.Contains("Done.", StringComparison.Ordinal) && Web.WaitingCount == 0 && _keys is not { PendingLine.IsCompleted: false })
                    {
                        step++;
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.NotNull(seen);
        Assert.Equal(CameraText.ModelPaneTitle, seen!.Title);
        Assert.Equal("Show me the label.", seen.Body);
        Assert.Equal([("take", CameraText.TakeAndSendRow), ("decline", CameraText.CancelRow)], seen.Choices.Select(c => (c.Id, c.Label)));
        var result = Assert.Single(Results(_chat.Requests[^1])).Result as string;
        if (take)
        {
            Assert.StartsWith("photo taken with Fake Cam", result, StringComparison.Ordinal);
            Assert.Single(CameraFiles());
        }
        else
        {
            Assert.Equal(CameraText.Declined, result);
            Assert.Empty(CameraFiles());
        }

        Assert.Contains("Done.", output);
        Assert.Empty(Web.Asks.OpenAsks());
    }
}
