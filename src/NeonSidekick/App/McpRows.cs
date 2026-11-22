using NeonSidekick.Mcp;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the <c>/mcp</c> pane shows (2026-09-20): the session's rows and problems, the two switches, the per-tool list and the two files. Re-read after every act.</summary>
/// <param name="Servers">Every configured server as the session last saw it.</param>
/// <param name="Problems">What the last read of the two files could not use.</param>
/// <param name="Enabled">The setting <c>MCP servers</c> (the master switch).</param>
/// <param name="ToolsEnabled">The setting <c>LLM offer tools</c>.</param>
/// <param name="Disabled">The tools switched off by name (<c>ToolsDisabled</c>, the prefixed names among them).</param>
/// <param name="ProfilePath">The profile's <c>mcp.json</c>.</param>
/// <param name="GlobalPath">The home's <c>mcp.json</c>.</param>
public sealed record McpFacts(IReadOnlyList<McpServerStatus> Servers, IReadOnlyList<McpConfigProblem> Problems, bool Enabled, bool ToolsEnabled, IReadOnlySet<string> Disabled, string ProfilePath, string GlobalPath)
{
    /// <summary>
    /// Whether <i>Allow for this session</i> holds for a tool, by its prefixed name (2026-11-20, the mcp-ask review's #25): the Tools tab's
    /// footer says so. Null reads as none, as the tests' facts have it. The <c>asks</c> tag stays: the rule applies again after <c>/new</c>.
    /// </summary>
    public Func<string, bool>? Allowed { get; init; }

    /// <summary>
    /// The connected servers that declare resources (2026-11-21, feature idea #33): while there's one, the Tools tab lists the built-in
    /// <c>mcp_read</c> first, its footer naming them. Empty, as the tests' facts have it, leaves the row out.
    /// </summary>
    public IReadOnlyList<string> ResourceServers { get; init; } = [];
}

/// <summary>What a row of the Servers tab does on Enter: a server's flip, one of the two edit rows, the reload row.</summary>
public abstract record McpRow
{
    public sealed record Server(string Name) : McpRow
    {
        /// <summary>
        /// Which file's entry the row is (2026-11-20, plan idempotent-mixing-ripple): a shadowed global row and the profile row that shadows it
        /// share a name, and <c>a</c> writes the ask into one file. Null where a test builds a row by name alone: the first of that name.
        /// </summary>
        public McpScope? Scope { get; init; }
    }

    public sealed record EditProfile : McpRow;
    public sealed record EditGlobal : McpRow;
    public sealed record Reload : McpRow;

    /// <summary>
    /// A skipped server whose <c>ask</c> is a typo (2026-11-20, the user's call): its line under Skipped takes <c>a</c>, which writes a mode over
    /// the bad value in <paramref name="Path"/>; Enter and Space do nothing there, as on every other skipped line.
    /// </summary>
    public sealed record BadAsk(string Name, string Path) : McpRow;
}

/// <summary>
/// The <c>/mcp</c> pane's rows (2026-09-20), pure over <see cref="McpFacts"/> — the <c>ToolsText</c> shape:
/// the Servers tab (<see cref="ServerRows"/>) and the Tools tab (<see cref="ToolRows"/>) as markup with
/// what each row means beside it, and the same as plain lines for a console without the pane.
/// </summary>
public static class McpRows
{
    /// <summary>The widest a name column grows (the prefixed names are long).</summary>
    public const int MaxNameWidth = 40;

    /// <summary>The narrowest a name column is, so a short list still reads in columns.</summary>
    public const int MinNameWidth = 12;

    public const int StateWidth = ToolsText.StateWidth;

    /// <summary>The name column of the rows in <paramref name="names"/>: the widest plus two, between <see cref="MinNameWidth"/> and <see cref="MaxNameWidth"/>.</summary>
    public static int NameWidth(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        int widest = names.Select(n => n.Length).DefaultIfEmpty(0).Max() + 2;
        return Math.Clamp(widest, MinNameWidth, MaxNameWidth);
    }

