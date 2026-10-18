using System.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>
/// One bot check the Browser window is on (<see cref="BrowserJobs"/>): the request, its answer to come, whether a <c>/browser</c> visit holds
/// the window (it then stays open after), the watch and its clock, and what the window found. The native window fills the rest.
/// </summary>
public sealed class BrowserCheck(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done, bool keepOpen)
{
    public BotCheckRequest Request { get; } = request;

    public TaskCompletionSource<BotCheckResult> Done { get; } = done;

    /// <summary>Whether a <c>/browser</c> visit held the window when the check began: it stays open when the check ends.</summary>
    public bool KeepOpen { get; } = keepOpen;

    public BrowserWatch Watch { get; } = new();

    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    /// <summary>A look at the page is on its way: the next tick waits for it.</summary>
    public bool Busy { get; set; }

    /// <summary>The check is through (its PDF, its cookies on their way): nothing more is asked of the page, and no input goes in.</summary>
    public bool Finishing { get; set; }

    public bool PdfWritten { get; set; }

    /// <summary>The address turned out to be a file (a download, or a page that is no HTML): its cookies are what the caller wants.</summary>
    public bool WasFile { get; set; }

    public bool Truncated { get; set; }

    /// <summary>Where the page's held button is down, in the view's points; null with none held (the Web UI's live view).</summary>
    public (double X, double Y)? Held { get; set; }
}

/// <summary>What the window does about a close (<see cref="BrowserJobs.Closing"/>).</summary>
public enum BrowserCloseStep
{
    /// <summary>Hide and read the visit's cookies first (<see cref="BrowserJobs.HarvestHosts"/>); the close comes again once they are in.</summary>
    Harvest,

    /// <summary>Close now: any check given up, any visit settled with what it has.</summary>
    Close,
}

/// <summary>
/// The Browser window's jobs, a bot check and a <c>/browser</c> visit, as rules (2026-10-18, the Mac Browser plan, pass 24): on Windows this
/// lifecycle lives inside the Win32 class, out of the tests' reach; the Mac's window carries out what this decides, and the suite tests it
/// against the Windows window's behaviour. A check over a visit keeps the window open when it ends; a check alone hides it and closes it
/// after <see cref="Linger"/> unless another comes; a newer check, or a visit, gives the older check up; the visit records the hosts it went to
/// (never a staged PDF's or a Web UI sign-in link's) and hands their cookies over at its close, the first <see cref="MaxVisitedHosts"/>,
/// once. Not thread-safe: the window's thread only. Written so the PC's window could adopt it.
/// </summary>
public sealed class BrowserJobs
{
    /// <summary>How long a check's window stays, hidden, after its check is through before it closes (Windows' <c>Linger</c>).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(3);

    /// <summary>How long a visit's close waits for its cookies before it closes without them.</summary>
    public static readonly TimeSpan HarvestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The most hosts a visit hands cookies over for.</summary>
    public const int MaxVisitedHosts = 50;

    private HashSet<string>? _visited;
    private readonly List<string> _visitOrder = [];
    private bool _harvested;

    /// <summary>The check the window is on, or null.</summary>
    public BrowserCheck? Check { get; private set; }

    /// <summary>Whether a <c>/browser</c> visit holds the window.</summary>
    public bool Visiting => _visited is not null;

    /// <summary>Whether the visit's cookies have been read (or given up on): a later close goes straight through.</summary>
    public bool Harvested => _harvested;

    /// <summary>The hosts the visit went to, in the order first seen, at most <see cref="MaxVisitedHosts"/>.</summary>
    public IReadOnlyList<string> HarvestHosts => _visitOrder.Take(MaxVisitedHosts).ToList();

