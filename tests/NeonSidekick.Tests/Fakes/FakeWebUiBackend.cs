using System.Collections.Concurrent;
using System.Net;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests.Fakes;

/// <summary>The Web UI host's other side for <c>WebUiHostTests</c>: records every call and answers what the test sets.</summary>
internal sealed class FakeWebUiBackend : IWebUiBackend
{
    public WebEventHub Events { get; } = new();

    public ConcurrentQueue<WebSendRequest> Sent { get; } = new();

    public ConcurrentQueue<WebAnswerRequest> Answers { get; } = new();

    public ConcurrentQueue<WebCheckRequest> Checks { get; } = new();

    public ConcurrentQueue<IPAddress?> SignIns { get; } = new();

    public int Stops;

    public WebReply Reply { get; set; } = WebReply.Ok;

    public WebShopping List { get; set; } = new("Shopping list: 0 items to get.", null, [], "Ticked", [], "(guess)");

    public WebSnapshot Snap { get; set; } = new([new WebEntry(1, "user", "hello", null, null, false)], [], new WebState("p", "", "", "m", false, false, false, "", [], null, "1"));

    public WebSnapshot Snapshot() => Snap;

    public void SignedIn(IPAddress? remote) => SignIns.Enqueue(remote);

    public WebReply Send(WebSendRequest request, IPAddress? remote)
    {
        Sent.Enqueue(request);
        if (Reply.Outcome == WebOutcome.Ok && !string.IsNullOrWhiteSpace(request.Text))
        {
            lock (ListsGate)
            {
                HistoryLines.Add(request.Text);   // remembered as accepted (2026-10-16), as the screen's session does
            }
        }

        return Reply;
    }

    public WebReply Stop(IPAddress? remote)
    {
        Interlocked.Increment(ref Stops);
        return Reply;
    }

    public WebReply Answer(WebAnswerRequest request, IPAddress? remote)
    {
        Answers.Enqueue(request);
        return Reply;
    }

    public WebShopping Shopping() => List;

    public WebReply Check(WebCheckRequest request, IPAddress? remote)
    {
        Checks.Enqueue(request);
        return Reply;
    }

    public ConcurrentQueue<WebStoreRequest> Stores { get; } = new();

    public WebReply UseStore(WebStoreRequest request, IPAddress? remote)
    {
        Stores.Enqueue(request);
        return Reply;
    }

    /// <summary>
    /// The Shopping tab's list controls (2026-10-16): every call as <c>verb args</c> (<c>add 2 milk</c>, <c>quantity 7 3</c>, <c>checkall True</c>,
    /// <c>clear</c>…), answered with <see cref="ShopReply"/> or else <see cref="Reply"/>. <see cref="OnShop"/> lets a page test change
    /// <see cref="List"/> as the real list would.
    /// </summary>
    public ConcurrentQueue<string> ShopCalls { get; } = new();

    public WebReply? ShopReply { get; set; }

    public Action<string>? OnShop { get; set; }

    /// <summary>The past items the 🕘 sheet lists; null answers the read with a 404.</summary>
    public WebBought? BoughtList { get; set; } = new([], "Nothing yet.");

    public WebBought? ShopBought() => BoughtList;

    private WebReply Shop(string call)
    {
        ShopCalls.Enqueue(call);
        OnShop?.Invoke(call);
        return ShopReply ?? Reply;
    }

    public WebReply ShopAdd(WebTextBody request, IPAddress? remote) => Shop("add " + request.Text);

    public WebReply ShopReAdd(WebShopId request, IPAddress? remote) => Shop("readd " + Num(request.Id));

    public WebReply ShopForget(WebShopId request, IPAddress? remote) => Shop("forget " + Num(request.Id));

    public WebReply ShopQuantity(WebShopQuantity request, IPAddress? remote) => Shop("quantity " + Num(request.Id) + " " + Num(request.Qty));

    public WebReply ShopRename(WebShopRename request, IPAddress? remote) => Shop("rename " + Num(request.Id) + " " + request.Name);

    public WebReply ShopRemove(WebShopId request, IPAddress? remote) => Shop("remove " + Num(request.Id));

