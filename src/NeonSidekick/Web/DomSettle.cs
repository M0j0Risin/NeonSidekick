namespace NeonSidekick.Web;

/// <summary>
/// When a page the headless browser is watching in real time is ready to read (2026-10-11, replacing <c>--dump-dom</c> with
/// a five-second <c>--virtual-time-budget</c>): fed one look per <see cref="PollInterval"/>, it says done once the page has
/// loaded (<c>interactive</c> or <c>complete</c>) and its DOM has held still for <see cref="Quiet"/>, or at the latest
/// <see cref="MostAfterLoad"/> after it loaded (a ticker or a clock never holds still). A thin page — scripts and next to no
/// text (<see cref="LooksThin"/>), a shell still fetching what it will show — is waited on to that latest point rather than taken
/// at its first quiet moment. A bot check's page is never done while it
/// lasts — it counts real seconds and reloads into the page — up to <see cref="MostChallenge"/>, then it is taken as it is (and
/// the fetcher reports it blocked); a page that follows it starts its own count. Pure: time is the caller's.
/// </summary>
public sealed class DomSettle
{
    /// <summary>How often the page is looked at.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a loaded page's DOM must hold still to be read.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    /// <summary>The longest a loaded page is waited on, still changing or still thin: the old virtual budget, in real time.</summary>
    public static readonly TimeSpan MostAfterLoad = TimeSpan.FromSeconds(5);

    /// <summary>The longest a bot check is waited on to clear: one that runs by itself takes a few seconds; one wanting a click never clears.</summary>
    public static readonly TimeSpan MostChallenge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long this watch waits on a bot check before taking the page as it is: <see cref="MostChallenge"/> for the headless browser;
    /// <see cref="Timeout.InfiniteTimeSpan"/> for the Browser window (2026-10-13, <see cref="BotCheck"/>), where a person is solving it
    /// and the caller's own clock (<see cref="BotCheck.SolveTimeout"/>) ends the wait.
    /// </summary>
    public TimeSpan ChallengeLimit { get; init; } = MostChallenge;

    private TimeSpan? _loadedAt;
    private TimeSpan? _gatedAt;
    private TimeSpan _changedAt;

    /// <summary>
    /// One look at <paramref name="now"/> (time since the run began): whether the page has <paramref name="loaded"/>, its DOM
    /// <paramref name="changed"/> since the last look, it is a bot check's page (<paramref name="gated"/>) or a thin one
    /// (<paramref name="thin"/>, <see cref="LooksThin"/>). True when the page is ready to read.
    /// </summary>
    public bool Observe(TimeSpan now, bool loaded, bool changed, bool gated, bool thin)
    {
        if (changed)
        {
            _changedAt = now;
        }

        if (gated)
        {
            _loadedAt = null;
            _gatedAt ??= now;
            return ChallengeLimit != Timeout.InfiniteTimeSpan && now - _gatedAt.Value >= ChallengeLimit;
        }

        _gatedAt = null;
        if (!loaded)
        {
            _loadedAt = null;
            return false;
        }

        _loadedAt ??= now;
        if (now - _loadedAt.Value >= MostAfterLoad)
        {
            return true;
        }

        return !thin && now - _changedAt >= Quiet;
    }

    /// <summary>
    /// A page with scripts and under <see cref="WebFetcher.ShellTextChars"/> of readable text: wider than
    /// <see cref="WebFetcher.LooksLikeShell"/>, which wants a framework's mount point too — the fetcher's test decides whether to
    /// start the browser at all, this one only how long to wait. Pure; pinned.
    /// </summary>
    public static bool LooksThin(string html, string readable)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(readable);
        return readable.Length < WebFetcher.ShellTextChars && html.Contains("<script", StringComparison.OrdinalIgnoreCase);
    }
}
