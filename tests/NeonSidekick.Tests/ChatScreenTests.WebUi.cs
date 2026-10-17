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
    public async Task AWebLine_LeavesThePcsHalfTypedDraft_OnItsRow_AndJoinsTheHistory()
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
        // One history since 2026-10-16 (the user's call): the page's line joined it as the page sent it, ahead of the PC's.
        Assert.Equal(["from the phone", "dra", "/exit"], _input_History());
    }

    /// <summary>The PC's ↑ history as the screen's input line keeps it: the page's lines join it as the page sends them (2026-10-16).</summary>
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

        /// <summary>Every binding it was started on, in order.</summary>
        public List<WebUiBinding> Starts { get; } = [];

        /// <summary>How long a start takes (later on 2026-10-15: /webui browser waits for a server still starting).</summary>
        public TimeSpan StartDelay { get; set; }

        private int _codes;

        public async Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken)
        {
            if (StartDelay > TimeSpan.Zero)
            {
                await Task.Delay(StartDelay, CancellationToken.None);
            }

            Started = binding;
            Starts.Add(binding);
        }

        /// <summary>A one-time link as the real host's reads (later on 2026-10-15), its code numbered.</summary>
        public string? OneTimeLink(string host) => Started is null ? null : WebUiAuth.OnceLink(host, Port, "code" + Interlocked.Increment(ref _codes));

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

    /// <summary>
    /// The pane's buttons (2026-10-14, the user's ask: <c>📱 Web UI │ enabled │ lan │ reset │ …</c>): l switches local to lan, saves it, restarts
    /// the server on it and opens the pane again, its button reading lan.
    /// </summary>
    [Fact]
    public async Task WebUiPane_TheReachButton_SwitchesTheReach_RestartsTheServer_AndOpensThePaneAgain()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.ReachKey), Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal("lan", _settings.Current.WebUiReach);
        Assert.Equal(WebUiAudience.LocalAreaNetwork, made[^1].Started!.Audience);
        string before = WebUiText.PaneTitle + " │ enabled │ local │ reset ";   // one address (localhost): a pane of one tab shows no title
        string after = WebUiText.PaneTitle + " │ enabled │ lan │ reset ";
        Assert.Contains(before, output);
        Assert.Contains(after, output);
        Assert.True(output.IndexOf(before, StringComparison.Ordinal) < output.IndexOf(after, StringComparison.Ordinal), output);
        Assert.Contains(InfoPane.ButtonsHint([('e', "on/off"), ('l', "lan/local"), ('r', "new token"), ('c', "copy link")], tabs: false), output);   // copy later on 2026-10-15
        Assert.Equal("lan", WebUiText.ReachButton(WebUiAudience.LocalAreaNetwork));
        Assert.Equal("local", WebUiText.ReachButton(WebUiAudience.Local));
    }

    /// <summary>The on/off button (2026-10-14, the user's ask): e turns a disabled Web UI on, starts the server and opens the pane on its address; again turns it off.</summary>
    [Fact]
    public async Task WebUiPane_TheEnabledButton_TurnsTheWebUiOn_AndOff()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = false; d.WebUiReach = "local"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.EnabledKey), Keys.Char(WebUiText.EnabledKey), Keys.Escape);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.False(_settings.Current.WebUi);
        var host = Assert.Single(made);
        Assert.Equal(WebUiAudience.Local, host.Started!.Audience);
        Assert.True(host.Stopped);
        string off = WebUiText.PaneTitle + " │ disabled │ local │ reset ";
        string on = WebUiText.PaneTitle + " │ enabled │ local │ reset ";
        Assert.Contains(off, output);
        Assert.Contains(on, output);
        Assert.True(output.IndexOf(off, StringComparison.Ordinal) < output.IndexOf(on, StringComparison.Ordinal), output);
        Assert.True(output.IndexOf(on, StringComparison.Ordinal) < output.LastIndexOf(off, StringComparison.Ordinal), output);
        Assert.Equal("enabled", WebUiText.EnabledButton(true));
        Assert.Equal("disabled", WebUiText.EnabledButton(false));
    }

    /// <summary>The reset button (later on 2026-10-14, the user's ask): r asks first; a yes makes a new token and restarts the server on it, a no keeps both.</summary>
    [Fact]
    public async Task WebUiPane_TheResetButton_AsksFirst_AndAYesMakesANewToken()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var made = new List<RecordingHost>();
        _webUiHost = _ => { var host = new RecordingHost(); made.Add(host); return host; };
        var input = WhenIdle(
            i =>
            {
                PushLine(i, "/webui");
                i.Push(Keys.Char(WebUiText.ResetKey), Keys.Escape);   // asked, no: kept
                i.Push(Keys.Char(WebUiText.ResetKey), Keys.Char('y'), Keys.Enter, Keys.Escape);   // asked, yes: a new token
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        var starts = made.SelectMany(h => h.Starts).ToList();
        Assert.Equal(2, starts.Count);
        Assert.NotEqual(starts[0].Token, starts[1].Token);
        Assert.True(WebUiAuth.IsWellFormed(starts[1].Token));
        Assert.Contains(WebUiText.ResetQuestion, output);
        Assert.Contains(ChatScreen.KeptNotice, output);
        Assert.Contains(WebUiText.ResetNotice, output);
        Assert.True(output.IndexOf(ChatScreen.KeptNotice, StringComparison.Ordinal) < output.IndexOf(WebUiText.ResetNotice, StringComparison.Ordinal), output);
    }

    [Fact]
    public async Task WebUi_WithoutAServerInThisBuild_SaysSo()
    {
        _settings.Update(d => d.TtsOutput = false);
        var input = WhenIdle(i => PushLine(i, "/webui"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.NotAvailableError, output);
    }

    // ── Pictures on the page (later on 2026-10-14) ──────────────────────────

    private List<WebPicture> PagePictures() => [.. Web.Feed.Snapshot(m => m).Where(e => e.Kind == "picture").SelectMany(e => e.Pictures ?? [])];

    [Fact]
    public async Task ADroppedPicture_WithThumbnailsOff_IsStillOfferedToThePage_FromItsFile()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShowImageThumbnails = false; });
        _chat.EnqueueText("A pink square.");
        string path = Bmp("square.bmp");
        var input = new ScriptedInput();
        input.PushPaste(path).Push(Keys.Enter);
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.DoesNotContain("▀", output);   // nothing at the PC
        var picture = Assert.Single(PagePictures());
        Assert.Equal("square.bmp", picture.Name);
        Assert.Equal((4, 4), (picture.Width, picture.Height));
        Assert.False(picture.TooLarge);
        var source = Web.Picture(picture.Id);
        Assert.NotNull(source);
        Assert.Equal(path, source.FullPath);
        Assert.Null(source.Bytes);   // a web-only picture with a file keeps none
        Assert.Equal(WebUiAssets.Current, Web.Snapshot().State.Assets);
    }

    [Fact]
    public async Task ADroppedPicture_ThePcDrew_IsTheSamePicture_OnThePage_AndKeepsItsBytes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("A pink square.");
        string path = Bmp("square.bmp");
        var input = new ScriptedInput();
        input.PushPaste(path).Push(Keys.Enter);
        PushLine(input, "/exit");

        string output = await RunAsync(input);

        Assert.Contains("▀", output);
        var picture = Assert.Single(PagePictures());
        Assert.NotNull(Web.Picture(picture.Id)!.Bytes);
        Assert.NotNull(_running!.PictureBytesForTests(picture.Id));
    }

    /// <summary>
    /// A rewind or a <c>/clear</c> lets the page's pictures go (later on 2026-10-14): a restored picture whose file is not there keeps its bytes only
    /// while the page's transcript holds it, so replaying a picture-heavy session again and again does not pile them up.
    /// </summary>
    [WindowsFact]
    public async Task ARestoredPicture_OnThePageAlone_KeepsItsBytes_UntilThePageLetsItGo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ShowImageThumbnails = false; });
        PaneOf40Rows();
        long id = SeedPictureSession();
        List<WebPicture>? restored = null;
        bool keptWhileShown = false;
        StepsWhenIdle(
            Line("/sessions " + id),
            i =>
            {
                restored = PagePictures();
                keptWhileShown = restored.All(p => _running!.PictureBytesForTests(p.Id) is not null);
                PushLine(i, "/clear");
            },
            Line("/exit"));

        await RunAsync();

        Assert.Equal(2, restored!.Count);
        Assert.True(keptWhileShown);
        foreach (var picture in restored)
        {
            Assert.Null(_running!.PictureBytesForTests(picture.Id));
            Assert.Null(Web.Picture(picture.Id));
        }

        Assert.Empty(PagePictures());
    }

    // ── Commands from the page (later on 2026-10-14) ────────────────────────

    /// <summary>
    /// A script for commands from the page: each step at the idle line, never while an ask is open on the page (the page answers it) or a page's
    /// line waits to be taken.
    /// </summary>
    private ScriptedInput WebSteps(params Action<ScriptedInput>[] steps) => WebSteps((Func<WebAskHandle, string?>?)null, steps);

    private ScriptedInput WebSteps(string? answer, params Action<ScriptedInput>[] steps) => WebSteps(answer is null ? null : _ => answer, steps);

    /// <summary>The asks the page was shown, in order (kind and title), recorded by <see cref="WebSteps(Func{WebAskHandle, string?}?, Action{ScriptedInput}[])"/>.</summary>
    private readonly List<WebAskSpec> _pageAsks = [];

    /// <summary>The panes a command from the page opened at the PC with nothing on the page: every web test ends with none.</summary>
    private readonly List<string> _pcOnlyPanes = [];

    /// <summary>
    /// <see cref="WebSteps(Action{ScriptedInput}[])"/> whose page answers each ask with <paramref name="answer"/> once the PC's pane waits on a key
    /// (a phone's user reading the question), so the pane is up when the page's answer closes it.
    /// </summary>
    private ScriptedInput WebSteps(Func<WebAskHandle, string?>? answer, params Action<ScriptedInput>[] steps)
    {
        var input = new ScriptedInput();
        int next = 0;
        input.OnWait = () =>
        {
            if (_running is null)
            {
                return;
            }

            _running.WebPaneWithoutAskForTests ??= title => { lock (_pcOnlyPanes) { _pcOnlyPanes.Add(title); } };
            if (Web.Asks.OpenAsks() is [var ask, ..])
            {
                if (ask.Winner is null && !_pageAsks.Contains(ask.Spec))
                {
                    _pageAsks.Add(ask.Spec);
                    if (answer?.Invoke(ask) is { } choice)
                    {
                        Web.Answer(new WebAnswerRequest { AskId = ask.Id, Choice = choice }, IPAddress.Loopback);
                    }
                    else if (answer is null)
                    {
                        _pcAnswer?.Invoke(input);   // the PC answers instead
                    }
                }

                return;
            }

            if (next < steps.Length && Web.WaitingCount == 0)
            {
                steps[next++](input);
            }
        };
        return input;
    }

    private void SendFromPage(string text) => Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = text }, IPAddress.Loopback).Outcome);

    [Fact]
    public async Task ACommandFromThePage_RunsAtTheIdleLine_AsTyped()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.MemoryMode = "read-write"; });
        var input = WebSteps(_ => SendFromPage("/memory read-only"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal("read-only", _settings.Current.MemoryMode);
        Assert.Empty(_chat.Requests);   // a command, not a message
    }

    [Fact]
    public async Task ACommandFromThePage_IsCheckedAgainAsItIsTaken()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.MemoryMode = "read-write"; });
        var input = WebSteps(
            _ =>
            {
                SendFromPage("/memory off");
                _settings.Update(d => d.WebUiCommands = false);   // switched off while it waited
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal("read-write", _settings.Current.MemoryMode);
        Assert.Contains(WebUiText.RefusedAtDispatch(WebUiText.CommandsOff), output);
    }

    [Fact]
    public async Task AModelSetFromThePage_IsRefused_WhenTheConnectWouldLookForAServer()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        string model = _settings.Current.LlmModel;
        var input = WebSteps(
            _ =>
            {
                _settings.Update(d => { d.LlmUrl = ""; d.LlmScanMode = "local"; });
                SendFromPage("/model qwen3");
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Contains(WebUiText.RefusedAtDispatch(WebUiText.WouldScan), output);
        Assert.Equal(model, _settings.Current.LlmModel);
    }

    [Fact]
    public async Task ASavedPrompt_FromThePage_IsSent()
    {
        // 2026-10-16, the user's ask: /prompts <n> sends the prompt (it went on the page's draft until then), as the page's own line.
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _chat.EnqueueText("Why did the chicken…");
        WebEventHub.Subscription? page = null;
        var input = WebSteps(
            _ =>
            {
                page = Web.Events.Subscribe();
                SendFromPage("/prompts save tell me a joke");
            },
            _ => SendFromPage("/prompts 1"),
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        var drafts = new List<string>();
        while (page!.Reader.TryRead(out var item))
        {
            if (item.Name == "draft")
            {
                drafts.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text);
            }
        }

        Assert.Empty(drafts);
        Assert.Single(_chat.Requests);
        Assert.Equal("tell me a joke", UserText(_chat.Requests[0]));
        page.Dispose();
    }

    [Theory]
    [InlineData("no", 1)]
    [InlineData("yes", 0)]
    public async Task AYesNoFromAPagesCommand_IsAskedOnThePage_AndItsAnswerActs(string answer, int left)
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        int lines = -1;
        var input = WebSteps(
            answer,
            _ => SendFromPage("/shopping add milk"),
            _ => SendFromPage("/shopping clear all"),
            i =>
            {
                var list = Web.Shopping();
                lines = list.Groups.Sum(g => g.Items.Count) + list.Ticked.Count;
                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Equal(left, lines);
        Assert.Contains(WebUiText.AnsweredNotice(answer == "yes" ? SettingsMenu.ConfirmRows[1] : SettingsMenu.ConfirmRows[0]), output);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task ATurnsStage_ReachesThePagesStatusRow_AsTheFunVerbThePcShows_AndClearsAtItsEnd()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUseFunVerbs = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _random = new Random(7);
        _chat.EnqueueText("Hello there.");
        WebEventHub.Subscription? page = null;
        WebStatus? after = null;
        var input = WhenIdle(
            _ =>
            {
                page = Web.Events.Subscribe();
                Web.Send(new WebSendRequest { Text = "hi" }, null);
            },
            i =>
            {
                after = Web.Snapshot().Status;
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        var labels = new List<string?>();
        while (page!.Reader.TryRead(out var item))
        {
            if (item.Name == "status")
            {
                labels.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebStatus)!.Label);
            }
        }

        // The first stage's verb, drawn as the PC's busy row draws it; never the plain words under the fun verbs.
        var draws = new Random(7);
        Assert.Equal(ThinkingVerbs.Pick(draws, null), labels.First(l => l is not null));
        Assert.DoesNotContain(ChatScreen.ThinkingLabel, labels);
        Assert.DoesNotContain(TurnStages.WritingLabel, labels);
        Assert.Null(after!.Label);   // the row clears once the turn is over
        page.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATurnsThinking_ReachesThePage_FoldedUnderItsSummary_OnlyWhenShown(bool show)
    {
        ThinkingTurn(show);
        _settings.Update(d => d.TtsOutput = false);
        LinesWhenIdle("why?", "/exit");

        await RunAsync();

        var feed = Web.Feed.Snapshot(m => m);
        var thinking = feed.Where(e => e.Kind == "thinking").ToList();
        if (show)
        {
            var block = Assert.Single(thinking);
            Assert.Equal("Pondering the sky.", block.Text);
            Assert.StartsWith(ThinkingFoldText.Glyph + "thought for", block.Summary, StringComparison.Ordinal);
            Assert.True(block.Ended);
        }
        else
        {
            Assert.Empty(thinking);
        }

        Assert.Contains(feed, e => e.Kind == "assistant" && e.Text.Contains("It is blue.", StringComparison.Ordinal) && e.Continues is null);
    }

    [Fact]
    public async Task ATurnsToolCalls_GatherUnderOneRun_ItsSummaryCountingThem()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolCollapseCount = 2; });
        _chat.Enqueue(FakeChatClient.Call("c1", GetCurrentTimeTool.ToolName, new Dictionary<string, object?>()));
        _chat.Enqueue(FakeChatClient.Call("c2", GetCurrentTimeTool.ToolName, new Dictionary<string, object?>()));
        _chat.EnqueueText("Done.");
        LinesWhenIdle("what time is it?", "/exit");

        await RunAsync();

        var feed = Web.Feed.Snapshot(m => m);
        // The turn's opening calls are a run of their own, as on the PC; the reply's two calls the next.
        var runs = feed.Where(e => e.Kind == "run").ToList();
        Assert.Equal(2, runs.Count);
        var run = runs[^1];
        Assert.Equal(ToolGroupText.Bare([(GetCurrentTimeTool.ToolName, 2)]), run.Text);
        Assert.True(run.Ended);
        Assert.Equal(2, feed.Count(e => e.Run == run.Seq));   // two quiet calls: their result notes, a unit each
        Assert.All(feed.Where(e => e.Kind is "tool" or "result"), e => Assert.NotNull(e.Run));
    }

    [Fact]
    public async Task ARestoredSession_IsOnThePageAsOnThePc_ALoneToolNoteNoRunHead()
    {
        // 2026-10-15 (E6): a restore draws through the same renderer, so the page's copy is the PC's lines; its one tool note opens a run of one
        // unit, whose head the page draws only past Tool collapse count, as the PC folds.
        _settings.Update(d => { d.TtsOutput = false; d.ToolCollapseCount = 2; });
        long id = SeedSession(toolCalls: 2);
        PushLine("/sessions " + id);
        PushLine("/exit");

        await RunAsync();

        var feed = Web.Feed.Snapshot(m => m).SkipWhile(e => e.Kind != "user").ToList();
        Assert.Equal(["user", "run", "tool", "assistant"], feed.Select(e => e.Kind).Take(4));
        Assert.Equal("How do I wire the Vosk model?", feed[0].Text);
        Assert.Equal(TranscriptRenderer.ToolGlyph.TrimStart() + NeonSidekick.Sessions.SessionText.ToolCallsNote(2), feed[2].Text);
        Assert.Equal((int?)2, feed[1].Keep);   // one unit of a keep of two: the page shows the note and no head
        Assert.Contains("Pass the folder to the detector.", feed[3].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(feed, e => e.NoReply == true);
    }

    [Fact]
    public async Task AReplyThatSaysNothing_IsNoReplyOnThePage_AsOnThePc()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("");
        LinesWhenIdle("hello?", "/exit");

        string output = await RunAsync();

        Assert.Contains(TranscriptRenderer.NoReplyText, output);
        Assert.Contains(Web.Feed.Snapshot(m => m), e => e.Kind == "assistant" && e.NoReply == true);
    }

    [Fact]
    public async Task APagesCommand_ShowsItsSpinnerOnTheStatusRow_AndNoLineInTheTranscript()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);   // the pane: its spinner is what the row reads
        WebEventHub.Subscription? page = null;
        WebStatus? after = null;
        var input = WebSteps(
            _ =>
            {
                page = Web.Events.Subscribe();
                SendFromPage("/ha");   // no server set: an error, but under its spinner
            },
            i =>
            {
                after = Web.Snapshot().Status;   // the row as a page connecting now reads it (the end's own send may wait out the throttle)
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        var events = new List<(string Name, string Json)>();
        while (page!.Reader.TryRead(out var item))
        {
            events.Add((item.Name, item.Json.Value));
        }

        // The status row (2026-10-15) carries the command's spinner, and the row clears when it ends; the transcript holds no line for it.
        var statuses = events.Where(e => e.Name == "status").Select(e => JsonSerializer.Deserialize(e.Json, WebUiJsonContext.Default.WebStatus)!).ToList();
        Assert.Contains(statuses, s => s.Label == NeonSidekick.HomeAssistant.HaText.Working && s.ElapsedMs >= 0);
        Assert.Null(after!.Label);
        Assert.DoesNotContain(events, e => e.Name == "append" && e.Json.Contains("\"kind\":\"working\"", StringComparison.Ordinal));
        page.Dispose();
    }

    [Fact]
    public async Task APcTypedCommand_NeverAsksOnThePage()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        int asked = 0;
        int lines = -1;
        var input = WebSteps(
            _ =>
            {
                Web.Asks.Changed += () => Interlocked.Increment(ref asked);
                SendFromPage("/shopping add milk");
            },
            i =>
            {
                PushLine(i, "/shopping clear all");
                i.Push(Keys.Enter);   // the pane's No
            },
            i =>
            {
                lines = Web.Shopping().Groups.Sum(g => g.Items.Count);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal(0, asked);
        Assert.Equal(1, lines);
    }

    /// <summary>What the PC presses while a page's ask is open and the page leaves it (the "either answer wins" tests).</summary>
    private Action<ScriptedInput>? _pcAnswer;

    /// <summary>The page's id for the pick card's row reading <paramref name="label"/>.</summary>
    private static string ItemId(WebAskHandle ask, string label) => ask.Spec.Items!.First(i => i.Label == label).Id;

    [Fact]
    public async Task BareReasoningFromThePage_IsAPickCard_AndThePagesPickIsSaved()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.LlmReasoning = "none"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = WebSteps(ask => ItemId(ask, "high"), _ => SendFromPage("/reasoning"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        Assert.Equal("high", _settings.Current.LlmReasoning);
        var card = Assert.Single(_pageAsks);
        Assert.Equal(("pick", SettingsMenu.ReasoningTitle), (card.Kind, card.Title));
        Assert.Equal(NeonSidekick.Llm.ReasoningLevel.Levels, card.Items!.Select(i => i.Label));
        Assert.Equal(ItemIdOf(card, "none"), card.Current);
        Assert.Contains(WebUiText.AnsweredNotice("high"), output);
    }

    private static string ItemIdOf(WebAskSpec spec, string label) => spec.Items!.First(i => i.Label == label).Id;

    [Fact]
    public async Task BareReasoningFromThePage_ThePcMayPickInstead()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.LlmReasoning = "none"; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _pcAnswer = i => i.Push(Keys.Down, Keys.Enter);   // the row under none
        var input = WebSteps(_ => SendFromPage("/reasoning"), i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(NeonSidekick.Llm.ReasoningLevel.Levels[1], _settings.Current.LlmReasoning);
        Assert.Single(_pageAsks);
        Assert.Empty(Web.Asks.OpenAsks());
    }

    [Fact]
    public async Task AListFromThePage_IsAnInfoCard_ThePageClosesIt_AndNothingIsSaidOfIt()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; d.FileTools = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        string files = Path.Combine(_settings.ProfileDirectory, NeonSidekick.Files.WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(Path.Combine(files, "docs"));
        File.WriteAllText(Path.Combine(files, "docs", "a.txt"), "a");
        var input = WebSteps(_ => "close", _ => SendFromPage("/tree"), i => PushLine(i, "/exit"));

        string output = await RunAsync(input);

        var card = Assert.Single(_pageAsks);
        Assert.Equal("info", card.Kind);
        Assert.Contains(card.Lines!, line => line.Contains("a.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(WebUiText.AnsweredNotice(WebUiText.CloseChoice), output);
    }

    [Fact]
    public async Task RewindFromThePage_PicksOnTheCard_AsksYes_AndPutsTheMessageOnThePagesDraft()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("One.").EnqueueText("Two.");
        WebEventHub.Subscription? page = null;
        var input = WebSteps(
            ask => ask.Spec.Kind == "pick" ? ask.Spec.Items![0].Id : "yes",
            i => PushLine(i, "first"),
            i => PushLine(i, "second"),
            _ =>
            {
                page = Web.Events.Subscribe();
                SendFromPage("/rewind");
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(["pick", "confirm"], _pageAsks.Select(a => a.Kind));
        Assert.Equal(2, _pageAsks[0].Items!.Count);
        Assert.Equal(_pageAsks[0].Items![1].Id, _pageAsks[0].Current);   // the last message by default
        Assert.Equal(0, _session.History.TurnCount);
        var drafts = new List<string>();
        while (page!.Reader.TryRead(out var item))
        {
            if (item.Name == "draft")
            {
                drafts.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text);
            }
        }

        Assert.Equal(["first"], drafts);
        page.Dispose();
    }

    [Fact]
    public async Task ThePagesDropdown_IsBuiltAtTheIdleLine_WhileAPageIsConnected_AndFollowsTheSettings()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebUiCommands = true; });
        WebEventHub.Subscription? page = null;
        IReadOnlyList<string>? on = null;
        IReadOnlyList<string>? off = null;
        var input = WebSteps(
            i =>
            {
                Assert.Null(Web.Completions);   // nobody connected: never built
                page = Web.Events.Subscribe();
                PushLine(i, "/new");
            },
            i =>
            {
                on = [.. Web.Complete(new WebCompleteRequest { Text = "/tr", Cursor = 3, Seq = 1 }).Items.Select(x => x.Text)];
                _settings.Update(d => d.WebUiCommands = false);
                PushLine(i, "/new");
            },
            i =>
            {
                off = [.. Web.Complete(new WebCompleteRequest { Text = "/tr", Cursor = 3, Seq = 2 }).Items.Select(x => x.Text)];
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Contains("/tree", on!);
        Assert.Empty(off!);
        page!.Dispose();
    }

    private static class AppSettingsDataDefaults
    {
        public const int WebUiPort = Settings.AppSettingsData.DefaultWebUiPort;
    }
}