    /// <summary>The dim words after a server's name and state.</summary>
    public static string StatusText(McpServerStatus server)
    {
        ArgumentNullException.ThrowIfNull(server);
        string scope = server.Scope == McpScope.Global ? " " + McpText.GlobalMark : "";
        if (server.ShadowedBy is not null)
        {
            return McpText.StatusShadowed + scope;
        }

        string status = server.State switch
        {
            McpState.Connected when server.Dropped is not null => McpText.StatusDropped,   // 2026-11-19: its tools still offered, back at the next call
            McpState.Connected => McpText.StatusConnected(server.Tools.Count),
            McpState.Connecting when server.SigningIn => McpText.StatusSigningIn,   // 2026-11-21, feature idea #32
            McpState.Connecting => McpText.StatusConnecting,
            McpState.Failed when server.NeedsSignIn => McpText.StatusNeedsSignIn,
            McpState.Failed => McpText.StatusFailed(server.Detail ?? ""),
            _ => McpText.StatusOff,
        };
        return status + scope + "  " + server.Transport;
    }

    /// <summary>
    /// The Servers tab: the <c>LLM offer tools</c> line dim first while that is off, <see cref="McpText.OffLine"/>
    /// dim while the master switch is off, one row per server (the name in the label colour, <c>on</c> / <c>off</c>,
    /// the status dim; a shadowed row dim whole), <see cref="McpText.NoServersLine"/> with none, then the two edit
    /// rows and — while the master switch is on — the reload row (off, nothing would start and a switch on re-reads
    /// both files anyway; later on 2026-09-20, the user's ask), then the problems dim. The row's meaning beside it, null
    /// on a line that does nothing. Since 2026-10-10 (the user's ask: the <c>/settings</c> look) the three runs sit under
    /// <see cref="SectionRule"/>s — <see cref="McpText.ServersHeading"/> with the server count, <see cref="McpText.FilesHeading"/>,
    /// <see cref="McpText.ProblemsHeading"/> with the problem count — an empty row before every one but the first (the
    /// <c>/tools</c> Offered shape), the problems no longer indented; <c>Heading</c> true beside a heading alone.
    /// </summary>
    public static IReadOnlyList<(string Markup, McpRow? Row, bool Heading)> ServerRows(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<(string, McpRow?, bool)>(facts.Servers.Count + 12);
        if (!facts.ToolsEnabled)
        {
            rows.Add((Theme.DimMarkup(ToolsText.OffLine), null, false));
        }

        if (!facts.Enabled)
        {
            rows.Add((Theme.DimMarkup(McpText.OffLine), null, false));
        }

        Heading(McpText.ServersHeading, facts.Servers.Count);
        int width = NameWidth(facts.Servers.Select(s => s.Name));
        if (facts.Servers.Count == 0)
        {
            rows.Add((Theme.DimMarkup(McpText.NoServersLine), null, false));
        }

        foreach (var server in facts.Servers)
        {
            string state = ToolsText.State(server.Enabled).PadRight(StateWidth);
            bool live = facts.ToolsEnabled && facts.Enabled && server.Startable;
            string row = live
                ? Styled(Theme.AccentSecondary, server.Name.PadRight(width)) + Theme.ColorMarkup(Theme.Ink, state) + Theme.DimMarkup(StatusText(server))
                : Theme.DimMarkup(server.Name.PadRight(width) + state + StatusText(server));
            rows.Add((row, new McpRow.Server(server.Name) { Scope = server.Scope }, false));
        }

        Heading(McpText.FilesHeading, null);
        rows.Add((Theme.DimMarkup(McpText.EditProfileRow), new McpRow.EditProfile(), false));
        rows.Add((Theme.DimMarkup(McpText.EditGlobalRow), new McpRow.EditGlobal(), false));
        if (facts.Enabled)
        {
            rows.Add((Theme.DimMarkup(McpText.ReloadRow), new McpRow.Reload(), false));
        }

        if (facts.Problems.Count > 0)
        {
            Heading(McpText.ProblemsHeading, facts.Problems.Count);
            foreach (var problem in facts.Problems)
            {
                // A typo in a server's ask (2026-11-20, the user's call) is the one problem a key mends: the line carries its server and file.
                McpRow? mend = problem is { Name: { } name, Path: { } path } && string.Equals(problem.Reason, McpText.BadAsk, StringComparison.Ordinal) ? new McpRow.BadAsk(name, path) : null;
                rows.Add((Theme.DimMarkup(McpText.ProblemLine(problem)), mend, false));
            }
        }

        return rows;

        void Heading(string title, int? count)
        {
            if (rows.Any(r => r.Item3))
            {
                rows.Add(("", null, false));
            }

            rows.Add((SectionRule.Markup(title, count?.ToString(System.Globalization.CultureInfo.InvariantCulture)), null, true));
        }
    }

