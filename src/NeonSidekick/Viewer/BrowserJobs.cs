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

    /// <summary>
    /// Where the page's held button is down, null with none held (the Web UI's live view): in the view's points on a Mac, in CSS pixels on
    /// Windows.
    /// </summary>
    public (double X, double Y)? Held { get; set; }

    /// <summary>
    /// Its answer given (<see cref="TryEnd"/>): a callback still to come for it (a look, the print, the cookies) does nothing (2026-10-20, the
    /// code review; Windows' <c>SolveJob.Ended</c> until <see cref="BrowserCheckWatch"/> took both windows' watch).
    /// </summary>
    public bool Ended { get; private set; }

    /// <summary>The visible part as the last look read it (Windows' Web UI live view, 2026-10-17); null before any.</summary>
    public BrowserViewport? Viewport { get; set; }

    /// <summary>How many pixels a device-independent one came to in the last picture (Windows' live view: the PC's display scale).</summary>
    public double PixelsPerDip { get; set; } = 1;

    /// <summary>
    /// The check ends here: true the first time (it is <see cref="Ended"/> and <see cref="Finishing"/> from now), false after — each window's
    /// <c>Finish</c> carries out an end once (2026-10-20, the code review: the Mac's had no such guard, so a late cookie answer or a stale look
    /// could end a check twice, hide the window under the next one and unbalance its activity).
    /// </summary>
    public bool TryEnd()
    {
        if (Ended)
        {
            return false;
        }

        Ended = true;
        Finishing = true;
        return true;
    }
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
/// once. Not thread-safe: the window's thread only. Written so the PC's window could adopt it; a check's watch, from its first look to its
/// answer (<see cref="BrowserCheckWatch"/> over <see cref="BrowserCheck"/>), is both windows' since 2026-10-20.
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

    /// <summary>
    /// The two hosts a check's cookies are read for, both windows': the one asked and the one it ended on — the latter only on the same site
    /// (<see cref="BotCheck.SameSite"/>; 2026-10-20, the code review: a page that sent the window on to a site the user is signed in to had
    /// that session's cookies read, the tools refusing such an ending since, <see cref="BotCheck.LeftTheSite"/>). Pure.
    /// </summary>
    public static IReadOnlyList<string> CheckHosts(Uri asked, string final)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return BrowserPage.HostOf(final) is { } ended && BotCheck.SameSite(asked.IdnHost, ended) ? [asked.Host, ended] : [asked.Host, asked.Host];
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

/// <summary>
/// What a Browser window lends the shared watch of its bot check (<see cref="BrowserCheckWatch"/>, 2026-10-20, the code review's G8: the
/// look-and-decide sequence was a near copy in Windows' Win32 window and the Mac's). The window's thread only; every answer comes back on it.
/// </summary>
internal interface IBrowserCheckPage
{
    /// <summary>Whether <paramref name="check"/> is still the window's own (and the window lives): a late answer for another does nothing.</summary>
    bool Holds(BrowserCheck check);

    /// <summary>The page's address as the window shows it (the Mac's staged PDF its https address), or null.</summary>
    string? Source { get; }

    /// <summary>
    /// <paramref name="expression"/> evaluated in the page, its value as JSON to <paramref name="answer"/> (null for an error or nothing to
    /// evaluate in); false, and no answer ever, when nothing could be sent.
    /// </summary>
    bool Evaluate(string expression, Action<string?> answer);

    /// <summary>The page printed to <paramref name="path"/>, <paramref name="written"/> told whether a PDF is there; false when no print started.</summary>
    bool PrintToPdf(string path, Action<bool> written);

    /// <summary>The window's cookies for <paramref name="hosts"/> to <paramref name="answer"/>; false when none could be asked for.</summary>
    bool ReadCookies(IReadOnlyList<string> hosts, Action<IReadOnlyList<BrowserCookie>> answer);

    /// <summary>The browser the cookies were earned as.</summary>
    BrowserIdentity Identity { get; }

    /// <summary>The check's end carried out by the window (its timers, the hide, the linger): once per check (<see cref="BrowserCheck.TryEnd"/>).</summary>
    void Finish(BrowserCheck check, BotCheckResult result, bool close);
}

/// <summary>
/// A bot check's watch in the Browser window, both windows' (2026-10-20, the code review's G8 and G1, the user's call): a look every poll at the
/// page's fingerprint (<see cref="HeadlessBrowser.PrintExpression"/>), its DOM when <see cref="BrowserWatch.WantsRead"/> says, read once more
/// when ready and changed since; once <see cref="BrowserWatch.Decide"/> says through, the address it ended on, the PDF when one was asked for,
/// then the cookies for its hosts (<see cref="BrowserJobs.CheckHosts"/>), then the answer. A check that is <see cref="BrowserCheck.Finishing"/>
/// takes no more looks: a look still in flight as it began to finish — a download ended it as a file, the user gave it up — is dropped when
/// it answers, where before (on both windows) it could conclude the check again as refused, or complete it a second time. Pure but for the
/// page's callbacks; the window's thread only.
/// </summary>
internal static class BrowserCheckWatch
{
    /// <summary>One look at the page while <paramref name="check"/> waits; nothing while one is on its way, the check is finishing, or no page loaded yet.</summary>
    public static void Tick(IBrowserCheckPage page, BrowserCheck check)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(check);
        if (check.Busy || check.Finishing || !check.Watch.Loaded || !page.Holds(check))
        {
            return;
        }

