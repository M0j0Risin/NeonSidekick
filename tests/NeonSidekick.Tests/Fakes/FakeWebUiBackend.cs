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

    public WebUsageCard Card { get; set; } = new("Usage", ["Context: 1,000 of 8,000 tokens (13%)"]);

    public WebUsageCard Usage() => Card;

    public IWebHome? Home { get; set; }

    public Dictionary<long, WebEntry> Entries { get; } = [];

    public WebEntry? Entry(long seq) => Entries.GetValueOrDefault(seq);

    public WebPulse PulseNow { get; set; } = new(false, 0, null, "", "tag");

    public WebPulse Pulse() => PulseNow;

    public string Css { get; set; } = ":root { --accent: #123456; }\n";

    public string ThemeCss() => Css;

    public string Manifest() => WebTheme.Manifest(NeonSidekick.UI.Theme.Current, on: true);
}
