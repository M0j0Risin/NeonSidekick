using System.Net;
using System.Text.Json;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's pure parts (2026-10-14): the transcript feed, the asks' broker, the session's messages and notices, the reach word, the
/// sign-in's token and cookie, the reply's HTML, the shopping list's page, and the controller over a fake server.
/// </summary>
public sealed class WebUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStoreHolder _holder;
    private readonly ShoppingList _list;
    private readonly WebUiSession _session;
    private int _nudges;
    private int _shoppingChanges;

    public WebUiTests()
    {
        _holder = new ShoppingStoreHolder(() => _dir, _time);
        _list = new ShoppingList(_holder.Current);
        _session = new WebUiSession(State, _ => new WebReply(WebOutcome.Conflict, WebUiText.NothingToStop), () => _list, () => _shoppingChanges++, _time)
        {
            Nudge = () => _nudges++,
        };
    }

    private WebState State() => new("p", "🦊", "🙂", "m", false, false, false, "", _session?.Waiting ?? [], null, "1");

    public void Dispose()
    {
        _session.Dispose();
        _holder.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private List<FeedChange> Watch(TranscriptFeed feed)
    {
        var changes = new List<FeedChange>();
        feed.Changed += changes.Add;
        return changes;
    }

    // ── The feed ─────────────────────────────────────────────────────────────

    [Fact]
    public void AReply_IsOneEntry_ThatGrows_AndEnds()
    {
        var feed = new TranscriptFeed();
        var changes = Watch(feed);

        long seq = feed.Begin("🦊");
        feed.Append("Hello");
        feed.Append(" **there**");
        feed.End();

        var entry = Assert.Single(feed.Snapshot(m => "<p>" + m + "</p>"));
        Assert.Equal((seq, "assistant", "Hello **there**", "🦊", "<p>Hello **there**</p>"), (entry.Seq, entry.Kind, entry.Text, entry.Glyph, entry.Html));
        Assert.Collection(changes,
            c => Assert.IsType<FeedChange.Appended>(c),
            c => Assert.Equal(new FeedChange.Grew(seq, 0, "Hello"), c),
            c => Assert.Equal(new FeedChange.Grew(seq, 5, " **there**"), c),
            c => Assert.Equal(new FeedChange.Ended(seq, "Hello **there**"), c));
    }

    [Fact]
    public void AToolLine_EndsTheReplyRun_AndTheTextAfterItKeepsTheGlyph()
    {
        var feed = new TranscriptFeed();
        feed.Begin("🦊");
        feed.Append("Looking.");
        feed.Add(FeedKind.Tool, "🛠️ web_search cats");
        feed.Append("Found them.");
        feed.End();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["assistant", "tool", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal("🦊", entries[2].Glyph);
        Assert.All(entries.Where(e => e.Kind == "assistant"), e => Assert.NotNull(e.Html));
    }

    [Fact]
    public void AStreamingReply_HasNoHtmlYet_AndAToolResultIsCut()
    {
        var feed = new TranscriptFeed();
        feed.Add(FeedKind.ToolResult, new string('x', 1000), failed: true);
        feed.Begin(null);
        feed.Append("half");

        var entries = feed.Snapshot(m => "html");
        Assert.Equal(TranscriptFeed.MaxResultChars + 1, entries[0].Text.Length);
        Assert.True(entries[0].Failed);
        Assert.Null(entries[1].Html);
    }

    [Fact]
    public void TheFeed_KeepsTheNewestEntries_AndClearSaysSo()
    {
        var feed = new TranscriptFeed();
        for (int i = 0; i < TranscriptFeed.MaxEntries + 50; i++)
        {
            feed.Add(FeedKind.Notice, "n" + i);
        }

        Assert.Equal(TranscriptFeed.MaxEntries, feed.Count);
        Assert.Equal("n50", feed.Snapshot(m => m)[0].Text);

        var changes = Watch(feed);
        feed.Clear();
        Assert.Equal(0, feed.Count);
        Assert.IsType<FeedChange.Cleared>(Assert.Single(changes));
    }

    [Fact]
    public void TheRenderer_RecordsWhatItDraws()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        var feed = new TranscriptFeed();
        var renderer = new TranscriptRenderer(console) { Feed = feed };

        renderer.User("hi");
        renderer.Tool("web_search", "{\"query\":\"cats\"}");
        renderer.ToolResult("web_search", "3 results");
        renderer.Notice("(a notice)");
        renderer.Rule();
        renderer.BeginAssistant(glyph: "🦊");
        renderer.AppendDelta("  Hello");
        renderer.EndAssistant();

        var entries = feed.Snapshot(m => m);
        Assert.Equal(["user", "tool", "result", "notice", "separator", "assistant"], entries.Select(e => e.Kind));
        Assert.Equal(TranscriptRenderer.ToolText("web_search", "{\"query\":\"cats\"}"), entries[1].Text);
        Assert.Equal("Hello", entries[5].Text);   // the leading whitespace dropped, as on the screen
    }

    // ── The asks ─────────────────────────────────────────────────────────────

    private static WebAskSpec YesNo() => new("confirm", "Go?", [new WebChoice("no", "No"), new WebChoice("yes", "Yes")]);

    [Fact]
    public async Task AWebAnswer_ClaimsTheAsk_AndClosesIt()
    {
        var broker = new WebAskBroker();
        var handle = broker.Open(YesNo());

        var reply = broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "yes" }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Equal(AskSource.Web, handle.Winner);
        Assert.True(handle.Answered.IsCompletedSuccessfully);
        Assert.Equal("Yes", handle.Describe(await handle.Answered));
        Assert.Empty(broker.OpenAsks());
        Assert.False(handle.TryClaim(AskSource.Pc));   // the pane's answer, coming after, does not count
    }

    [Fact]
    public void ThePcClaimingFirst_LeavesTheWebAConflict()
    {
        var broker = new WebAskBroker();
        var handle = broker.Open(YesNo());
        Assert.True(handle.TryClaim(AskSource.Pc));

        var reply = broker.Answer(new WebAnswerRequest { AskId = handle.Id, Choice = "yes" }, null);

        Assert.Equal(WebOutcome.Conflict, reply.Outcome);
        Assert.False(handle.Answered.IsCompleted);
        Assert.Equal(WebOutcome.Conflict, broker.Answer(new WebAnswerRequest { AskId = "nope", Choice = "yes" }, null).Outcome);
    }

    [Fact]
    public void AnAnswer_MustFitTheAsk()
    {
        var questions = new WebAskSpec("question", "Questions", [], Questions: [new WebQuestion("Color?", null, false, ["red", "blue"]), new WebQuestion("Toppings?", null, true, ["cheese", "ham"])]);

        Assert.Equal(WebOutcome.Refused, WebAskBroker.Check(YesNo(), new WebAnswerRequest { Choice = "maybe" })!.Value.Outcome);
        Assert.Null(WebAskBroker.Check(YesNo(), new WebAnswerRequest { Choice = "no" }));
        // Every question answered, by a pick or typed words; one pick for a single choice.
        Assert.Equal(WebUiText.AskIncomplete, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }] })!.Value.Message);
        Assert.Equal(WebUiText.AskIncomplete, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }, new() { Picked = [] }] })!.Value.Message);
        Assert.Equal(WebUiText.NoSuchChoice, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [0, 1] }, new() { Picked = [0] }] })!.Value.Message);
        Assert.Equal(WebUiText.NoSuchChoice, WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Picked = [5] }, new() { Picked = [0] }] })!.Value.Message);
        Assert.Null(WebAskBroker.Check(questions, new WebAnswerRequest { Questions = [new() { Other = "green" }, new() { Picked = [0, 1], Other = "olives" }] }));
    }

    [Fact]
    public void AskUsersAnswers_ReadAsTheToolReadsThePane()
    {
        var questions = new List<Llm.Tools.AskQuestion> { new("Color?", "Color", false, ["red", "blue"]), new("Toppings?", null, true, ["cheese", "olives", "ham"]) };

        var answers = App.ChatScreen.QuestionAnswers(questions, new WebAnswerRequest { Questions = [new() { Picked = [1] }, new() { Picked = [2, 0], Other = " anchovies " }] });

        Assert.Equal(["blue"], answers[0].Choices);
        Assert.Equal(["cheese", "ham", "anchovies"], answers[1].Choices);   // in option order, the typed answer last
    }

    [Fact]
    public void ThePlansRefine_ReadsAsThePcsTypedLine()
    {
        var verdict = App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "refine", Text = "  add\r\ntests  " });
        Assert.Equal(new Plans.PlanVerdict(Plans.PlanChoice.Refine, "add tests"), verdict);
        Assert.Equal(new Plans.PlanVerdict(Plans.PlanChoice.Refine), App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "refine", Text = " " }));
        Assert.Equal(Plans.PlanChoice.ApproveFresh, App.ChatScreen.PlanVerdictOf(new WebAnswerRequest { Choice = "fresh" }).Choice);
    }

    [Fact]
    public void AnApproval_OnThePage_IsTheWholeCommand_InThePcsWords()
    {
        var request = new Shell.CommandRequest("run_command", "git status --short\ngit log -1", ["git"], IsScript: true);

        var spec = App.ChatScreen.ApprovalSpec(request);

        Assert.Equal(Shell.ShellText.ScriptApprovalTitle, spec.Title);
        Assert.Equal(Shell.ShellText.WholeCommandLines(request), spec.Lines);
        Assert.Equal([Shell.ShellText.DenyRow, Shell.ShellText.OnceRow, Shell.ShellText.SessionRow(request), Shell.ShellText.PermanentRow(request)], spec.Choices.Select(c => c.Label));
        Assert.Equal(Shell.CommandChoice.Permanent, App.ChatScreen.ApprovalChoice(new WebAnswerRequest { Choice = "permanent" }));
        Assert.Equal(Shell.CommandChoice.Deny, App.ChatScreen.ApprovalChoice(new WebAnswerRequest { Choice = "?" }));
    }

    // ── The session ──────────────────────────────────────────────────────────

    [Fact]
    public void AMessage_Waits_ForTheIdleLine_AndWakesIt()
    {
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Id = "m1", Text = "hello" }, null).Outcome);

        Assert.Equal(1, _nudges);
        Assert.Equal(["hello"], _session.Waiting);
        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal(new WebLine("hello", "m1"), line);
        Assert.False(_session.LinesReady);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("/clear")]
    [InlineData("/settings")]
    [InlineData("/nosuch thing")]
    public void ABlankOrASlashLine_IsRefused(string text)
    {
        var reply = _session.Send(new WebSendRequest { Text = text }, null);

        Assert.Equal(WebOutcome.Refused, reply.Outcome);
        Assert.False(_session.LinesReady);
    }

    [Fact]
    public void ABareWord_IsAMessage_NotACommand()
    {
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = "clear" }, null).Outcome);
    }

    [Fact]
    public void TheCap_IsInBytes_AndTheQueueHoldsTwenty()
    {
        string wide = new('界', WebUiSession.MaxMessageBytes / 3 + 1);   // three bytes each in UTF-8
        Assert.Equal(WebOutcome.Refused, _session.Send(new WebSendRequest { Text = wide }, null).Outcome);

        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Id = "q" + i, Text = "m" + i }, null).Outcome);
        }

        Assert.Equal(WebOutcome.TooMany, _session.Send(new WebSendRequest { Id = "late", Text = "late" }, null).Outcome);
        Assert.Equal(WebUiSession.MaxWaiting, _session.WaitingCount);
    }

    [Fact]
    public void ARetry_OfAMessageTaken_IsNotSentTwice()
    {
        _session.Send(new WebSendRequest { Id = "same", Text = "once" }, null);
        var again = _session.Send(new WebSendRequest { Id = "same", Text = "once" }, null);

        Assert.Equal(WebOutcome.Ok, again.Outcome);
        Assert.Equal(1, _session.WaitingCount);
    }

    [Fact]
    public void ThePcsPasteStandIns_AreTakenOut()
    {
        string token = new([(char)0xE000]);
        Assert.True(PasteBlocks.IsToken(token[0]));
        _session.Send(new WebSendRequest { Text = "see " + token + " here" }, null);

        Assert.True(_session.TryTakeLine(out var line));
        Assert.Equal("see  here", line.Text);
    }

    [Fact]
    public void DroppedAndWithdrawnLines_GoBackToThePage()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Send(new WebSendRequest { Text = "one" }, null);
        _session.Send(new WebSendRequest { Text = "two" }, null);

        Assert.Equal(2, _session.DropLines());
        _session.Withdrawn("three");

        var events = Drain(subscription);
        Assert.Equal(["dropped:one", "dropped:two", "withdrawn:three"], events.Where(e => e.Name != "state").Select(e => e.Name + ":" + JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text));
        Assert.False(_session.LinesReady);
    }

    [Fact]
    public void Notices_WaitForTheScreen_InOrder()
    {
        _session.Notice("a");
        _session.Notice("b", error: true);

        Assert.True(_session.NoticesReady);
        Assert.Equal([("a", false), ("b", true)], _session.TakeNotices());
        Assert.False(_session.NoticesReady);
        Assert.Equal(2, _nudges);
    }

    [Fact]
    public void TheFeed_AndTheAsks_ReachThePages()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Feed.Begin(null);
        _session.Feed.Append("**hi**");
        _session.Feed.End();
        var handle = _session.Asks.Open(YesNo());
        _session.Asks.Close(handle);

        var events = Drain(subscription);
        Assert.Equal(["append", "delta", "end", "asks", "asks"], events.Select(e => e.Name));
        Assert.Contains("<strong>hi</strong>", JsonSerializer.Deserialize(events[2].Json.Value, WebUiJsonContext.Default.WebEnd)!.Html, StringComparison.Ordinal);
        Assert.Contains("\"Go?\"", events[3].Json.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheState_IsSentWhenItChanges_AndOnlyThen()
    {
        using var subscription = _session.Events.Subscribe();

        _time.Advance(WebUiSession.StateInterval);
        _time.Advance(WebUiSession.StateInterval);
        _session.Send(new WebSendRequest { Text = "x" }, null);

        var states = Drain(subscription).Where(e => e.Name == "state").ToList();
        Assert.Equal(2, states.Count);   // the first tick, then the queued line; the second tick changed nothing
    }

    private static List<WebEvent> Drain(WebEventHub.Subscription subscription)
    {
        var events = new List<WebEvent>();
        while (subscription.Reader.TryRead(out var item))
        {
            events.Add(item);
        }

        return events;
    }

    // ── Shopping ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheShoppingPage_IsTheListInTheAppsOwnWords()
    {
        _list.Add("Milk", 2, null, "whole");
        var bread = _list.Add("Bread", 1, null)!.Item;
        _list.Check(bread, true);

        var page = _session.Shopping();

        Assert.Equal(ShoppingText.List(_list.View(null), withTicked: false).Split('\n')[0], page.Header);
        var milk = Assert.Single(Assert.Single(page.Groups).Items);
        Assert.Equal(("Milk ×2", "whole", false), (milk.Text, milk.Note, milk.Checked));
        Assert.Equal(ShoppingText.UnplacedHeading, page.Groups[0].Heading);
        Assert.Equal(ShoppingText.TickedHeading, page.TickedHeading);
        Assert.Equal("Bread", Assert.Single(page.Ticked).Text);
    }

    [Fact]
    public void ATick_IsExplicit_AndAGoneLineIsNotFound()
    {
        var milk = _list.Add("Milk", 1, null)!.Item;

        Assert.Equal(WebOutcome.Ok, _session.Check(new WebCheckRequest { Id = milk.Id, On = true }, null).Outcome);
        Assert.Equal(WebOutcome.Ok, _session.Check(new WebCheckRequest { Id = milk.Id, On = true }, null).Outcome);   // again: no flip back
        Assert.True(_list.Store.Items().Single().Checked);
        Assert.Equal(1, _shoppingChanges);
        Assert.Equal(WebOutcome.NotFound, _session.Check(new WebCheckRequest { Id = 9999, On = true }, null).Outcome);
    }

    // ── The words, the token, the reply's HTML ───────────────────────────────

    [Fact]
    public void TheReach_ReadsItsTwoWords()
    {
        Assert.Equal(["local", "lan"], WebUiReach.Names);
        Assert.Equal("lan", WebUiReach.Default);
        Assert.True(WebUiReach.TryParse(" LOCAL ", out var local) && local == WebUiAudience.Local);
        Assert.False(WebUiReach.TryParse("internet", out var fallback));
        Assert.Equal(WebUiAudience.LocalAreaNetwork, fallback);
        Assert.Equal(WebUiAudience.LocalAreaNetwork, WebUiReach.Resolve(new Settings.AppSettingsData { WebUiReach = "nope" }));
    }

    [Fact]
    public void TheToken_TheCookie_AndTheLink()
    {
        string token = WebUiAuth.NewToken();
        Assert.True(WebUiAuth.IsWellFormed(token));
        Assert.NotEqual(token, WebUiAuth.NewToken());
        Assert.True(WebUiAuth.Matches(token, token));
        Assert.False(WebUiAuth.Matches(token[..^1] + "0", token) && token[^1] != '0');
        Assert.False(WebUiAuth.Matches("", ""));
        Assert.False(WebUiAuth.Matches(null, token));
        Assert.Equal("neon_webui_8765", WebUiAuth.CookieName(8765));
        Assert.Equal("http://192.168.1.5:8765/signin?t=abc", WebUiAuth.SignInLink("192.168.1.5", 8765, "abc"));
        Assert.Equal("http://[fd00::1]:8765/", WebUiAuth.Address("fd00::1", 8765));
        Assert.Equal("on lan:8765", new WebUiWanted(true, 8765, WebUiAudience.LocalAreaNetwork, "secret").ToString());   // never the token
    }

    [Fact]
    public void AReplysHtml_KeepsSafeLinksOnly_AndNoPicture()
    {
        string html = WebUiMarkdown.ToHtml("[ok](https://example.com/a) [bad](javascript:alert(1)) ![pic](http://tracker/x.png) <b>raw</b> <https://auto.example>");

        Assert.Contains("href=\"https://example.com/a\"", html, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", html, StringComparison.Ordinal);
        Assert.Contains("bad", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://auto.example\"", html, StringComparison.Ordinal);
        Assert.Null(WebUiMarkdown.SafeLink("file:///c:/x"));
        Assert.Equal("mailto:a@b.c", WebUiMarkdown.SafeLink("mailto:a@b.c"));
    }

    // ── The settings ─────────────────────────────────────────────────────────

    [Fact]
    public void TheThreeRows_AreRefusedUnderAReply_AndRestartTheServer()
    {
        foreach (var field in new[] { App.SettingsField.WebUi, App.SettingsField.WebUiPort, App.SettingsField.WebUiReach })
        {
            Assert.True(App.SettingsMenu.IsWebUiField(field));
            Assert.True(App.SettingsMenu.RefusedMidTurn(field));
        }

        Assert.True(App.SettingsMenu.IsToggle(App.SettingsField.WebUi));
        Assert.Equal("must be a port from 1024 to 65535", App.SettingsMenu.WebUiPortRangeError);
        Assert.Equal([App.SettingsField.WebUi, App.SettingsField.WebUiPort, App.SettingsField.WebUiReach], App.SettingsMenu.TabFields[(int)App.SettingsTab.WebUi]);
        Assert.Equal("Web UI", App.SettingsMenu.TabTitles[(int)App.SettingsTab.WebUi]);
        var defaults = new Settings.AppSettingsData();
        Assert.Equal((false, 8765, "lan", ""), (defaults.WebUi, defaults.WebUiPort, defaults.WebUiReach, defaults.WebUiToken));
        Assert.Contains(nameof(Settings.AppSettingsData.WebUiToken), Settings.SettingsDiff.Secrets);
    }

    [Fact]
    public void ACopiedProfile_KeepsItsOwnToken_AndANewOneHasNone()
    {
        Settings.Profiles.Create(_dir, "from", new Settings.AppSettingsData { WebUi = true, WebUiToken = "dpapi:from" });
        Settings.Profiles.Create(_dir, "to", new Settings.AppSettingsData { WebUiToken = "dpapi:to" });

        Settings.Profiles.CopySettings(_dir, "from", "to");
        Settings.Profiles.CopySettings(_dir, "from", "fresh");

        var to = Settings.Profiles.ReadProfileFile(Settings.Profiles.ProfileFile(_dir, "to"));
        Assert.True(to.WebUi);   // the rest copied
        Assert.Equal("dpapi:to", to.WebUiToken);
        Assert.Equal("", Settings.Profiles.ReadProfileFile(Settings.Profiles.ProfileFile(_dir, "fresh")).WebUiToken);
    }

    // ── The sign-in addresses ────────────────────────────────────────────────

    private static WebUiAdapter Adapter(System.Net.NetworkInformation.NetworkInterfaceType type, string address, bool gateway, long speed = 1_000_000_000) =>
        new(type, [IPAddress.Parse(address)], gateway, speed);

    [Fact]
    public void TheHomeNetwork_ThenTailscale_NotNordLynxNorWsl()
    {
        // This machine on 2026-10-14 (the user's question: three addresses): NordLynx and Tailscale are tunnels (a virtual type), WSL's switch
        // is Ethernet with no gateway, and the Realtek card is the home network; Tailscale's own address follows it (later that day).
        var adapters = new[]
        {
            Adapter((System.Net.NetworkInformation.NetworkInterfaceType)53, "10.5.0.2", gateway: false),
            Adapter((System.Net.NetworkInformation.NetworkInterfaceType)53, "100.78.20.125", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.17.224.1", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true, speed: 2_500_000_000),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Loopback, "127.0.0.1", gateway: false),
        };

        Assert.Equal(["192.168.1.217", "100.78.20.125"], WebUiController.ChooseAddresses(adapters));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("100.64.0.1")));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("100.127.255.254")));
        Assert.False(WebUiController.IsTailnet(IPAddress.Parse("100.128.0.1")));
        Assert.False(WebUiController.IsTailnet(IPAddress.Parse("10.5.0.2")));
    }

    [Fact]
    public void TailscaleDisconnected_LeavesTheHomeNetworkAlone()
    {
        var adapters = new[]
        {
            Adapter((System.Net.NetworkInformation.NetworkInterfaceType)53, "169.254.83.107", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true),
        };

        Assert.Equal(["192.168.1.217"], WebUiController.ChooseAddresses(adapters));
    }

    [Fact]
    public void WiredAndWireless_BothShow_TheFasterFirst()
    {
        var adapters = new[]
        {
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211, "192.168.1.50", gateway: true, speed: 300_000_000),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "192.168.1.217", gateway: true, speed: 2_500_000_000),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "8.8.8.8", gateway: true),   // not private: never
        };

        Assert.Equal(["192.168.1.217", "192.168.1.50"], WebUiController.ChooseAddresses(adapters));
    }

    [Fact]
    public void WithNoHomeAdapter_EveryPrivateAddressIsOffered()
    {
        var adapters = new[]
        {
            Adapter((System.Net.NetworkInformation.NetworkInterfaceType)53, "100.101.102.103", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "172.17.224.1", gateway: false),
            Adapter(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "169.254.1.1", gateway: false),
        };

        Assert.Equal(["100.101.102.103", "172.17.224.1"], WebUiController.ChooseAddresses(adapters));
        Assert.Empty(WebUiController.ChooseAddresses([]));
    }

    // ── The controller ───────────────────────────────────────────────────────

    private sealed class FakeHost : IWebUiHost
    {
        public static List<FakeHost> Made { get; } = [];

        public WebUiBinding? Started { get; private set; }

        public bool Stopped { get; private set; }

        public Exception? Fail { get; init; }

        public int Port => Started?.Port ?? 0;

        public Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

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
    public async Task TheController_StartsStopsAndRestarts_AsTheSettingsSay()
    {
        var made = new List<FakeHost>();
        await using var controller = new WebUiController(_ => { var host = new FakeHost(); made.Add(host); return host; }, _session);

        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t1"));
        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t1"));   // the same: nothing
        Assert.Single(made);
        Assert.Equal(new WebUiBinding(WebUiAudience.Local, 8765, "t1"), made[0].Started);
        Assert.True(controller.Serving);
        Assert.Contains(_session.TakeNotices(), n => n.Text == WebUiText.ServingNotice("http://localhost:8765/"));
        Assert.Equal(["http://localhost:8765/signin?t=t1"], controller.Links("t1"));

        await controller.ReconcileAsync(new WebUiWanted(true, 8766, WebUiAudience.Local, "t1"));
        Assert.True(made[0].Stopped);
        Assert.Equal(8766, made[1].Started!.Port);

        await controller.ReconcileAsync(WebUiWanted.Off);
        Assert.True(made[1].Stopped);
        Assert.False(controller.Serving);
        Assert.Contains(_session.TakeNotices(), n => n.Text == WebUiText.StoppedNotice);
    }

    [Fact]
    public async Task APortThatCannotBeOpened_IsOneError_AndNotTriedAgainUntilTheSettingsChange()
    {
        int made = 0;
        await using var controller = new WebUiController(_ => { made++; return new FakeHost { Fail = new IOException("Failed to bind") }; }, _session);
        var wanted = new WebUiWanted(true, 8765, WebUiAudience.LocalAreaNetwork, "t");

        await controller.ReconcileAsync(wanted);
        await controller.ReconcileAsync(wanted);

        Assert.Equal(1, made);
        Assert.False(controller.Serving);
        var notice = Assert.Single(_session.TakeNotices());
        Assert.True(notice.Error);
        Assert.Equal(WebUiText.PortFailedError(8765, "Failed to bind"), notice.Text);
    }

    [Fact]
    public async Task WithoutAFactory_ThereIsNoWebUi()
    {
        await using var controller = new WebUiController(null, _session);

        await controller.ReconcileAsync(new WebUiWanted(true, 8765, WebUiAudience.Local, "t"));

        Assert.False(controller.Available);
        Assert.False(controller.Serving);
        Assert.Empty(_session.TakeNotices());
    }
}
