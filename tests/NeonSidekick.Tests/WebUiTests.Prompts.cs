using System.Net;
using NeonSidekick.App;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The page's saved prompts on the session (later on 2026-10-15): bare <c>/prompts</c> opens the page's sheet and queues nothing (refused with
/// Web UI commands off, as every typed command); its other forms keep their verdicts; the sheet numbers the profile's prompts from 1; a Page
/// line that ever reached the PC's dispatch is refused with its reason.
/// </summary>
public sealed partial class WebUiTests
{
    private static readonly WebCommandSettings PromptCommandsOn = new(Commands: true, FileTools: true, ObsidianTools: true);

    [Fact]
    public void BarePrompts_OpensThePagesSheet_AndQueuesNothing()
    {
        _session.CommandSettings = () => PromptCommandsOn;

        var reply = _session.Send(new WebSendRequest { Id = "a", Text = "/prompts" }, IPAddress.Loopback);

        Assert.Equal((WebOutcome.Ok, WebUiText.OpenPrompts), (reply.Outcome, reply.Open));
        Assert.False(_session.TryTakeLine(out _));
        Assert.False(WebCommands.Check("/prompts", PromptCommandsOn).Allowed);   // never dispatched at the PC
        Assert.Equal(WebUiText.OpensOnThePage, WebCommands.Check("/prompts", PromptCommandsOn).Reason);
        Assert.Equal(WebCommandKind.Run, WebCommands.Check("/prompts 2", PromptCommandsOn).Kind);
        Assert.Equal(WebCommandKind.Refused, WebCommands.Check("/prompts list --clear", PromptCommandsOn).Kind);
        Assert.Equal((WebCommandKind.Refused, WebUiText.CommandsOff), (WebCommands.Check("/prompts", default).Kind, WebCommands.Check("/prompts", default).Reason));
    }

    [Fact]
    public void ThePromptsSheet_NumbersThePromptsFromOne_InTheirOrder()
    {
        _session.PromptsNow = () => [new("a1", "first one", DateTimeOffset.UnixEpoch), new("b2", "second one", DateTimeOffset.UnixEpoch)];

        var sheet = _session.Prompts();

        Assert.Equal([new WebPrompt(1, "first one", "a1"), new WebPrompt(2, "second one", "b2")], sheet.Items);
        _session.PromptsNow = () => throw new IOException("locked");
        Assert.Empty(_session.Prompts().Items);
    }
}
