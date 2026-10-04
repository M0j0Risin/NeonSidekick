using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary><c>/claude</c> on the screen (2026-09-27), over <see cref="FakeClaudeCli"/>.</summary>
public partial class ChatScreenTests
{
    private readonly FakeClaudeCli _claudeCli = new();

    [Fact]
    public async Task Claude_StreamsTheReplyUnderItsName_AndTheNextLocalTurnSeesTheTaggedPair()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.Enqueue(
            new ClaudeEvent.ToolActivity("Read", "notes.txt"),
            new ClaudeEvent.TextDelta("Hello "),
            new ClaudeEvent.TextDelta("there."),
            FakeClaudeCli.Ok("s-1", "Hello there."));
        _chat.EnqueueText("ok");
        PushLine("/claude what is in notes.txt?");
        PushLine("what did Claude say?");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.SpeakerGlyph + ClaudeText.SpeakerName, output);
        Assert.Contains(ClaudeText.ToolNote("Read", "notes.txt"), output);
        Assert.Contains("Hello there.", output);
        Assert.Contains("  · " + ClaudeText.Footer(0.02m, FakeClaudeCli.Ok("s-1").Usage), output);
        var asked = Assert.Single(_claudeCli.Requests);
        Assert.Equal("what is in notes.txt?", asked.Prompt);
        Assert.False(asked.Resume);
        Assert.Equal(ClaudePermissionLevel.ReadOnly, asked.Permission);   // the default
        var request = Assert.Single(_chat.Requests);
        Assert.Contains(request, m => m.Role == ChatRole.User && m.Text == "[to Claude] what is in notes.txt?");
        Assert.Contains(request, m => m.Role == ChatRole.Assistant && m.Text == "[Claude] Hello there.");
        Assert.Equal("what did Claude say?", request.Last(m => m.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task Claude_TheSecondMessageResumesTheFirstsSession_AndNewStartsAnother()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.EnqueueReply("s-1", "one").EnqueueReply("s-1", "two").EnqueueReply("s-2", "three");
        PushLine("/claude first");
        PushLine("/claude second");
        PushLine("/claude new");
        PushLine("/claude third");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _claudeCli.Requests.Count);
        Assert.False(_claudeCli.Requests[0].Resume);
        Assert.True(_claudeCli.Requests[1].Resume);
        Assert.Equal("s-1", _claudeCli.Requests[1].SessionId);
        Assert.False(_claudeCli.Requests[2].Resume);
        Assert.NotEqual("s-1", _claudeCli.Requests[2].SessionId);
        Assert.True(Guid.TryParse(_claudeCli.Requests[2].SessionId, out _));   // minted here, so the store can resume it
        Assert.Contains("  · " + ClaudeText.NewThreadNotice, output);
    }

    [Fact]
    public async Task Claude_ALostResume_DropsTheId_AndTriesOnceWithANewOne()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.EnqueueReply("s-1", "one");
        _claudeCli.Enqueue(FakeClaudeCli.Failed("s-1", "No conversation found with session ID: s-1"));
        _claudeCli.EnqueueReply("s-2", "fresh");
        PushLine("/claude first");
        PushLine("/claude again");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(3, _claudeCli.Requests.Count);
        Assert.True(_claudeCli.Requests[1].Resume);
        Assert.False(_claudeCli.Requests[2].Resume);
        Assert.Contains("  · " + ClaudeText.ResumeLostNotice, output);
        Assert.Contains("fresh", output);
        Assert.DoesNotContain(ClaudeText.Failed("No conversation found"), output);
    }

    [Fact]
    public async Task Claude_AFailure_IsOneErrorLine_AndAddsNothingToTheHistory()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.EnqueueStartFailure(ClaudeText.NotFound);
        _claudeCli.Enqueue(FakeClaudeCli.Failed(null, "Not logged in"));
        _chat.EnqueueText("ok");
        PushLine("/claude one");
        PushLine("/claude two");
        PushLine("hello");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ClaudeText.NotFound, output);
        Assert.Contains("  ✗ " + ClaudeText.Failed("Not logged in"), output);
        Assert.DoesNotContain(TranscriptRenderer.SpeakerGlyph + ClaudeText.SpeakerName, output);   // nothing was shown, so no name
        Assert.DoesNotContain(Assert.Single(_chat.Requests), m => m.Text?.Contains("Claude", StringComparison.Ordinal) == true);
        Assert.False(_claudeCli.Requests[1].Resume);   // a failed first message never became a thread
    }

    [Fact]
    public async Task Claude_ABareCommand_IsItsUsage_AndTheDeniedToolsAreSaid()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClaudeCliPermissions = "edit"; });
        _claudeCli.Enqueue(new ClaudeEvent.TextDelta("could not run it"), FakeClaudeCli.Ok("s-1", denied: ["Bash", "Bash"]));
        PushLine("/claude");
        PushLine("/claude run the tests");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ClaudeText.UsageError, output);
        Assert.Contains("  · Claude was denied Bash (Claude CLI slash command permissions: edit).", output);
        Assert.Equal(ClaudePermissionLevel.Edit, Assert.Single(_claudeCli.Requests).Permission);
    }

    [Fact]
    public async Task Claude_Esc_KillsTheRun_TheReplySoFarKept()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.Enqueue(new ClaudeEvent.TextDelta("partial"), new ClaudeEvent.TextDelta(" never"), FakeClaudeCli.Ok("s-1"));
        _claudeCli.BeforeEvent = async (i, ct) =>
        {
            if (i == 1)
            {
                _console.Input.PushKey(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        PushLine("/claude go");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("partial", output);
        Assert.DoesNotContain("never", output);
        Assert.Contains("  · " + ChatScreen.CancelledNotice, output);
        Assert.Contains(_session.History.Messages, m => m.Text == "[Claude] partial");
    }

    [Fact]
    public async Task Claude_TheExchangeIsStored_AndARestoreResumesItsThread()
    {
        _settings.Update(d => d.TtsOutput = false);
        _claudeCli.EnqueueReply("s-1", "stored reply");
        PushLine("/claude remember me");
        PushLine("/exit");
        await RunAsync();

        using var store = new SessionStore(_settings.ProfileDirectory, _time);
        var summary = Assert.Single(store.List(10));
        var record = store.Load(summary.Id)!;
        Assert.Equal("/claude remember me", Assert.Single(record.Turns).UserText);
        SessionHistory.FromJson(record.HistoryJson, out _, out _, out string? claudeId);
        Assert.Equal("s-1", claudeId);
    }

    [Fact]
    public void MidTurn_ClaudeIsRefused()
    {
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Claude, hasArgs: true));
    }
}
