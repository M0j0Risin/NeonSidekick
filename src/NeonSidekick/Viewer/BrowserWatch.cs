using NeonSidekick.Web;

namespace NeonSidekick.Viewer;

/// <summary>What the Browser window does after a look at its page (<see cref="BrowserWatch.Decide"/>).</summary>
public enum BrowserWatchStep
{
    /// <summary>Not yet: the page is loading, changing, or still a bot check.</summary>
    Wait,

    /// <summary>Through: the page has loaded and settled with no check showing; read it and hand it back.</summary>
    Read,

    /// <summary>Loaded and settled with no check showing, but with an error status (or no page at all): no person can solve that.</summary>
    Refused,
}

/// <summary>
/// When the Browser window's page is through its bot check (2026-10-13, <see cref="BotCheck"/>): the headless browser's watch
/// (<c>HeadlessBrowser.WatchPageAsync</c>) made pure for a window whose looks arrive as script answers. Each look is the page's
/// ready state and its DOM's fingerprint (<see cref="HeadlessBrowser.PrintExpression"/>); the DOM itself is read
/// (<see cref="WantsRead"/>, then <see cref="Read"/>) when it differs from the one last read and that read is
/// <see cref="DomSettle.Quiet"/> old, and once more when the page is ready and changed since (<see cref="Stale"/>). Unlike the
/// headless browser's it never takes a check's page as it is (<see cref="DomSettle.ChallengeLimit"/> infinite): a person is solving
/// it, and the caller's clock (<see cref="BotCheck.SolveTimeout"/>) ends the wait. The navigation's own outcome comes from
/// <see cref="Navigated"/>. Pure: time is the caller's.
///
/// <para>A navigation under way is waited for (2026-10-26, the user's report, session 795: a Cloudflare check passed on
/// thewoksoflife.com, and the tool still said the window "got HTTP 403 too, with no check to solve"). The status comes only with a
/// navigation's completion, which waits for the page's pictures and scripts; the recipe page that followed the check was
/// <c>interactive</c> and still long before then, so it was taken under the check's own 403 and refused. So the window tells the watch
/// when a navigation begins (<see cref="Began"/>), and until it completes (<see cref="Navigated"/>, or <see cref="LoadEnded"/> for one
/// that ended on no web page) the page is not taken, for at most <see cref="MostLoading"/>; past that its status is unknown and a page
/// with no check is read. Navigations can overlap (WebView2's docs): one superseded by the next completes too, failed, after the next
/// began, so Windows passes each navigation's id and a completion for another is ignored. The Mac has no ids; there the cap is the
/// backstop.</para>
/// </summary>
public sealed class BrowserWatch
{
    private readonly DomSettle _settle = new() { ChallengeLimit = Timeout.InfiniteTimeSpan };
    private (int, uint)? _last;
    private (int, uint)? _readPrint;
    private TimeSpan _readAt;
    private bool _thin;
    private TimeSpan? _loadingSince;
    private ulong? _loadingId;

    /// <summary>The longest a navigation under way (<see cref="Began"/>) is waited for before its page is judged with no status.</summary>
    public static readonly TimeSpan MostLoading = TimeSpan.FromSeconds(15);

    /// <summary>The DOM last read; null before the first read.</summary>
    public string? Html { get; private set; }

    /// <summary>Whether the DOM last read is a bot check's (<see cref="BotCheck.StillGated"/>).</summary>
    public bool Gated { get; private set; }

    /// <summary>Whether a check was seen at any read: the window had something for the person to do.</summary>
    public bool ChallengeSeen { get; private set; }

    /// <summary>The last top-level navigation's HTTP status (0: none yet, or none given).</summary>
    public int Status { get; private set; }

    /// <summary>Whether a top-level navigation has completed: until then the blank page before it is no answer.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Whether the last navigation failed without an answer (no name, no connection, cancelled).</summary>
    public bool LoadFailed { get; private set; }

