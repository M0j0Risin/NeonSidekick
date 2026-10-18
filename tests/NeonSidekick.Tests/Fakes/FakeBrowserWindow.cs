using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The Browser window without WebView2 (2026-10-13): every bot check and <c>/browser</c> visit recorded; a check answers
/// <see cref="Answer"/> at once (cleared with <see cref="Html"/> by default), or with <see cref="Hold"/> waits until its token is
/// cancelled and ends as given up, noting the cancel; a visit answers <see cref="Visit"/>; a clear (later on 2026-10-13) is recorded and
/// answers <see cref="ClearAnswer"/>; every <see cref="Close"/> is counted. The saved links (later still): <see cref="Current"/> is the page
/// a bare <c>/browser save</c> reads (<see cref="CurrentHold"/>: it never answers), <see cref="LinksChangedCount"/> counts the pane's
/// changes, and with <see cref="HoldVisit"/> every visit stays open until <see cref="EndVisit"/>, all of them answered at once as the
/// window does. The Web UI's live view (2026-10-17): while a held check runs, <see cref="FrameAsync"/> answers <see cref="Frame"/> and
/// <see cref="InputAsync"/> records each input and answers true; with none running, null and false, as the window does. <see cref="EndHeld"/>
/// ends the held checks with an answer of the test's choosing.
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
        lock (_running)
        {
            _running.Add((request, held));
        }

        cancellationToken.Register(() =>
        {
            CancelSeen = true;
            held.TrySetResult(BotCheckResult.Ended(BotCheckEnd.GaveUp, request.Url.AbsoluteUri));
        });
        _ = held.Task.ContinueWith(_ =>
        {
            lock (_running)
            {
                _running.RemoveAll(r => r.Done == held);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return held.Task;
    }

    private readonly List<(BotCheckRequest Request, TaskCompletionSource<BotCheckResult> Done)> _running = [];

    /// <summary>Whether a held check is running now.</summary>
    public bool Running
    {
        get
        {
            lock (_running)
            {
                return _running.Count > 0;
            }
        }
    }

    /// <summary>The held checks ended: each answered <paramref name="answer"/> (cleared with <see cref="Html"/> when null), as the window's page getting through.</summary>
    public void EndHeld(Func<BotCheckRequest, BotCheckResult>? answer = null)
    {
        (BotCheckRequest Request, TaskCompletionSource<BotCheckResult> Done)[] running;
        lock (_running)
        {
            running = [.. _running];
        }

        foreach (var (request, done) in running)
        {
            done.TrySetResult(answer?.Invoke(request) ?? Cleared(request, Html));
        }
    }

    /// <summary>The picture a running check answers (a tiny JPEG by default).</summary>
    public BrowserFrame? Frame { get; set; } = new(TinyJpeg, BrowserRemote.TagOf(TinyJpeg));

    /// <summary>How many pictures were asked for while a check ran.</summary>
    public int FrameAsks => Volatile.Read(ref _frameAsks);

    private int _frameAsks;

    /// <summary>Every input sent while a check ran, in order.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<BrowserInput> Inputs { get; } = new();

    public Task<BrowserFrame?> FrameAsync(int maxWidth, CancellationToken cancellationToken)
    {
        if (!Running)
        {
            return Task.FromResult<BrowserFrame?>(null);
        }

        Interlocked.Increment(ref _frameAsks);
        return Task.FromResult(Frame);
    }

    public Task<bool> InputAsync(BrowserInput input, CancellationToken cancellationToken)
    {
        if (!Running)
        {
            return Task.FromResult(false);
        }

        Inputs.Enqueue(input);
        return Task.FromResult(true);
    }

    /// <summary>A 1×1 baseline JPEG a browser draws.</summary>
    public static byte[] TinyJpeg { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==");

    public bool HoldVisit { get; set; }

    private TaskCompletionSource<BrowserVisit>? _held;

    public Task<BrowserVisit> BrowseAsync(Uri? url)
    {
        Browses.Add(url);
        if (!HoldVisit)
        {
            return Task.FromResult(Visit);
        }

        _held ??= new TaskCompletionSource<BrowserVisit>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _held.Task;
    }

    /// <summary>The held visit closed: every open of it answered <see cref="Visit"/>.</summary>
    public void EndVisit()
    {
        var held = _held;
        _held = null;
        held?.TrySetResult(Visit);
    }

    /// <summary>The page the window shows for a bare <c>/browser save</c>; null: none open.</summary>
    public (string Url, string? Title)? Current { get; set; }

    /// <summary>The page is asked for and never answered (a closing window), so the screen's own wait ends it.</summary>
    public bool CurrentHold { get; set; }

    /// <summary>How many times the page was asked for.</summary>
    public int CurrentAsks => Volatile.Read(ref _currentAsks);

    private int _currentAsks;

    public Task<(string Url, string? Title)?> CurrentPageAsync()
    {
        Interlocked.Increment(ref _currentAsks);
        return CurrentHold ? new TaskCompletionSource<(string Url, string? Title)?>().Task : Task.FromResult(Current);
    }

    public int LinksChangedCount { get; private set; }

    public void LinksChanged() => LinksChangedCount++;

    /// <summary>What <see cref="Unavailable"/> answers (later still on 2026-10-13): null (a page can be opened) by default, else why not.</summary>
    public string? UnavailableReason { get; set; }

    public string? Unavailable() => UnavailableReason;

    public List<BrowserClear> Clears { get; } = new();

    /// <summary>The profile folders <see cref="ForgetProfileAsync"/> was asked to forget (2026-10-18, <c>/profile delete</c>), and whether each folder was still there then.</summary>
    public List<(string Directory, bool Existed)> Forgotten { get; } = new();

    public Task ForgetProfileAsync(string profileDirectory)
    {
        Forgotten.Add((profileDirectory, Directory.Exists(profileDirectory)));
        return Task.CompletedTask;
    }

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