    public WebReply ShopCheckAll(WebShopCheckAll request, IPAddress? remote) => Shop("checkall " + request.On);

    public WebReply ShopClearTicked(IPAddress? remote) => Shop("clear");

    private static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string PictureRun { get; set; } = "0123456789abcdef0123456789abcdef";

    /// <summary>The pictures the transcript holds, by id.</summary>
    public ConcurrentDictionary<int, WebPictureSource> Pictures { get; } = new();

    public WebPictureSource? Picture(int id) => Pictures.GetValueOrDefault(id);

    public ConcurrentQueue<WebCompleteRequest> Completed { get; } = new();

    public WebCompletion Completion { get; set; } = WebCompletion.Empty(0);

    public WebCompletion Complete(WebCompleteRequest request)
    {
        Completed.Enqueue(request);
        return Completion with { Seq = request.Seq };
    }

    public WebUsageCard Card { get; set; } = new(
        "📊 Usage",
        new WebUsageContext("Context", "1.0k", "8.0k", 13, "configured"),
        [new WebUsageSection("Last reply", "", [new WebUsageRow("Total", "1,000"), new WebUsageRow("Speed", "42.0 tok/s")])],
        null);

    public WebUsageCard Usage() => Card;

    /// <summary>
    /// The saved prompts the sheet lists (later on 2026-10-15); each one's id is <c>p</c> and its place from 1 (2026-10-16), so a delete names
    /// one. Under <see cref="ListsGate"/> while a page may read or change them.
    /// </summary>
    public List<string> PromptList { get; } = [];

    /// <summary>The lock over the sheet's lists (2026-10-16): the page's requests change them on Kestrel's threads.</summary>
    public object ListsGate { get; } = new();

    public WebPrompts Prompts()
    {
        lock (ListsGate)
        {
            return new([.. PromptList.Select((t, i) => new WebPrompt(i + 1, t, PromptId(i + 1)))]);
        }
    }

    /// <summary>The fake's id for the prompt at <paramref name="number"/>.</summary>
    public static string PromptId(int number) => "p" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The answer a sheet change gets (Ok with a sentence of the fake's own unless set), and the requests it was asked (2026-10-16).</summary>
    public WebReply? ListReply { get; set; }

    public ConcurrentQueue<string> PromptsSaved { get; } = new();

    public ConcurrentQueue<string> PromptsDeleted { get; } = new();

    public ConcurrentQueue<string> MemoriesSaved { get; } = new();

    public ConcurrentQueue<string> MemoriesDeleted { get; } = new();

    public int HistoryClears;

    public WebReply SavePrompt(WebTextBody request, IPAddress? remote)
    {
        PromptsSaved.Enqueue(request.Text ?? "");
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            PromptList.Add(request.Text ?? "");
        }

        return new WebReply(WebOutcome.Ok, "Saved.");
    }

