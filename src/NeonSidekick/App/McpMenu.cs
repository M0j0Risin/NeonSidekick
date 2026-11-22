using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/mcp</c> screen (2026-09-20, the user's ask): a tabbed <see cref="MenuPane"/> in the
/// <see cref="ToolsMenu"/> shape. The Servers tab lists every server the two <c>mcp.json</c> files name
/// with <c>on</c> / <c>off</c> and its state (<see cref="McpRows.ServerRows"/>); Enter or Space on a server
/// flips it — the name into or out of <c>McpServersDisabled</c> — and connects or disconnects it right
/// there, the outcome on the status line; Enter on a failed server that is on retries it, and on a dropped one (2026-11-19) reconnects it; the three
/// rows after the servers open the profile's or the home's file in the editor (made with the empty
/// shape first when missing) or re-read both files and reconcile. While the master switch is off
/// (later on 2026-09-20, the user's ask) a server row answers <see cref="McpText.OffNotice"/> and
/// changes nothing, the reload row is left out and the two edit rows work. The Tools tab lists every connected
/// server's tools (<see cref="McpRows.ToolRows"/>) and flips one by its prefixed name into
/// <c>ToolsDisabled</c>, the <c>/tools</c> list, read at the next turn; with two servers or more connected (2026-10-10, the user's pick)
/// it holds a sub-tab per server after <c>all</c> on the second strip (<see cref="McpRows.ToolTabs"/>), the one filter typed running on
/// each and the one on show kept by name across the rebuilds. The Options tab is
/// <see cref="SettingsMenu.McpTabFields"/> through the settings menu's seams under this pane's strip,
/// its pickers titled <c>MCP › …</c>. Mid-turn the tool flips and the edit rows work; a server flip, a
/// retry, a reload and the Options rows that reconnect are refused (a call could be in flight, the
/// history's tools change). The result says whether the screen owes a reconnect
/// (<see cref="SettingsChanges.Mcp"/>: the master switch saved on the Options tab). Without the pane
/// the three tabs print as plain lines. Bare only: <c>/mcp x</c> is the no-argument error.
/// </summary>
internal sealed class McpMenu
{
    private readonly Func<McpFacts> _facts;
    private readonly McpSession _session;
    private readonly AppSettings _settings;
    private readonly SettingsMenu _menu;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Action<string> _openFile;
    private readonly Func<AppSettingsData> _effective;

    /// <summary>The Options tab's index into <see cref="McpText.TabTitles"/>.</summary>
    public const int OptionsTab = 2;

    /// <summary>The hidden button behind the Servers tab's <c>a</c> (never drawn, so never seen).</summary>
    private const string AskButtonTitle = "ask";

    /// <summary>The hidden button behind the Servers tab's <c>s</c> (2026-11-21, feature idea #32): sign in, or out.</summary>
    private const string SignInButtonTitle = "sign in";

    /// <summary>The index of <see cref="SignInButtonTitle"/> among the page's buttons.</summary>
    private const int SignInButton = 1;

    // A requested sign-in (2026-11-21): it runs behind the pane, which stays open and live; Esc calls it off, a second Esc closes.
    private readonly object _signInGate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, bool Error, bool Transcript)> _signInNotices = new();
    private Task? _signInTask;
    private CancellationTokenSource? _signInCancel;
    private CancellationTokenSource? _wake;
    private bool _detached;

    /// <summary>Whether the MCP wave still runs behind the input line (2026-09-29): the rows that take the session's gate are refused meanwhile.</summary>
    public Func<bool> Connecting { get; init; } = static () => false;

    public McpMenu(Func<McpFacts> facts, McpSession session, AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane, Action<string> openFile, Func<AppSettingsData> effective)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    /// <summary>
    /// The tabbed page: the Servers rows, the Tools rows, the Options fields; Space a flip on the first two. The Tools tab
    /// filters (2026-10-03, <see cref="MenuFilter"/>, <c>/tools</c>' Offered shape): <paramref name="tools"/> are the rows under
    /// <paramref name="filter"/>, <paramref name="total"/> the tools there are, for the caption while a filter is typed.
    /// </summary>
    /// <remarks>
    /// <paramref name="toolFooter"/> (2026-10-05) says under the list what the Tools tab's cursor tool does, whole (<see cref="McpRows.ToolFooter"/>).
    /// <paramref name="byServer"/> (2026-10-10, the user's pick) makes the Tools tab hold a sub-tab per entry on the second strip
    /// (<see cref="McpRows.ToolTabs"/>: all, then each connected server) — its title, its rows under the same filter and the tools it
    /// holds for the caption — <paramref name="sub"/> the one on show; null or empty leaves the tab plain over <paramref name="tools"/>.
    /// </remarks>
    public static MenuPage Page(IReadOnlyList<(string Markup, McpRow? Row, bool Heading)> servers, IReadOnlyList<(string Markup, string? Tool, bool Heading)> tools, AppSettingsData saved, SettingsMenu menu, int tab, string filter = "", int total = 0, Func<string, MenuFooter?>? toolFooter = null, Func<McpRow?, MenuFooter?>? serverFooter = null,
        IReadOnlyList<(string Title, IReadOnlyList<(string Markup, string? Tool, bool Heading)> Rows, int Total)>? byServer = null, int sub = 0)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(filter);
        var tabs = new MenuTab[]
        {
            new(McpText.ServersTabTitle, servers.Select(r => r.Markup).ToList()) { Hint = McpText.ServersKeys, Headings = McpRows.HeadingRows(servers) },
            byServer is { Count: > 0 }
                ? new MenuTab(McpText.ToolsTabTitle, [])
                {
                    SubTabs = byServer.Select(s => ToolsTab(s.Title, s.Rows, s.Total)).ToList(),
                    Sub = Math.Clamp(sub, 0, byServer.Count - 1),
                }
                : ToolsTab(McpText.ToolsTabTitle, tools, total),
            menu.FieldsTab(McpText.OptionsTabTitle, SettingsMenu.McpTabFields[0], saved, sections: SettingsMenu.McpMenuSections[0]),
        };
        return MenuPage.Tabbed(McpText.Label, tabs, tab, SettingsMenu.TabKeys) with
        {
            SpaceToggles = true,
            // A server's ask on Servers alone (2026-11-20, plan idempotent-mixing-ripple): tied to tab 0, an `a` on the Tools tab still filters,
            // and no TabButtons, whose strip would drop the Options tab's badge.
            Buttons = [new MenuButton(AskButtonTitle, 'a') { Hidden = true, Tab = 0 }, new MenuButton(SignInButtonTitle, 's') { Hidden = true, Tab = 0 }],
            TabCursors = [McpRows.FirstServerRow(servers), ToolsText.FirstToolRow(tools), 0],
            // The Tools tab's band says the cursor's tool (2026-10-05, /tools' Offered shape); the Servers tab's the cursor's server whole (2026-10-07).
            Footer = (t, row) => t switch
            {
                0 => row < servers.Count ? serverFooter?.Invoke(servers[row].Row) : null,
                1 => row < tools.Count && tools[row].Tool is { } name ? toolFooter?.Invoke(name) : null,
                2 => OptionsField(row) is { } field ? menu.FieldFooter(field, saved) : null,
                _ => null,
            },
        };

        // The Tools rows (or one server's): the filter's hint and caption, the headings, the band saying the cursor's tool.
        MenuTab ToolsTab(string title, IReadOnlyList<(string Markup, string? Tool, bool Heading)> rows, int held) => new(title, rows.Select(r => r.Markup).ToList())
        {
            Hint = MenuFilter.Hint(ToolsText.OfferedKeys, filter),
            Headings = ToolsText.HeadingRows(rows),
            Filter = filter,
            Caption = MenuFilter.CaptionOrNull(filter, ToolsText.ToolCount(rows), held),
            Footer = row => row >= 0 && row < rows.Count && rows[row].Tool is { } name ? toolFooter?.Invoke(name) : null,
        };
    }

    /// <summary>The three tabs as plain lines, for a console without the pane.</summary>
    public static IEnumerable<string> Lines(McpFacts facts, AppSettingsData saved, SettingsMenu menu)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        yield return McpText.ServersTabTitle;
        foreach (var line in McpRows.ServerLines(facts))
        {
            yield return "  " + line;
        }

        yield return McpText.ToolsTabTitle;
        foreach (var line in McpRows.ToolLines(facts))
        {
            yield return "  " + line;
        }

        yield return McpText.OptionsTabTitle;
        foreach (var field in SettingsMenu.McpTabFields[0])
        {
            yield return "  " + menu.PlainRow(field, saved);
        }
    }

    /// <param name="midTurn">The pane opened while a reply runs: the tool flips and the edit rows work, the rest is refused.</param>
    /// <returns><see cref="SettingsChanges.Mcp"/> when the master switch was saved on the Options tab (the screen reconnects), else <see cref="SettingsChanges.None"/>.</returns>
    public Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, bool midTurn = false) => ShowAsync(cancellationToken, ReplyRun.Of(midTurn));

    /// <summary>The pane under a reply asked live (2026-10-12, <see cref="ReplyRun"/>): what it refused opens when the reply ends, the pane saying so.</summary>
    public async Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, ReplyRun reply)
    {
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(_facts(), _settings.Current, _menu))
            {
                _transcript.Notice(line);
            }

            return SettingsChanges.None;
        }

        var changes = SettingsChanges.None;
        int tab = 0;
        int cursor = -1;
        string filter = "";
        string? shownServer = null;   // the Tools tab's server on show (2026-10-10), by name so a reconnect that reorders keeps it; null = all
        _menu.Root = McpText.Label;
        lock (_signInGate)
        {
            _detached = false;
        }

        try
        {
            while (true)
            {
                FlushSignInNotices();
                var facts = _facts();
                var saved = _settings.Current;
                var servers = McpRows.ServerRows(facts);
                var toolTabs = McpRows.ToolTabs(facts);
                int sub = Math.Max(0, IndexOf(toolTabs, shownServer));
                var connected = facts.Servers.Where(s => s.State == McpState.Connected).ToList();
                var byServer = toolTabs.Select(s => (McpRows.ToolTabTitle(s), McpRows.ToolRows(facts, filter, s), connected.Where(c => s is null || c.Name == s).Sum(c => c.Tools.Count))).ToList();
                var tools = byServer.Count > 0 ? byServer[sub].Item2 : McpRows.ToolRows(facts, filter);
                var page = Page(servers, tools, saved, _menu, tab, filter, connected.Sum(s => s.Tools.Count), name => McpRows.ToolFooter(facts, name), row => McpRows.ServerFooter(facts, row), byServer, sub);
                if (cursor < 0)
                {
                    cursor = tab == 1 ? ToolsText.FirstToolRow(tools) : McpRows.FirstServerRow(servers);
                }

                bool replyRan = reply.Running;
                using var wake = Wake(reply.Stale);
                var picked = await _pane.PickAsync(page, cursor, cancellationToken, stale: wake?.Token ?? reply.Stale).ConfigureAwait(false);
                if (picked is null && SignInWaiting && !cancellationToken.IsCancellationRequested)
                {
                    // Esc while a sign-in waits (2026-11-21): it is called off, the pane stays; a second Esc closes it.
                    CancelSignIn();
                    await WaitSignInAsync().ConfigureAwait(false);
                    continue;
                }

                if (picked is not { } pick)
                {
                    return changes;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                if (pick.Refresh)
                {
                    if (!replyRan || reply.Running)
                    {
                        continue;   // a sign-in's news (2026-11-21), not the reply's end
                    }

                    Sink.Notice(SettingsMenu.ReplyEndedNotice);   // the reply ended under the pane (2026-10-12): every row open now
                    shownServer = _pane.ShownSubs is { Count: > 1 } refreshed && byServer.Count > 0 ? toolTabs[Math.Clamp(refreshed[1], 0, byServer.Count - 1)] : shownServer;
                    continue;
                }

                // ←/→ move the server with no pick, so it is read off the pane (ToolsMenu's shape), and the tools picked from are its.
                if (byServer.Count > 0 && _pane.ShownSubs is { Count: > 1 } shownSubs)
                {
                    sub = Math.Clamp(shownSubs[1], 0, byServer.Count - 1);
                    shownServer = toolTabs[sub];
                    tools = byServer[sub].Item2;
                }
                if (pick.Button >= 0)
                {
                    // The Servers tab's `a` (2026-11-20): first, or the tab's branch below would read it as Enter and flip the server.
                    if (tab == 0 && cursor >= 0 && cursor < servers.Count)
                    {
                        if (pick.Button == SignInButton)
                        {
                            await SignInOrOutAsync(servers[cursor].Row, facts, reply.Running, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await AskAsync(servers[cursor].Row, facts, reply.Running, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    continue;
                }

                if (pick.Filter is { } typed)
                {
                    // The Tools tab's filter (2026-10-03): the rows again under it, the cursor on the first tool left.
                    filter = typed;
                    cursor = -1;
                    continue;
                }

                if (tab == 0)
                {
                    if (cursor >= servers.Count || servers[cursor].Row is not { } row || row is McpRow.BadAsk)
                    {
                        continue;   // a heading, the off lines, a problem (a typo'd ask's too: only `a` mends it)
                    }

                    await ActAsync(row, facts, reply.Running, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (tab == 1)
                {
                    if (cursor >= tools.Count || tools[cursor].Tool is not { } tool)
                    {
                        continue;
                    }

                    bool on = facts.Disabled.Contains(tool);   // off now → on
                    _settings.Update(d => d.ToolsDisabled = ToolsText.Flip(d.ToolsDisabled, tool));
                    Sink.Notice(NoticeGlyphs.Mcp + ToolsText.FlippedNotice(tool, on));   // the plug ahead (2026-09-22); the Tools pane's own line goes without
                    continue;
                }

                if (OptionsField(cursor) is not { } field)
                {
                    continue;   // the heading (2026-10-10) or past the rows
                }

                if (reply.Running && SettingsMenu.RefusedMidTurn(field))
                {
                    Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                    continue;
                }

                if (pick.Toggle)
                {
                    // Space on an Options row (2026-10-04, as on /settings): a switch flips and saves, any other row does nothing.
                    if (await _menu.FlipAsync(field, cancellationToken).ConfigureAwait(false) && SettingsMenu.IsMcpField(field))
                    {
                        changes |= SettingsChanges.Mcp;
                    }

                    continue;
                }

                var shown = pick.Tab == page.Tab ? page : MenuPage.Tabbed(McpText.Label, page.Tabs!, pick.Tab, SettingsMenu.TabKeys);
                if (await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false) && SettingsMenu.IsMcpField(field))
                {
                    changes |= SettingsChanges.Mcp;
                }
            }
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            lock (_signInGate)
            {
                // A sign-in still waiting runs to its end; what it says goes to the transcript from here (2026-11-21).
                _detached = true;
            }

            FlushSignInNotices();
            _pane.Close();
        }
    }

    // ── Sign-in (2026-11-21, feature idea #32) ──────────────────────────────

    /// <summary>Whether a sign-in started from this pane still waits.</summary>
    private bool SignInWaiting
    {
        get
        {
            lock (_signInGate)
            {
                return _signInTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>The pick's stale signal: the reply's end, and the sign-in's news (its address, its end).</summary>
    private CancellationTokenSource? Wake(CancellationToken replyStale)
    {
        lock (_signInGate)
        {
            if (_signInTask is not { IsCompleted: false } && _signInNotices.IsEmpty)
            {
                _wake = null;
                return null;
            }

            _wake = replyStale.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(replyStale) : new CancellationTokenSource();
            return _wake;
        }
    }

    /// <summary>A line from the sign-in, from any thread: to the pane's loop while it is open, else the transcript through the session's queue.</summary>
    private void SignInSays(string text, bool error, bool transcript)
    {
        lock (_signInGate)
        {
            if (_detached)
            {
                _session.QueueNotice(text, error);
                return;
            }

            _signInNotices.Enqueue((text, error, transcript));
            try
            {
                _wake?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That pick is over; the next one reads the queue first.
            }
        }
    }

    /// <summary>The sign-in's lines on the pane's own thread: the address above the pane on the transcript, the outcome on the status line.</summary>
    private void FlushSignInNotices()
    {
        while (_signInNotices.TryDequeue(out var line))
        {
            INoticeSink sink = line.Transcript ? _transcript : Sink;
            if (line.Error)
            {
                sink.Error(line.Text);
            }
            else
            {
                sink.Notice(line.Text);
            }
        }
    }

    /// <summary>Calls off this pane's sign-in, whether or not it has reached the session yet.</summary>
    private void CancelSignIn()
    {
        lock (_signInGate)
        {
            try
            {
                _signInCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ended meanwhile.
            }
        }

        _session.CancelSignIn();
    }

    private async Task WaitSignInAsync()
    {
        Task? running;
        lock (_signInGate)
        {
            running = _signInTask;
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }

        FlushSignInNotices();
    }

    /// <summary>
    /// The Servers tab's <c>s</c>: on a needs-sign-in row a sign-in, on a signed-in server a sign-out after a yes; any other row says it
    /// uses no sign-in. Mid-turn, during the connect wave, with the master switch off, or while another sign-in waits, refused.
    /// </summary>
    private async Task SignInOrOutAsync(McpRow? row, McpFacts facts, bool midTurn, CancellationToken cancellationToken)
    {
        if (row is not McpRow.Server named || McpRows.ServerOf(facts, named) is not { } server)
        {
            return;
        }

        if (midTurn)
        {
            Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
            return;
        }

        if (Connecting())
        {
            Sink.Notice(BackgroundJobText.McpStillConnecting);
            return;
        }

        if (!facts.Enabled)
        {
            Sink.Notice(McpText.OffNotice);
            return;
        }

        if (SignInWaiting || _session.SigningIn is not null)
        {
            Sink.Notice(McpText.SignInBusy);
            return;
        }

        if (server.Startable && server.Enabled && server.State == McpState.Failed && server.NeedsSignIn)
        {
            StartSignIn(server.Name);
            return;
        }

        if (server.State == McpState.Connected && _session.HoldsSignIn(server.Name))
        {
            if (!await _menu.ConfirmAsync(McpText.SignOutQuestion(server.Name), cancellationToken, close: false).ConfigureAwait(false))
            {
                return;
            }

            if (await _session.SignOutAsync(server.Name).ConfigureAwait(false))
            {
                Sink.Notice(McpText.SignedOutNotice(server.Name));
            }

            return;
        }

        Sink.Notice(McpText.NoSignIn(server.Name));
    }

    /// <summary>Starts a sign-in behind the pane: its row reads signing in, its address goes on the transcript, its outcome on the status line.</summary>
    private void StartSignIn(string name)
    {
        var effective = _effective();
        lock (_signInGate)
        {
            var cancel = new CancellationTokenSource();
            _signInCancel = cancel;
            _session.SignInNotice = (text, error) => SignInSays(text, error, transcript: true);
            _signInTask = Task.Run(async () =>
            {
                McpServerStatus? outcome = null;
                try
                {
                    outcome = await _session.SignInAsync(name, effective, cancel.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    SignInSays(McpText.SignInFailed(name, LogText.Excerpt(ex.Message)), true, transcript: false);
                    return;
                }
                finally
                {
                    _session.SignInNotice = null;
                }

                switch (outcome)
                {
                    case null when cancel.IsCancellationRequested:
                        SignInSays(McpText.SignInCancelledNotice(name), false, transcript: false);
                        break;
                    case null:
                        break;
                    case { State: McpState.Connected }:
                        SignInSays(McpText.SignedInNotice(name, outcome.Tools.Count), false, transcript: false);
                        break;
                    case { Detail: McpText.SignInCancelledDetail }:
                        SignInSays(McpText.SignInCancelledNotice(name), false, transcript: false);
                        break;
                    default:
                        SignInSays(McpText.SignInFailed(name, outcome.Detail ?? ""), true, transcript: false);
                        break;
                }
            });
        }
    }

    /// <summary>The field on row <paramref name="row"/> of the Options tab, its heading counted (2026-10-10, <see cref="SettingsMenu.McpMenuSections"/>); null for the heading or past the rows.</summary>
    private static SettingsField? OptionsField(int row) => SettingsMenu.SectionedField(SettingsMenu.McpTabFields[0], SettingsMenu.McpMenuSections[0], row);

    private static int IndexOf(IReadOnlyList<string?> tabs, string? server)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            if (string.Equals(tabs[i], server, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One Servers-tab act: a flip (with its connect or disconnect), a retry, an edit row, the reload; under the master switch off every row but the two edit ones is the off notice.</summary>
    private async Task ActAsync(McpRow row, McpFacts facts, bool midTurn, CancellationToken cancellationToken)
    {
        switch (row)
        {
            case McpRow.EditProfile:
                Open(facts.ProfilePath, profile: true);
                return;
            case McpRow.EditGlobal:
                Open(facts.GlobalPath, profile: false);
                return;
        }

        if (!facts.Enabled)
        {
            // The master switch off: a server row (a retry included) and a stale reload row change nothing — the Options tab is the way in.
            Sink.Notice(McpText.OffNotice);
            return;
        }

        if (midTurn)
        {
            Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
            return;
        }

        if (Connecting())
        {
            // The wave still runs behind the line (2026-09-29): a reload, connect or disconnect would wait for all of it.
            Sink.Notice(BackgroundJobText.McpStillConnecting);
            return;
        }

        var effective = _effective();
        if (row is McpRow.Reload)
        {
            var (added, removed, kept) = await _session.ReloadAsync(effective, cancellationToken).ConfigureAwait(false);
            Sink.Notice(McpText.ReloadedNotice(added, removed, kept));
            return;
        }

        if (row is not McpRow.Server(var name))
        {
            return;
        }

        var server = facts.Servers.FirstOrDefault(s => s.Name == name);
        if (server is null || !server.Startable)
        {
            return;
        }

        if (server.Enabled && server.State == McpState.Failed && server.NeedsSignIn)
        {
            // Enter on a needs-sign-in row signs in (2026-11-21): a plain retry would land on needs sign-in again.
            await SignInOrOutAsync(row, facts, midTurn, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (server.Enabled && server.State == McpState.Failed)
        {
            // Enter on a failed server that is on: try again, nothing saved.
            await ConnectAsync(name, effective, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (server.Enabled && server.Dropped is not null)
        {
            // Enter on a dropped server (2026-11-19, feature idea #30): reconnect it from the config it was connected with, as its next call
            // would, nothing saved. Turning it off is what Enter does on a live row; a user who reads "dropped" doesn't mean that.
            await ReconnectAsync(name, cancellationToken).ConfigureAwait(false);
            return;
        }

        bool on = !server.Enabled;   // off now → on
        _settings.Update(d => d.McpServersDisabled = ToolsText.Flip(d.McpServersDisabled, name));
        Sink.Notice(McpText.ServerFlippedNotice(name, on));
        if (!on)
        {
            await _session.DisconnectServerAsync(name).ConfigureAwait(false);
            Sink.Notice(McpText.StoppedNotice(name));
            return;
        }

        await ConnectAsync(name, _effective(), cancellationToken).ConfigureAwait(false);
    }

    private async Task ConnectAsync(string name, AppSettingsData effective, CancellationToken cancellationToken)
    {
        Sink.Notice(McpText.ConnectingNotice(name));
        await _session.ConnectServerAsync(name, effective, cancellationToken).ConfigureAwait(false);
        var after = _session.Servers.FirstOrDefault(s => s.Name == name);
        if (after is { State: McpState.Connected })
        {
            Sink.Notice(McpText.ConnectedNotice(name, after.Tools.Count));
        }
        else
        {
            Sink.Error(McpText.FailedNotice(name, after?.Detail ?? ""));
        }
    }

    private async Task ReconnectAsync(string name, CancellationToken cancellationToken)
    {
        Sink.Notice(McpText.ConnectingNotice(name));
        var after = await _session.ReconnectAsync(name, cancellationToken).ConfigureAwait(false);
        if (after is { State: McpState.Connected })
        {
            Sink.Notice(McpText.ConnectedNotice(name, after.Tools.Count));
        }
        else
        {
            Sink.Error(McpText.FailedNotice(name, after?.Detail ?? ""));
        }
    }

    /// <summary>
    /// The Servers tab's <c>a</c> (2026-11-20, plan idempotent-mixing-ripple, the user's ask after writing <c>"ask": "always"</c> by hand for a
    /// hand check): a picker of the three modes, the cursor on the file's, and the one chosen written into that server's own <c>mcp.json</c>
    /// (<see cref="Sql.ConnectionsFileEdit.SetEntryString"/>, nothing else in the file touched), then the session's reload: a change of
    /// <c>ask</c> alone updates the kept connection in place, and its session allows lapse with it. A skipped server whose <c>ask</c> is a
    /// typo is mended the same way (the user's call), the reload bringing it back. A shadowed row, mid-turn and the connect wave are refused;
    /// with the master switch off, or a server not connected, it is only written. Logged, since it changes what asks.
    /// </summary>
    private async Task AskAsync(McpRow? row, McpFacts facts, bool midTurn, CancellationToken cancellationToken)
    {
        string name;
        string path;
        bool global;
        McpAskMode current;
        bool later;
        switch (row)
        {
            case McpRow.Server named when McpRows.ServerOf(facts, named) is { } server:
                if (server.ShadowedBy is not null)
                {
                    Sink.Notice(McpText.AskShadowedNotice(server.Name));
                    return;
                }

                name = server.Name;
                global = server.Scope == McpScope.Global;
                path = global ? facts.GlobalPath : facts.ProfilePath;
                current = _session.Read(seal: false).Entries.FirstOrDefault(e => e.Name == name && e.Scope == server.Scope)?.Config.AskMode ?? McpAskMode.Writes;
                later = !facts.Enabled || server.State != McpState.Connected;
                break;
            case McpRow.BadAsk bad:
                name = bad.Name;
                path = bad.Path;
                global = string.Equals(path, facts.GlobalPath, StringComparison.Ordinal);
                current = McpAskMode.Writes;   // the file's word means none of them: the default first
                later = !facts.Enabled;
                break;
            default:
                return;   // an edit row, the reload row, a heading, a plain skipped line
        }

        if (midTurn)
        {
            Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
            return;
        }

        if (Connecting())
        {
            Sink.Notice(BackgroundJobText.McpStillConnecting);
            return;
        }

        var rows = McpAskRule.Words
            .Select((word, i) => Theme.ColorMarkup(Theme.Ink, word.PadRight(AskWordWidth)) + Theme.DimMarkup(McpText.AskModeMeanings[i]))
            .ToList();
        var picker = new MenuPage(McpText.AskPickerTitle(name), rows, McpText.AskPickerKeys);
        if (await _pane.PickAsync(picker, (int)current, cancellationToken).ConfigureAwait(false) is not { } pick || pick.Row < 0 || pick.Row >= rows.Count)
        {
            return;   // Esc: back to Servers, nothing written
        }

        var mode = (McpAskMode)pick.Row;
        if (mode == current && row is McpRow.Server)
        {
            Sink.Notice(McpText.AskUnchangedNotice(name, mode));
            return;
        }

        if (Sql.ConnectionsFileEdit.SetEntryString(path, McpConfigFile.ServersKey, name, McpConfigFile.AskKey, McpAskRule.Words[(int)mode]) is { } error)
        {
            Sink.Error(McpText.AskFailedError(name, error));
            return;
        }

        DiagnosticLog.Info(McpConfigFile.Category, McpText.AskSetLogLine(name, mode, path));
        if (facts.Enabled)
        {
            // The reload applies it: in place for a kept connection (its ask is no connection shape), a mended server started. Other edits
            // made meanwhile come with it, so their count is said when there are any.
            var (added, removed, kept) = await _session.ReloadAsync(_effective(), cancellationToken).ConfigureAwait(false);
            if (added > 0 || removed > 0)
            {
                Sink.Notice(McpText.ReloadedNotice(added, removed, kept));
            }
        }

        Sink.Notice(McpText.AskSetNotice(name, mode, global, later));
    }

    /// <summary>The picker's word column: the longest mode word and three spaces.</summary>
    private static readonly int AskWordWidth = McpAskRule.Words.Max(w => w.Length) + 3;

    /// <summary>Opens one of the two files in the editor, made with the empty shape first when missing; an IO failure is an error on the status line.</summary>
    private void Open(string path, bool profile)
    {
        try
        {
            McpConfigFile.EnsureExists(path);
            _openFile(path);
            Sink.Notice(McpText.EditingNotice(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(McpText.EditFailedError(path, LogText.Excerpt(ex.Message)));
        }
    }
}