    /// <summary>The indices of the Servers tab's heading rows (2026-10-10): the pane's <see cref="MenuTab.Headings"/>.</summary>
    public static IReadOnlySet<int> HeadingRows(IReadOnlyList<(string Markup, McpRow? Row, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Enumerable.Range(0, rows.Count).Where(i => rows[i].Heading).ToHashSet();
    }

    /// <summary>
    /// The server a Servers-tab row names (2026-11-20, plan idempotent-mixing-ripple): by name, and by scope when the row carries one, so a
    /// shadowed global row and the profile row that shadows it each find their own. Null when the session no longer holds it.
    /// </summary>
    public static McpServerStatus? ServerOf(McpFacts facts, McpRow.Server row)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(row);
        return facts.Servers.FirstOrDefault(s => s.Name == row.Name && (row.Scope is null || s.Scope == row.Scope));
    }

    /// <summary>
    /// The footer under the Servers tab for <paramref name="row"/> (2026-10-07, the user's ask: a failed server's error was the row's
    /// cut end, and the tab's band stood blank): a server's whole status — the error whole, or the tools it offers by name — then its
    /// scope and transport (<c>profile · stdio: docker mcp gateway run</c>), shadowed said so; an edit row the file it opens; null for
    /// the reload row and a line that does nothing. Pinned.
    /// </summary>
    public static MenuFooter? ServerFooter(McpFacts facts, McpRow? row)
    {
        ArgumentNullException.ThrowIfNull(facts);
        switch (row)
        {
            case McpRow.EditProfile:
                return new MenuFooter(facts.ProfilePath);
            case McpRow.EditGlobal:
                return new MenuFooter(facts.GlobalPath);
            case McpRow.BadAsk:
                return new MenuFooter(McpText.BadAskFixFooter);
            case McpRow.Server named when ServerOf(facts, named) is { } server:
            {
                string status = server.ShadowedBy is not null ? McpText.StatusShadowed
                    : server.State switch
                    {
                        McpState.Connected when server.Dropped is not null => McpText.StatusDropped,
                        McpState.Connected => McpText.StatusConnected(server.Tools.Count) + (server.Tools.Count > 0 ? ": " + string.Join(", ", server.Tools.Select(t => t.Name)) : ""),
                        McpState.Connecting when server.SigningIn => McpText.StatusSigningIn,
                        McpState.Connecting => McpText.StatusConnecting,
                        // A sign-in that failed or was cancelled says why after the state (2026-11-21).
                        McpState.Failed when server.NeedsSignIn => McpText.StatusNeedsSignIn
                            + (server.Detail is { } why && why != McpText.NeedsSignInDetail ? " (" + why + ")" : ""),
                        McpState.Failed => McpText.StatusFailed(server.Detail ?? ""),
                        _ => McpText.StatusOff,
                    };
                string scope = server.Scope == McpScope.Global ? "global" : "profile";
                // A connected server's ask and notes first (2026-11-19, feature ideas #28 and #59), where a long transport can't push them off the cut line.
                string words = server.State == McpState.Connected && server.ShadowedBy is null ? ConnectedWords(server) : "";
                return new MenuFooter(status, words + scope + " · " + server.Transport);
            }

            default:
                return null;
        }
    }