    /// <summary>
    /// A top-level navigation to a web page began at <paramref name="now"/>, its <paramref name="id"/> when the window has one: its page
    /// is not taken until it completes. A new id starts the wait again; the same one (a redirect) keeps it, as does no id (the Mac).
    /// </summary>
    public void Began(TimeSpan now, ulong? id = null)
    {
        if (_loadingSince is null || (id is not null && id != _loadingId))
        {
            _loadingSince = now;
            _loadingId = id;
        }
    }

    /// <summary>
    /// A top-level navigation completed: <paramref name="ok"/> without a network error, its <paramref name="status"/>. One whose
    /// <paramref name="id"/> is not the navigation under way's was superseded by it, and is ignored; with no id on either side it counts.
    /// </summary>
    public void Navigated(bool ok, int status, ulong? id = null)
    {
        if (_loadingId is { } pending && id is { } done && pending != done)
        {
            return;
        }

        _loadingSince = null;
        _loadingId = null;
        Loaded = true;
        LoadFailed = !ok && status == 0;
        Status = status;
    }

    /// <summary>
    /// A navigation ended on no web page (the window's blank start page, say), which <see cref="Navigated"/> is never told: the wait for
    /// it ends, when it is the navigation under way (<paramref name="id"/>) or with no id.
    /// </summary>
    public void LoadEnded(ulong? id = null)
    {
        if (id is null || _loadingId is null || id == _loadingId)
        {
            _loadingSince = null;
            _loadingId = null;
        }
    }

    /// <summary>Whether a look with <paramref name="print"/> at <paramref name="now"/> should read the DOM first.</summary>
    public bool WantsRead(TimeSpan now, (int, uint) print) =>
        print != _readPrint && (_readPrint is null || now - _readAt >= DomSettle.Quiet);

    /// <summary>Whether the DOM last read is older than <paramref name="print"/>: a ready page is read once more before it is taken.</summary>
    public bool Stale((int, uint) print) => print != _readPrint;

    /// <summary>The DOM read at <paramref name="now"/> on <paramref name="url"/>, judged: a check, a thin page.</summary>
    public void Read(TimeSpan now, string html, Uri url)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(url);
        Html = html;
        _readPrint = HeadlessBrowser.Fingerprint(html);
        _readAt = now;
        var page = HtmlToMarkdown.Convert(html, url);
        Gated = BotCheck.StillGated(html, page.Markdown);
        ChallengeSeen |= Gated;
        _thin = DomSettle.LooksThin(html, page.Markdown);
    }

    /// <summary>
    /// One look at <paramref name="now"/>: the page's ready <paramref name="state"/> and its DOM's <paramref name="print"/>, after any
    /// read it wanted. True when the page is ready to be taken (<see cref="Decide"/> then says how). A navigation under way holds it
    /// unloaded, its changes still counted, until it completes or <see cref="MostLoading"/> has passed: then its status is unknown (0).
    /// </summary>
    public bool Observe(TimeSpan now, string state, (int, uint) print)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed = print != _last;
        _last = print;
        bool loading = false;
        if (_loadingSince is { } since)
        {
            if (now - since < MostLoading)
            {
                loading = true;
            }
            else
            {
                _loadingSince = null;
                _loadingId = null;
                Loaded = true;
                LoadFailed = false;
                Status = 0;
            }
        }

        return Loaded && _settle.Observe(now, !loading && (state is "interactive" or "complete"), changed, Gated, _thin);
    }

    /// <summary>What a ready page is: <see cref="BrowserWatchStep.Read"/>, or <see cref="BrowserWatchStep.Refused"/> for an error status or no page; <see cref="BrowserWatchStep.Wait"/> while it is a check or unread.</summary>
    public BrowserWatchStep Decide()
    {
        if (Html is null || Gated)
        {
            return BrowserWatchStep.Wait;
        }

        return LoadFailed || Status >= 400 ? BrowserWatchStep.Refused : BrowserWatchStep.Read;
    }
}
