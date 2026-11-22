using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The 🗂️ sheet's Sessions tab (2026-11-16, plan lucid-roaming-lantern #40, the user's ask: resume a past conversation from the phone) ──
// The stored sessions listed and searched on a Kestrel thread (the store is lock-guarded; the screen swaps it on a profile switch, so each
// handler reads the field once), and a resume queued as the page's chord /sessions <id>, run at the idle line under the page's origin, where
// WebDispatchRefusal checks it again. Nothing here prints. No PC path reaches the page: titles lose their saved paths, models show as labels.

internal sealed partial class ChatScreen
{
    /// <summary>How many sessions the tab lists, newest first, and how many title matches a filter returns.</summary>
    public const int WebSessionRows = 50;

    /// <summary>How many full-text hits a search adds.</summary>
    public const int WebSessionHits = 20;

    /// <summary>The query length from which the tab's ask also searches what was said: shorter would match half of every session.</summary>
    public const int WebSessionSearchFrom = 3;

    /// <summary>
    /// A session as the page sees it: its title without saved paths, its model as the header's label (each of a botchat's), so a folder's name
    /// never shows nor matches.
    /// </summary>
    private static SessionSummary ForThePage(SessionSummary session) => session with
    {
        Title = PasteBlocks.WithoutSavedPaths(session.Title),
        Model = string.Join(", ", session.Model.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => ModelLabel(m))),
    };

    /// <summary>Whether a code audit, a gap review or a Ralph loop runs (any thread; read unlocked, so the chord's own check is the one that holds).</summary>
    private bool WebLoopRunning => _ralph is not null || _audit.Active || _plan.Closing is not null;

    /// <summary>
    /// The session store and the profile it belongs to, set together after a profile switch rebuilds the store (2026-11-16, the review's #60: the
    /// profile's name changes first, so a page reading the two apart could get the new name with the old store's rows). Read once per request.
    /// </summary>
    private sealed record SessionsFor(SessionStore Store, string Profile);

    private volatile SessionsFor? _webSessions;

    /// <summary>The pair now: set at the screen's start, before the Web UI can ask.</summary>
    private SessionsFor WebSessionsPair => _webSessions ?? new SessionsFor(_sessions, _settings.ProfileName);

    /// <summary>
    /// Every session as the page sees it, for a filter, kept while the store's <see cref="SessionStore.Version"/> holds (2026-11-16, the review's
    /// #15: a filter's keystrokes read the whole list once, not once each).
    /// </summary>
    private sealed record PageSessions(SessionStore Store, long Version, IReadOnlyList<SessionSummary> Sessions);

    private volatile PageSessions? _pageSessions;

    private IReadOnlyList<SessionSummary> PageSessionsOf(SessionStore store)
    {
        long version = store.Version;
        if (_pageSessions is { } held && ReferenceEquals(held.Store, store) && held.Version == version)
        {
            return held.Sessions;
        }

        var sessions = store.List(0).Select(ForThePage).ToList();
        _pageSessions = new PageSessions(store, version, sessions);
        return sessions;
    }

    /// <summary>The Sessions tab's tag (2026-11-16, the review's #78): the store's version and the session on screen, so an open tab reads again.</summary>
    private string WebSessionsTagNow() =>
        WebSessionsPair.Store.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
        + (_sessionId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none");

    /// <summary>The Sessions tab's view for <paramref name="query"/> (any thread, reads only).</summary>
    private WebSessionsView WebSessionsNow(string? query)
    {
        var (store, profile) = WebSessionsPair;
        long? current = _sessionId;
        string q = (query ?? "").Trim();
        var zone = _time.LocalTimeZone;
        string root = _files.Root;   // read once: a flag for the page, never the path (2026-11-21, feature idea #4)
        WebSessionRow Row(SessionSummary s) => new(s.Id, SessionText.DisplayTitle(s), SessionText.Moment(s.UpdatedAt, zone), SessionText.Turns(s.Turns), s.Model, s.Id == current,
            Here: Files.FolderMatch.Same(s.Cwd, root));

        var rows = q.Length == 0
            ? store.List(WebSessionRows).Select(ForThePage).Select(Row).ToList()
            : PageSessionsOf(store).Where(s => SessionsMenu.Matches(q, s)).Take(WebSessionRows).Select(Row).ToList();
        var found = new List<WebSessionRow>();
        if (q.Length >= WebSessionSearchFrom)
        {
            // A column counts only when a match survives the cleaning (the review's #8): one inside a saved path never stands for the session.
            static bool Shown(string marked) => WebSnippet.Parts(marked).Any(p => p.Match);
            foreach (var hit in store.Search(q, WebSessionHits, exclude: current, marks: (WebSnippet.Open, WebSnippet.Close), accept: Shown))
            {
                var snippet = WebSnippet.Parts(hit.Snippet);
                if (!snippet.Any(p => p.Match))
                {
                    continue;   // it matched only inside a saved path: listing it would tell the page what the folder held
                }

                int at = rows.FindIndex(r => r.Id == hit.Session.Id);
                if (at >= 0)
                {
                    rows[at] = rows[at] with { Snippet = snippet };   // one row per session: the title match carries it
                }
                else
                {
                    found.Add(Row(ForThePage(hit.Session)) with { Snippet = snippet });
                }
            }
        }

        var verdict = WebCommands.Check(SlashCommand.Session, "1", "/sessions 1", WebCommandSettingsNow());
        return new WebSessionsView(profile, rows, found, verdict.Allowed, verdict.Allowed ? null : verdict.Reason, !_effective().SessionLogging);
    }

    /// <summary>
    /// The Sessions tab's resume (a Kestrel thread): checked in the plan's order (the profile the tab was read from, the PC busy, a loop, the
    /// current session, a missing one, the page's command rule, another page chord waiting), then queued as the page's chord
    /// <c>/sessions &lt;id&gt;</c> with the profile, which the idle line runs as typed at the PC once it is free.
    /// </summary>
    private WebReply WebResumeSession(WebSessionResume request, IPAddress? remote)
    {
        var (store, profile) = WebSessionsPair;
        if (!Profiles.NameEquals(request.Profile ?? "", profile))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.ProfileSwitched);
        }

        var (working, loading) = ScopesNow();
        if (working || loading || _botChatRunning || _webDispatching)
        {
            return new WebReply(WebOutcome.Conflict, SettingsMenu.NotWhileReplyRunsNotice);
        }

        if (WebLoopRunning)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.ResumeNotUnderLoop);
        }

        if (_sessionId == request.Id)
        {
            return new WebReply(WebOutcome.Conflict, SessionsMenu.CurrentNotice);
        }

        if (store.Summary(request.Id) is null)
        {
            return new WebReply(WebOutcome.Conflict, SessionMissingError(request.Id));
        }

        string line = "/sessions " + request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var verdict = WebCommands.Check(SlashCommand.Session, request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), line, WebCommandSettingsNow());
        if (!verdict.Allowed)
        {
            return new WebReply(WebOutcome.Refused, verdict.Reason);
        }

        return QueueWebChord(line, remote, page: null, speak: null, profile, new WebReply(WebOutcome.Conflict, WebUiText.ChordWaiting),
            WebUiText.SessionResumeLogLine(request.Id, remote), WebUiText.SessionResumedFlash(request.Id));
    }

    /// <summary>
    /// The chord's own check as it runs at the idle line (the restore form from the page): the profile it was queued under, the current
    /// session, a loop, each read on the screen's task now.
    /// </summary>
    private string? WebSessionRefusal(string args)
    {
        if (ParseSessionArgs(args) is not { Kind: SessionActionKind.Restore } restore)
        {
            return null;
        }

        if (_sentWebProfile is { } profile && !Profiles.NameEquals(profile, _settings.ProfileName))
        {
            return WebUiText.ProfileSwitched;
        }

        if (_sessionId == restore.Id)
        {
            return SessionsMenu.CurrentNotice;
        }

        return WebLoopRunning ? WebUiText.ResumeNotUnderLoop : null;
    }
}