    /// <summary>A connected server's footer words, each with its <c> · </c>: <c>asks before writes · notes 812 chars · </c>.</summary>
    private static string ConnectedWords(McpServerStatus server)
    {
        string notes = McpText.NotesFooter(server.NotesLength, server.NotesRaw, server.NotesLeftOut, server.NotesOn);
        return McpText.AskFooter(server.Ask) + " · " + (notes.Length > 0 ? notes + " · " : "") + (server.SignedIn ? McpText.SignedInFooter + " · " : "");
    }

    /// <summary>
    /// The width of the Tools tab's ask column (2026-11-19): <see cref="McpText.AsksTag"/> and its gap, measured (2026-11-20, the mcp-ask
    /// review's #73), so the tag can't outgrow it; a string's length is no constant.
    /// </summary>
    public static readonly int AskWidth = McpText.AsksTag.Length + 2;

    /// <summary>The Tools tab's ask column for <paramref name="tool"/> of <paramref name="server"/>: <see cref="McpText.AsksTag"/> or blank, padded.</summary>
    public static string AskColumn(McpServerStatus server, McpTool tool) => (McpAskRule.Asks(server.Ask, tool) ? McpText.AsksTag : "").PadRight(AskWidth);

    /// <summary>
    /// The Tools tab: a heading per connected server (<c>── docker · 14 ───…</c>, <c>── docker · 12 of 14 ───…</c>, a
    /// <see cref="SectionRule"/> with an empty row before every one but the first since 2026-10-03, the <c>/tools</c> look) in the
    /// section colour whatever the switches say (the <c>/tools</c> rule, later on 2026-09-20), then a row per tool in the <c>/tools</c> Offered shape — the prefixed name, <c>on</c> /
    /// <c>off</c>, the description dim — the whole row dim while the turn would not offer it;
    /// <see cref="McpText.NoToolsLine"/> with no server connected. The tool's prefixed name beside every tool row. Under a
    /// <paramref name="filter"/> (2026-10-03, <see cref="MenuFilter"/>) the <c>/tools</c> Offered shape: only the tools whose
    /// name or description holds it, a server with none left out, <see cref="MenuFilter.NoMatchRow"/> alone when none is left.
    /// </summary>
    /// <remarks>
    /// <paramref name="only"/> (2026-10-10, the Tools tab's sub-tabs, <see cref="ToolTabs"/>) keeps that connected server's section
    /// alone, its heading and count as on the whole list; null is every server's. The name column stays the whole list's either way.
    /// </remarks>
    public static IReadOnlyList<(string Markup, string? Tool, bool Heading)> ToolRows(McpFacts facts, string filter = "", string? only = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(filter);
        var rows = new List<(string, string?, bool)>(32);
        var all = facts.Servers.Where(s => s.State == McpState.Connected).ToList();
        var connected = only is null ? all : all.Where(s => string.Equals(s.Name, only, StringComparison.Ordinal)).ToList();
        if (!facts.ToolsEnabled)
        {
            rows.Add((Theme.DimMarkup(ToolsText.OffLine), null, false));
        }

        if (!facts.Enabled)
        {
            rows.Add((Theme.DimMarkup(McpText.OffLine), null, false));
        }

        if (connected.Count == 0)
        {
            rows.Add((Theme.DimMarkup(McpText.NoToolsLine), null, false));
            return rows;
        }

        bool offered = facts.ToolsEnabled && facts.Enabled;
        bool builtIn = only is null && facts.ResourceServers.Count > 0;
        IEnumerable<string> names = all.SelectMany(s => s.Tools).Select(t => t.Name);
        int width = NameWidth(facts.ResourceServers.Count > 0 ? names.Append(McpReadTool.ToolName) : names);
        int shownServers = 0;
        // The built-in mcp_read first (2026-11-21, feature idea #33), on the whole list alone: it reads every resource server's.
        if (builtIn && MenuFilter.Matches(filter, McpReadTool.ToolName, McpText.ReadRowDescription))
        {
            bool on = !facts.Disabled.Contains(McpReadTool.ToolName);
            shownServers++;
            rows.Add((SectionRule.Markup(McpText.BuiltInSection, SystemPromptSummary.GroupCount(on ? 1 : 0, 1)), null, true));
            string ask = "".PadRight(AskWidth);
            string row = offered && on
                ? Styled(Theme.AccentSecondary, McpReadTool.ToolName.PadRight(width)) + Theme.ColorMarkup(Theme.Ink, ToolsText.State(on).PadRight(StateWidth)) + ask + Theme.DimMarkup(McpText.ReadRowDescription)
                : Theme.DimMarkup(McpReadTool.ToolName.PadRight(width) + ToolsText.State(on).PadRight(StateWidth) + ask + McpText.ReadRowDescription);
            rows.Add((row, McpReadTool.ToolName, false));
        }

        foreach (var server in connected)
        {
            // A tool that asks also matches its tag (2026-11-20, the mcp-ask review's #114): typing "asks" keeps the tools the column marks.
            var tools = server.Tools.Where(t => MenuFilter.Matches(filter, t.Name, t.Description)
                || (McpAskRule.Asks(server.Ask, t) && MenuFilter.Matches(filter, McpText.AsksTag, null))).ToList();
            if (tools.Count == 0 && filter.Length > 0)
            {
                continue;
            }

            if (shownServers++ > 0)
            {
                rows.Add(("", null, false));
            }

            int left = server.Tools.Count(t => !facts.Disabled.Contains(t.Name));
            rows.Add((SectionRule.Markup(server.Name, SystemPromptSummary.GroupCount(left, server.Tools.Count)), null, true));
            foreach (var tool in tools)
            {
                bool on = !facts.Disabled.Contains(tool.Name);
                string ask = AskColumn(server, tool);
                string row = offered && on
                    ? Styled(Theme.AccentSecondary, tool.Name.PadRight(width)) + Theme.ColorMarkup(Theme.Ink, ToolsText.State(on).PadRight(StateWidth)) + Theme.ColorMarkup(Theme.Ink, ask) + Theme.DimMarkup(tool.Description)
                    : Theme.DimMarkup(tool.Name.PadRight(width) + ToolsText.State(on).PadRight(StateWidth) + ask + tool.Description);
                rows.Add((row, tool.Name, false));
            }
        }

        if (shownServers == 0)
        {
            rows.Add((MenuFilter.NoMatchRow(filter), null, false));
        }

        return rows;
    }