        check.Busy = true;
        if (!page.Evaluate(HeadlessBrowser.PrintExpression, json => OnPrint(page, check, json)))
        {
            check.Busy = false;
        }
    }

    /// <summary>
    /// A file instead of a page during <paramref name="check"/> (a download, an answer that is no HTML): it ends as a file now, its cookies what
    /// the caller wants; false when it is finishing anyway.
    /// </summary>
    public static bool EndAsFile(IBrowserCheckPage page, BrowserCheck check)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(check);
        if (check.Finishing)
        {
            return false;
        }

        check.WasFile = true;
        check.Busy = true;
        Complete(page, check);
        return true;
    }

    // A look's answer, or a read's, for a check no longer the window's or already finishing: dropped.
    private static bool Stale(IBrowserCheckPage page, BrowserCheck check) => check.Finishing || !page.Holds(check);

    internal static void OnPrint(IBrowserCheckPage page, BrowserCheck check, string? json)
    {
        if (Stale(page, check))
        {
            return;
        }

        if (!BrowserAnswers.TryReadPrint(json, out string state, out var print))
        {
            check.Busy = false;   // mid-navigation: the next look tries again
            return;
        }

        if (check.Watch.WantsRead(check.Clock.Elapsed, print))
        {
            ReadDom(page, check, () => AfterRead(page, check, state, print));
        }
        else
        {
            AfterRead(page, check, state, print);
        }
    }

    private static void AfterRead(IBrowserCheckPage page, BrowserCheck check, string state, (int, uint) print)
    {
        bool ready = check.Watch.Observe(check.Clock.Elapsed, state, print);
        if (ready && check.Watch.Stale(print))
        {
            ReadDom(page, check, () => Conclude(page, check));   // ready: the DOM as it is now, when the last read is older than the last change
        }
        else if (ready)
        {
            Conclude(page, check);
        }
        else
        {
            check.Busy = false;
        }
    }

    private static void ReadDom(IBrowserCheckPage page, BrowserCheck check, Action then)
    {
        bool sent = page.Evaluate(HeadlessBrowser.PageExpression, json =>
        {
            if (Stale(page, check))
            {
                return;
            }

            if (BrowserAnswers.TryReadPage(json, out string html))
            {
                if (html.Length > WebFetcher.MaxDownloadBytes)
                {
                    html = html[..WebFetcher.MaxDownloadBytes];
                    check.Truncated = true;
                }

                var url = Uri.TryCreate(page.Source, UriKind.Absolute, out var at) && WebFetcher.IsHttp(at) ? at : check.Request.Url;
                check.Watch.Read(check.Clock.Elapsed, html, url);
            }

            then();
        });
        if (!sent)
        {
            check.Busy = false;
        }
    }

    /// <summary>A ready page judged: through (<see cref="Complete"/>), refused where it ended, or another look.</summary>
    internal static void Conclude(IBrowserCheckPage page, BrowserCheck check)
    {
        if (Stale(page, check))
        {
            return;
        }

        switch (BrowserJobs.Outcome(check.Watch.Decide()))
        {
            case BotCheckEnd.Cleared:
                Complete(page, check);
                break;
            case BotCheckEnd.Refused:
                page.Finish(check, BrowserJobs.Refused(check, page.Source ?? check.Request.Url.AbsoluteUri), close: BrowserJobs.EndCloses(check));
                break;
            default:
                check.Busy = false;
                break;
        }
    }

    // Through: the PDF when one was asked for, then the cookies, then the answer. Once: a check finishing already is on its way.
    private static void Complete(IBrowserCheckPage page, BrowserCheck check)
    {
        if (check.Finishing)
        {
            return;
        }

        check.Finishing = true;
        string final = BrowserJobs.FinalUrl(page.Source, check.Request.Url);
        if (check.Request.PdfPath is { } path && !check.WasFile && page.PrintToPdf(path, written =>
        {
            if (check.Ended)
            {
                return;
            }

            check.PdfWritten = written;
            Harvest(page, check, final);
        }))
        {
            return;
        }

        Harvest(page, check, final);
    }

    // The cookies for the host asked for and, on the same site, the one it ended on (BrowserJobs.CheckHosts): never another site's.
    private static void Harvest(IBrowserCheckPage page, BrowserCheck check, string final)
    {
        if (check.Ended)
        {
            return;
        }

        var hosts = BrowserJobs.CheckHosts(check.Request.Url, final);
        void Done(IReadOnlyList<BrowserCookie> cookies)
        {
            if (!check.Ended)
            {
                page.Finish(check, BrowserJobs.Cleared(check, final, cookies, page.Identity), close: BrowserJobs.EndCloses(check));
            }
        }

        if (!page.ReadCookies(hosts, Done))
        {
            Done([]);
        }
    }
}

/// <summary>
/// Who acts on a posted close, the window's thread or the caller who stopped waiting for it (2026-10-20, the code review's E4: Ctrl+B's close
/// waited a second for Windows' window thread and, on a timeout, reported no window while the close stayed queued, so the home page the chord
/// then opened met a window closing itself). Exactly one side takes it. Thread-safe.
/// </summary>
public sealed class BrowserCloseClaim
{
    private int _state;

    /// <summary>The window's thread, as the posted work runs: true when it acts (the caller has not given up on it).</summary>
    public bool TakeForWork() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    /// <summary>The caller, out of patience: true when the work had not begun and now never will (nothing is closing).</summary>
    public bool TakeForCaller() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;
}