    public WebReply DeletePrompt(WebPromptDelete request, IPAddress? remote)
    {
        PromptsDeleted.Enqueue(request.Id ?? "");
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            int at = Enumerable.Range(1, PromptList.Count).FirstOrDefault(n => PromptId(n) == request.Id);
            if (at == 0)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.PromptGone);
            }

            PromptList.RemoveAt(at - 1);
        }

        return new WebReply(WebOutcome.Ok, "Removed.");
    }

    /// <summary>The memories the 💾 tab lists, and whether the mode is off (2026-10-16).</summary>
    public List<WebMemory> MemoryList { get; } = [];

    public bool MemoryModeOff { get; set; }

    public WebMemories Memories()
    {
        lock (ListsGate)
        {
            return new([.. MemoryList], MemoryModeOff, MemoryMaxChars);
        }
    }

    public WebReply SaveMemory(WebTextBody request, IPAddress? remote)
    {
        MemoriesSaved.Enqueue(request.Text ?? "");
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            MemoryList.Add(new WebMemory(request.Text ?? "", false));
        }

        return new WebReply(WebOutcome.Ok, "Remembered.");
    }

    public WebReply DeleteMemory(WebTextBody request, IPAddress? remote)
    {
        MemoriesDeleted.Enqueue(request.Text ?? "");
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            if (MemoryList.RemoveAll(m => m.Text == request.Text) == 0)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
            }
        }

        return new WebReply(WebOutcome.Ok, "Removed.");
    }

    /// <summary>The 💾 tab's edits (old → new) and off/ons (text, off) asked (2026-10-18); each changes <see cref="MemoryList"/> as the store would.</summary>
    public ConcurrentQueue<(string Old, string Text)> MemoriesEdited { get; } = new();

    public ConcurrentQueue<(string Text, bool Off)> MemoriesToggled { get; } = new();

    /// <summary>The cap <see cref="Memories"/> reports (2026-10-18).</summary>
    public int MemoryMaxChars { get; set; } = 300;

    public WebReply EditMemory(WebMemoryEdit request, IPAddress? remote)
    {
        MemoriesEdited.Enqueue((request.Old ?? "", request.Text ?? ""));
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            int at = MemoryList.FindIndex(m => m.Text == request.Old);
            if (at < 0)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
            }

            MemoryList[at] = MemoryList[at] with { Text = request.Text ?? "" };
        }

        return new WebReply(WebOutcome.Ok, "Edited.");
    }

    public WebReply ToggleMemory(WebMemoryToggle request, IPAddress? remote)
    {
        MemoriesToggled.Enqueue((request.Text ?? "", request.Off == true));
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            int at = MemoryList.FindIndex(m => m.Text == request.Text);
            if (at < 0)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.MemoryGone);
            }

            MemoryList[at] = MemoryList[at] with { Off = request.Off == true };
        }

        return new WebReply(WebOutcome.Ok, request.Off == true ? "Turned off." : "Turned on.");
    }

    /// <summary>The message history the page walks (2026-10-16), oldest first.</summary>
    public List<string> HistoryLines { get; } = [];

    public WebHistory History()
    {
        lock (ListsGate)
        {
            return new([.. HistoryLines]);
        }
    }

    public WebReply ClearHistory(IPAddress? remote)
    {
        Interlocked.Increment(ref HistoryClears);
        if (ListReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            HistoryLines.Clear();
        }

        return new WebReply(WebOutcome.Ok, "Cleared.");
    }

    /// <summary>The status row's 🧠 (2026-10-17): how many cancels arrived; <see cref="LearnReply"/> answers them (Ok by default).</summary>
    public int LearnCancels;

    public WebReply? LearnReply { get; set; }

    public WebReply CancelLearning(IPAddress? remote)
    {
        Interlocked.Increment(ref LearnCancels);
        return LearnReply ?? WebReply.Ok;
    }

    /// <summary>The held queue's Resume (2026-10-17): how many arrived; <see cref="ResumeReply"/> answers them (Ok by default).</summary>
    public int Resumes;

    public WebReply? ResumeReply { get; set; }

    public WebReply ResumeQueue(IPAddress? remote)
    {
        Interlocked.Increment(ref Resumes);
        return ResumeReply ?? WebReply.Ok;
    }