    /// <summary>The Tools tab's first sub-tab (2026-10-10, the user's pick): every connected server's tools, the list as it was. Pinned.</summary>
    public const string AllSubTitle = "all";

    /// <summary>The widest a server's sub-tab title grows, in characters, its end cut by an ellipsis (a server's name is any JSON key). Pinned.</summary>
    public const int MaxSubTitleLength = 24;

    /// <summary>
    /// The Tools tab's sub-tabs (2026-10-10, the user's pick: one per connected server, the Skills pane's scopes' shape): null for
    /// <see cref="AllSubTitle"/>, then each connected server's name in the session's order; none while fewer than two are connected, the
    /// tab then a plain one with no second strip (a strip of one server says nothing the list does not).
    /// </summary>
    public static IReadOnlyList<string?> ToolTabs(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var names = facts.Servers.Where(s => s.State == McpState.Connected).Select(s => s.Name).ToList();
        return names.Count < 2 ? [] : [null, .. names];
    }

    /// <summary>
    /// A Tools sub-tab's title (2026-10-10): <see cref="AllSubTitle"/> for null, else the server's name with any control character a space
    /// and cut to <see cref="MaxSubTitleLength"/> with an ellipsis. Pure.
    /// </summary>
    public static string ToolTabTitle(string? server)
    {
        if (server is null)
        {
            return AllSubTitle;
        }

        string name = string.Concat(server.Select(c => char.IsControl(c) ? ' ' : c));
        return name.Length > MaxSubTitleLength ? name[..(MaxSubTitleLength - 1)] + "…" : name;
    }