    /// <summary>
    /// A check asked for: the new one (kept open after when a visit holds the window), and the older one to give up without closing, if any.
    /// </summary>
    public (BrowserCheck Check, BrowserCheck? GiveUp) BeginCheck(BotCheckRequest request, TaskCompletionSource<BotCheckResult> done)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(done);
        var older = Check;
        Check = new BrowserCheck(request, done, keepOpen: Visiting);
        return (Check, older);
    }

    /// <summary>A visit asked for (<c>/browser</c>): from now on the hosts are recorded; the check under way, if any, to give up without closing.</summary>
    public BrowserCheck? BeginVisit()
    {
        _visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Check;
    }

    /// <summary>
    /// <paramref name="check"/> ends: no longer the window's (when it was), and whether the window hides now and closes after <see cref="Linger"/>
    /// (<paramref name="close"/> asked and no visit held it).
    /// </summary>
    public bool EndCheck(BrowserCheck check, bool close)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (Check == check)
        {
            Check = null;
        }

        check.Finishing = true;
        return close;
    }

    /// <summary>Whether a check's cancellation closes the window as it ends: only one the window took alone.</summary>
    public static bool CancelCloses(BrowserCheck check) => !check.KeepOpen;

    /// <summary>Whether a check ending by itself (through, refused) hides the window: only one no visit held.</summary>
    public static bool EndCloses(BrowserCheck check) => !check.KeepOpen;

    /// <summary>The linger ran out: whether the window closes (no check came, no visit began meanwhile).</summary>
    public bool LingerCloses() => Check is null && !Visiting;

    /// <summary>The page's address changed: its host recorded for the visit, unless it is a staged PDF, a Web UI sign-in link or no web page.</summary>
    public void Navigated(string? source)
    {
        if (_visited is null || PdfView.IsOurs(source) || WebUi.WebUiAuth.IsSignInUrl(source) || BrowserPage.HostOf(source) is not { } host)
        {
            return;
        }

        if (_visited.Add(host))
        {
            _visitOrder.Add(host);
        }
    }

    /// <summary>
    /// The window asked to close: <see cref="BrowserCloseStep.Harvest"/> once, for a visit that went somewhere while the view lives and the
    /// close hands things over (<paramref name="handOver"/>: the user's close, not the app's exit or a profile switch); otherwise close.
    /// </summary>
    public BrowserCloseStep Closing(bool handOver, bool viewAlive)
    {
        if (_visited is null || _harvested)
        {
            return BrowserCloseStep.Close;
        }

        _harvested = true;
        return handOver && viewAlive && _visitOrder.Count > 0 ? BrowserCloseStep.Harvest : BrowserCloseStep.Close;
    }

    /// <summary>
    /// A download (or a page that is no HTML) during a check: true when the check ends as a file now (<see cref="BrowserCheck.WasFile"/>), its
    /// cookies what the caller wants; false when there is no check, or it is ending anyway. A <c>/browser</c> download is the window's own.
    /// </summary>
    public bool FileDuringCheck()
    {
        if (Check is not { Finishing: false } check)
        {
            return false;
        }

        check.WasFile = true;
        check.Busy = true;
        return true;
    }

    /// <summary>What a settled look decided about the check: through (<see cref="BotCheckEnd.Cleared"/>), refused, or nothing yet.</summary>
    public static BotCheckEnd? Outcome(BrowserWatchStep step) => step switch
    {
        BrowserWatchStep.Read => BotCheckEnd.Cleared,
        BrowserWatchStep.Refused => BotCheckEnd.Refused,
        _ => null,
    };

    /// <summary>The address a check ended on: the page's own when it is a web address, else the one asked for.</summary>
    public static string FinalUrl(string? source, Uri asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return Uri.TryCreate(source, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at) ? at.AbsoluteUri : asked.AbsoluteUri;
    }

    /// <summary>The two hosts a check's cookies are read for: the one asked and the one it ended on.</summary>
    public static IReadOnlyList<string> CheckHosts(Uri asked, string final)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return [asked.Host, BrowserPage.HostOf(final) ?? asked.Host];
    }

    /// <summary>
    /// The check's answer once through: the page (none for a file), the address, the status, the cookies for its hosts and the browser that earned them.
    /// </summary>
    public static BotCheckResult Cleared(BrowserCheck check, string final, IReadOnlyList<BrowserCookie> cookies, BrowserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(check);
        return new BotCheckResult(BotCheckEnd.Cleared, check.WasFile ? null : check.Watch.Html, final, check.Watch.Status, cookies, identity, "")
        {
            PdfWritten = check.PdfWritten,
            WasFile = check.WasFile,
            Truncated = check.Truncated,
        };
    }

    /// <summary>A check refused by its page (an error status, or no page at all), where it ended.</summary>
    public static BotCheckResult Refused(BrowserCheck check, string where)
    {
        ArgumentNullException.ThrowIfNull(check);
        return BotCheckResult.Ended(BotCheckEnd.Refused, where, check.Watch.LoadFailed ? BrowserText.DidNotLoad : "", check.Watch.Status);
    }

    /// <summary>The visit's answer at its close: the cookies read (none when they could not be) for the hosts it went to.</summary>
    public BrowserVisit Visit(IReadOnlyList<BrowserCookie> cookies, BrowserIdentity identity) => new(cookies, HarvestHosts, identity);
}
