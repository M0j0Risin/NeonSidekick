using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI on the screen (2026-10-14): a message from the page sent from the idle line, the page's copy of the transcript, an ask answered
/// from the page, the page's Stop withdrawing its message, a cleared conversation handing waiting messages back, and <c>/webui</c>.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The Web UI's server for the screen; null (the default) = none in this build.</summary>
    private Func<IWebUiBackend, IWebUiHost>? _webUiHost;

    private WebUiSession Web => _running!.WebUiSessionForTests;

    private static string? LastUser(IReadOnlyList<ChatMessage> request) => request.Last(m => m.Role == ChatRole.User).Text;

    private static List<(string Name, string Text)> TextBacks(WebEventHub.Subscription subscription)
    {
        var events = new List<(string, string)>();
        while (subscription.Reader.TryRead(out var item))
        {
            if (item.Name is "dropped" or "withdrawn")
            {
                events.Add((item.Name, JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text));
            }
        }

        return events;
    }

    [Fact]
    public async Task AWebLine_AtTheIdleLine_IsSentAsATurn_AndThePagesCopyHasBothSides()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hi from the model.");
        var input = WhenIdle(
            _ => Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "hello from the phone" }, null).Outcome),
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains("hello from the phone", output);
        Assert.Equal("hello from the phone", LastUser(_chat.Requests[0]));
        var feed = Web.Feed.Snapshot(m => m);
        Assert.Contains(feed, e => e.Kind == "user" && e.Text == "hello from the phone");
        Assert.Contains(feed, e => e.Kind == "assistant" && e.Text.Contains("Hi from the model.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWebLine_LeavesThePcsHalfTypedDraft_OnItsRow()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        var input = WhenIdle(
            i =>
            {
                i.Push(Keys.Char('d'), Keys.Char('r'), Keys.Char('a'));
                Web.Send(new WebSendRequest { Text = "from the phone" }, null);
            },
            i => i.Push(Keys.Enter),
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal("from the phone", LastUser(_chat.Requests[0]));
        Assert.Equal("dra", LastUser(_chat.Requests[1]));   // the PC's own draft, sent by its own Enter
        Assert.DoesNotContain("from the phone", _input_History());
    }

    /// <summary>The PC's ↑ history as the screen's input line keeps it: the page's lines never join it.</summary>
    private IReadOnlyList<string> _input_History() => _running!.InputHistoryForTests;

    [Fact]
    public async Task AskUser_AnsweredFromThePage_TheToolGetsTheAnswers_AndThePcSaysWhoAnswered()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Call("c1", AskUserTool.ToolName, new Dictionary<string, object?> { [AskUserTool.QuestionsArgument] = TwoQuestionsJson }));
        _chat.EnqueueText("Blue it is.");
        bool hooked = false;
        _chat.BeforeUpdate = (_, _) =>
        {
            if (!hooked)
            {
                hooked = true;
                var web = Web;
                web.Asks.Changed += () =>
                {
                    if (web.Asks.OpenAsks() is [var ask, ..] && ask.Winner is null)
                    {
                        _ = Task.Run(() => web.Answer(new WebAnswerRequest { AskId = ask.Id, Questions = [new() { Picked = [1] }, new() { Picked = [0, 1] }] }, IPAddress.Loopback));
                    }
                };
            }

            return Task.CompletedTask;
        };
        var input = Scripted();
        StepsWhenIdle(Line("pizza"), Line("/exit"));
        var idle = input.OnWait!;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine.IsCompleted: false })
            {
                return;   // the pane waits; the page answers
            }

            idle();
        };

        string output = await RunAsync();

        Assert.Contains(WebUiText.AnsweredNotice(QuestionMenu.SubmitRow), output);
        var result = Assert.Single(_chat.Requests[1][^1].Contents.OfType<FunctionResultContent>());
        Assert.Equal("Which color? — blue\nToppings? — cheese, olives", result.Result);
        Assert.Contains("Blue it is.", output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task ThePagesStop_BeforeTheFirstEvent_WithdrawsItsMessage_BackToThePage()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("never shown");
        WebEventHub.Subscription? subscription = null;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Stop(IPAddress.Loopback).Outcome);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // the Stop ends it long before
            }
        };
        var input = WhenIdle(
            _ =>
            {
                subscription = Web.Events.Subscribe();
                Web.Send(new WebSendRequest { Text = "take it back" }, null);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.WithdrawnNotice, output);
        Assert.DoesNotContain(ChatScreen.WithdrawnNotice, output);
        Assert.Equal([("withdrawn", "take it back")], TextBacks(subscription!));
        Assert.Equal(0, _session.History.TurnCount);
        Assert.Equal(WebOutcome.Conflict, Web.Stop(null).Outcome);   // nothing runs now
        subscription!.Dispose();
    }

    [Fact]
    public async Task ACanceledReply_UnderQueueCancelModeEmpty_HandsThePagesWaitingMessage_Back()
    {
        _settings.Update(d => { d.TtsOutput = false; d.QueueCancelMode = "empty"; });
        _chat.EnqueueText("One. ", "Two.");
        WebEventHub.Subscription? subscription = null;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                // After the first word, so the PC's line stays sent: a plain cancel, not a withdrawal.
                subscription = Web.Events.Subscribe();
                Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = "after the reply" }, null).Outcome);   // waits: a reply runs
                Assert.Equal(WebOutcome.Ok, Web.Stop(null).Outcome);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // the Stop ends it long before
            }
        };
        var input = WhenIdle(i => PushLine(i, "start"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal([("dropped", "after the reply")], TextBacks(subscription!));
        Assert.Single(_chat.Requests);   // the page's message never went
        subscription!.Dispose();
    }

    private sealed class RecordingHost : IWebUiHost
    {
        public WebUiBinding? Started { get; private set; }

        public bool Stopped { get; private set; }

        public int Port => Started?.Port ?? 0;

        public Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken)
        {
            Started = binding;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WebUiOn_StartsTheServerWithAToken_AndOff_StopsIt()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiReach = "local"; });
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        var input = WhenIdle(i => PushLine(i, "/webui on"), i => PushLine(i, "/webui off"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        var first = Assert.Single(made);
        Assert.Equal(WebUiAudience.Local, first.Started!.Audience);
        Assert.Equal(AppSettingsDataDefaults.WebUiPort, first.Started.Port);
        Assert.True(WebUiAuth.IsWellFormed(first.Started.Token));
        Assert.True(first.Stopped);
        Assert.False(_settings.Current.WebUi);
        Assert.StartsWith("dpapi:", _settings.Current.WebUiToken.Replace("keychain:", "dpapi:", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(first.Started.Token, output);   // never on the screen
    }

    [Fact]
    public async Task WebUi_WithoutAServerInThisBuild_SaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        var input = WhenIdle(i => PushLine(i, "/webui"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.NotAvailableError, output);
    }

    private static class AppSettingsDataDefaults
    {
        public const int WebUiPort = Settings.AppSettingsData.DefaultWebUiPort;
    }
}
