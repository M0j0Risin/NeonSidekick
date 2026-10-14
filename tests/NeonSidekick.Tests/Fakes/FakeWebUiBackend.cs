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
}