public IWebHome? Home { get; set; }

    /// <summary>
    /// The YouTube tab's player (2026-10-16): a real <see cref="WebVideoPlayer"/> (portable, on the test's clock when one is given), so the host's
    /// tests run the same owner rules the player's own pin; null for a backend with no player.
    /// </summary>
    public WebVideoPlayer? VideoPlayer { get; set; }

    public IWebVideo? Video => VideoPlayer;

    // ── A bot check's card (2026-10-17) ──────────────────────────────────────

    /// <summary>The live check's ask id: the only one the card's picture and input reach; null with none.</summary>
    public string? BotCheckLiveId { get; set; }

    /// <summary><i>Web UI bot check control</i>: off answers Forbidden.</summary>
    public bool BotCheckControl { get; set; } = true;

    /// <summary>The window's picture; null: it didn't draw (the host's 503).</summary>
    public WebBotCheckFrame? BotCheckPicture { get; set; } = new(FakeBrowserWindow.TinyJpeg, "aaaabbbbccccdddd");

    /// <summary>Each picture's width asked for, in order.</summary>
    public ConcurrentQueue<int> BotCheckWidths { get; } = new();

    /// <summary>Each input sent, in order.</summary>
    public ConcurrentQueue<WebBotCheckInput> BotCheckInputs { get; } = new();

    public Task<(WebOutcome Outcome, WebBotCheckFrame? Frame)> BotCheckFrameAsync(string askId, int width, CancellationToken cancellationToken)
    {
        BotCheckWidths.Enqueue(width);
        return Task.FromResult<(WebOutcome, WebBotCheckFrame?)>(
            !BotCheckControl ? (WebOutcome.Forbidden, null)
            : askId != BotCheckLiveId ? (WebOutcome.NotFound, null)
            : (WebOutcome.Ok, BotCheckPicture));
    }

    public Task<WebReply> BotCheckInputAsync(WebBotCheckInput input, IPAddress? remote, CancellationToken cancellationToken)
    {
        if (!BotCheckControl)
        {
            return Task.FromResult(new WebReply(WebOutcome.Forbidden, WebUiText.BotCheckControlOff));
        }

        if (input.AskId != BotCheckLiveId)
        {
            return Task.FromResult(new WebReply(WebOutcome.NotFound, WebUiText.BotCheckGone));
        }

        if (NeonSidekick.App.ChatScreen.BotCheckInputOf(input) is not { } read || NeonSidekick.Viewer.BrowserRemote.Check(read) is not null)
        {
            return Task.FromResult(new WebReply(WebOutcome.Refused, "refused"));
        }

        BotCheckInputs.Enqueue(input);
        return Task.FromResult(WebReply.Ok);
    }

    // ── The YouTube tab's saved list (later on 2026-10-16), over an in-memory list ─────────────────────────────

    /// <summary>The saved list, the user's order; guarded by <see cref="ListsGate"/>.</summary>
    public List<WebSavedVideo> SavedList { get; } = [];

    /// <summary>The video the player has open for the ☆, or null.</summary>
    public WebSavedPlaying? SavedPlaying { get; set; }

    /// <summary>Whether titles are being looked up.</summary>
    public bool SavedLooking { get; set; }

    /// <summary>False: no saved list (a 404).</summary>
    public bool SavedOn { get; set; } = true;

    /// <summary>A reply every change returns instead of changing the list, when set.</summary>
    public WebReply? SavedReply { get; set; }

    /// <summary>Every change asked for, in order: <c>play page id</c>, <c>remove id</c>, <c>clear</c>, <c>move id to</c>, <c>save</c>.</summary>
    public ConcurrentQueue<string> SavedChanges { get; } = new();

    /// <summary>A saved video for the list, its footer lines made up.</summary>
    public static WebSavedVideo SavedVideo(string id, string title, string? channel = "Channel", bool watched = false) =>
        new(id, title, channel, watched ? "watched" : "at 1:00 of 10:00", watched, title, "saved today · id " + id);

    public WebSavedVideos? SavedVideos()
    {
        if (!SavedOn)
        {
            return null;
        }

        lock (ListsGate)
        {
            return new WebSavedVideos([.. SavedList], NeonSidekick.YouTube.YouTubeText.SavedCaption(SavedList.Count), NeonSidekick.YouTube.YouTubeText.NoneSaved, SavedLooking, SavedPlaying);
        }
    }

    public WebReply PlaySaved(WebSavedPlay request, IPAddress? remote)
    {
        SavedChanges.Enqueue("play " + request.Page + " " + request.Id);
        if (SavedReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            if (!SavedList.Any(v => v.Id == request.Id))
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
            }
        }

        if (VideoPlayer is { } player)
        {
            if (player.Claim(request.Page ?? "", remote) == WebOutcome.Conflict)
            {
                return new WebReply(WebOutcome.Conflict, WebUiText.YouTubePageNotConnected);
            }

            player.Play(new NeonSidekick.Viewer.VideoRequest(request.Id!, 57));
        }

        return new WebReply(WebOutcome.Ok, "Resumed the saved video at 0:57, a moment before where it was left.");
    }

    public WebReply RemoveSaved(WebSavedId request, IPAddress? remote)
    {
        SavedChanges.Enqueue("remove " + request.Id);
        if (SavedReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            return SavedList.RemoveAll(v => v.Id == request.Id) > 0 ? WebReply.Ok : new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
        }
    }

    public WebReply ClearSaved(IPAddress? remote)
    {
        SavedChanges.Enqueue("clear");
        if (SavedReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            SavedList.Clear();
        }

        return WebReply.Ok;
    }

    public WebReply MoveSaved(WebSavedMove request, IPAddress? remote)
    {
        SavedChanges.Enqueue("move " + request.Id + " " + request.To);
        if (SavedReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            int at = SavedList.FindIndex(v => v.Id == request.Id);
            if (at < 0)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.SavedVideoGone);
            }

            int to = request.To switch { "up" => at - 1, "down" => at + 1, "top" => 0, "bottom" => SavedList.Count - 1, _ => -2 };
            if (to == -2)
            {
                return new WebReply(WebOutcome.Refused, WebUiText.SavedMoveUnknown);
            }

            var video = SavedList[at];
            SavedList.RemoveAt(at);
            SavedList.Insert(Math.Clamp(to, 0, SavedList.Count), video);
        }

        return WebReply.Ok;
    }

    public WebReply SaveCurrent(IPAddress? remote)
    {
        SavedChanges.Enqueue("save");
        if (SavedReply is { } reply)
        {
            return reply;
        }

        lock (ListsGate)
        {
            if (SavedPlaying is not { } playing)
            {
                return new WebReply(WebOutcome.Refused, NeonSidekick.YouTube.YouTubeText.NothingToSave);
            }

            if (!SavedList.Any(v => v.Id == playing.Id))
            {
                SavedList.Add(SavedVideo(playing.Id, playing.Title ?? playing.Id));
            }

            SavedPlaying = playing with { Saved = true };
        }

        return new WebReply(WebOutcome.Ok, "Saved it.");
    }

    public Dictionary<long, WebEntry> Entries { get; } = [];

    public WebEntry? Entry(long seq) => Entries.GetValueOrDefault(seq);

    public WebPulse PulseNow { get; set; } = new(false, 0, null, "", "tag");

    public WebPulse Pulse() => PulseNow;

    private readonly object _tabsGate = new();
    private WebTabs _tabs = WebTabs.All;

    /// <summary>The tabs on (later on 2026-10-15), every one by default; a test may flip them while a stream is open.</summary>
    public WebTabs TabsOn
    {
        get { lock (_tabsGate) { return _tabs; } }
        set { lock (_tabsGate) { _tabs = value; } }
    }

    public WebTabs Tabs() => TabsOn;

    /// <summary>The strip's picks from the page (later on 2026-10-15), and the ids the strip holds (a pick of any other is not found).</summary>
    public ConcurrentQueue<int> StripPicks { get; } = new();

    public ConcurrentDictionary<int, bool> StripIds { get; } = new();

    public WebReply SelectStripPicture(WebStripSelect request, IPAddress? remote)
    {
        if (!StripIds.ContainsKey(request.Id))
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.NoSuchPicture);
        }

        StripPicks.Enqueue(request.Id);
        return Reply;
    }

    /// <summary>The page's deletes (later on 2026-10-15), and the answer each gets (Ok with <see cref="DeleteMessage"/> unless set).</summary>
    public ConcurrentQueue<int> Deleted { get; } = new();

    public WebReply? DeleteReply { get; set; }

    public string DeleteMessage { get; set; } = "Moved pic.png to the Recycle bin.";

    public WebReply DeletePicture(WebPictureDelete request, IPAddress? remote)
    {
        Deleted.Enqueue(request.Id);
        return DeleteReply ?? new WebReply(WebOutcome.Ok, DeleteMessage);
    }

    public string Css { get; set; } = ":root { --accent: #123456; }\n";

    public string ThemeCss() => Css;

    public string Manifest() => WebTheme.Manifest(NeonSidekick.UI.Theme.Current, on: true);
}
