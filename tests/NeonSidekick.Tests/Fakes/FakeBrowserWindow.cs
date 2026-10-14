using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Browser window without WebView2 (2026-10-13): every bot check and <c>/browser</c> visit recorded; a check answers
/// <see cref="Answer"/> at once (cleared with <see cref="Html"/> by default), or with <see cref="Hold"/> waits until its token is
/// cancelled and ends as given up, noting the cancel; a visit answers <see cref="Visit"/>; a clear (later on 2026-10-13) is recorded and
/// answers <see cref="ClearAnswer"/>; every <see cref="Close"/> is counted.
/// </summary>
public sealed class FakeBrowserWindow : IBrowserWindow
{
    public List<BotCheckRequest> Solves { get; } = new();

    public List<Uri?> Browses { get; } = new();

    /// <summary>The page a check clears to when no <see cref="Answer"/> is set.</summary>
    public string Html { get; set; } = "<html><head><title>Example Domain</title></head><body><h1>Example Domain</h1><p>Through the check.</p></body></html>";

    public Func<BotCheckRequest, BotCheckResult>? Answer { get; set; }

    public bool Hold { get; set; }

    public bool CancelSeen { get; private set; }

    public BrowserVisit Visit { get; set; } = BrowserVisit.Empty;

    public Task<BotCheckResult> SolveAsync(BotCheckRequest request, CancellationToken cancellationToken)
    {
        Solves.Add(request);
        if (!Hold)
        {
            return Task.FromResult(Answer?.Invoke(request) ?? Cleared(request, Html));
        }

        var held = new TaskCompletionSource<BotCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() =>
        {
            CancelSeen = true;
            held.TrySetResult(BotCheckResult.Ended(BotCheckEnd.GaveUp, request.Url.AbsoluteUri));
        });
        return held.Task;
    }

    public Task<BrowserVisit> BrowseAsync(Uri? url)
    {
        Browses.Add(url);
        return Task.FromResult(Visit);
    }

    public List<BrowserClear> Clears { get; } = new();

    /// <summary>What a clear answers: null (done) by default, else why it failed.</summary>
    public string? ClearAnswer { get; set; }

    public int Closes { get; private set; }

    public bool Close()
    {
        Closes++;
        return false;
    }

    public Task<string?> ClearAsync(BrowserClear kind)
    {
        Clears.Add(kind);
        return Task.FromResult(ClearAnswer);
    }

    /// <summary>A check cleared to <paramref name="html"/> with one clearance cookie for the host, as Edge 155.</summary>
    public static BotCheckResult Cleared(BotCheckRequest request, string? html) =>
        new(BotCheckEnd.Cleared, html, request.Url.AbsoluteUri, 200,
            [new BrowserCookie("cf_clearance", "solved", "." + request.Url.Host, "/", -1, true, true)],
            new BrowserIdentity(BrowserBrand.Edge, 155, BrowserPlatform.Windows), "");
}
