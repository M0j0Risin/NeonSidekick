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
            return new([.. MemoryList], MemoryModeOff);
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

    public IWebHome? Home { get; set; }

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