    /// <summary>
    /// What the Tools tab says under the list for the cursor's tool (2026-10-05, <see cref="ToolsText.ToolFooter"/>'s twin): its
    /// whole description, word-wrapped. Null for a name no connected server holds. Pure.
    /// </summary>
    public static MenuFooter? ToolFooter(McpFacts facts, string tool)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(tool);
        if (string.Equals(tool, McpReadTool.ToolName, StringComparison.Ordinal) && facts.ResourceServers.Count > 0)
        {
            return new MenuFooter(McpText.ReadRowDescription, McpText.ReadRowFooter(facts.ResourceServers));
        }

        // The connected rows only (#119's FindTool, each caller its rows): the tab lists no other.
        if (McpServerStatus.FindTool(facts.Servers.Where(s => s.State == McpState.Connected), tool) is not var (server, found))
        {
            return null;
        }

        // Whether it asks (2026-11-19, feature idea #28), and why not when it doesn't; held for this session (2026-11-20, #25).
        string asks = !McpAskRule.Asks(server.Ask, found) ? (server.Ask == McpAskMode.Never ? McpText.ToolNeverFooter : McpText.ToolReadOnlyFooter)
            : facts.Allowed?.Invoke(found.Name) == true ? McpText.ToolAllowedFooter
            : McpText.ToolAsksFooter;
        return new MenuFooter(found.Description, asks);
    }

    /// <summary>The first server row (the cursor's opening place); 0 when there is none.</summary>
    public static int FirstServerRow(IReadOnlyList<(string Markup, McpRow? Row, bool Heading)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Row is McpRow.Server)
            {
                return i;
            }
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Row is not null)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The Servers tab as plain lines: the same words, one per row, without the action rows.</summary>
    public static IEnumerable<string> ServerLines(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.ToolsEnabled)
        {
            yield return ToolsText.OffLine;
        }

        if (!facts.Enabled)
        {
            yield return McpText.OffLine;
        }

        if (facts.Servers.Count == 0)
        {
            yield return McpText.NoServersLine;
        }

        int width = NameWidth(facts.Servers.Select(s => s.Name));
        foreach (var server in facts.Servers)
        {
            yield return server.Name.PadRight(width) + ToolsText.State(server.Enabled).PadRight(StateWidth) + StatusText(server);
        }

        if (facts.Problems.Count > 0)
        {
            yield return McpText.SkippedHeading;
            foreach (var problem in facts.Problems)
            {
                yield return "  " + McpText.ProblemLine(problem);
            }
        }
    }

    /// <summary>The Tools tab as plain lines: each connected server's heading, then <c>name  on/off  description</c>.</summary>
    public static IEnumerable<string> ToolLines(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var connected = facts.Servers.Where(s => s.State == McpState.Connected).ToList();
        if (connected.Count == 0)
        {
            yield return McpText.NoToolsLine;
            yield break;
        }

        IEnumerable<string> names = connected.SelectMany(s => s.Tools).Select(t => t.Name);
        int width = NameWidth(facts.ResourceServers.Count > 0 ? names.Append(McpReadTool.ToolName) : names);
        // The built-in mcp_read first, as the pane lists it (2026-11-22, the review's #90).
        if (facts.ResourceServers.Count > 0)
        {
            bool on = !facts.Disabled.Contains(McpReadTool.ToolName);
            yield return SystemPromptSummary.GroupName(McpText.BuiltInSection, on ? 1 : 0, 1);
            yield return "  " + McpReadTool.ToolName.PadRight(width) + ToolsText.State(on).PadRight(StateWidth) + "".PadRight(AskWidth) + McpText.ReadRowDescription;
        }

        foreach (var server in connected)
        {
            int left = server.Tools.Count(t => !facts.Disabled.Contains(t.Name));
            yield return SystemPromptSummary.GroupName(server.Name, left, server.Tools.Count);
            foreach (var tool in server.Tools)
            {
                yield return "  " + tool.Name.PadRight(width) + ToolsText.State(!facts.Disabled.Contains(tool.Name)).PadRight(StateWidth) + AskColumn(server, tool) + tool.Description;
            }
        }
    }

    private static string Styled(Style style, string text) => $"[{style.ToMarkup()}]{Markup.Escape(text)}[/]";
}
