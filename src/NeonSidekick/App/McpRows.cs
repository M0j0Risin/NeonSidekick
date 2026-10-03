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
public sealed record McpFacts(IReadOnlyList<McpServerStatus> Servers, IReadOnlyList<McpConfigProblem> Problems, bool Enabled, bool ToolsEnabled, IReadOnlySet<string> Disabled, string ProfilePath, string GlobalPath);

/// <summary>What a row of the Servers tab does on Enter: a server's flip, one of the two edit rows, the reload row.</summary>
public abstract record McpRow
{
    public sealed record Server(string Name) : McpRow;
    public sealed record EditProfile : McpRow;
    public sealed record EditGlobal : McpRow;
    public sealed record Reload : McpRow;
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
            McpState.Connected => McpText.StatusConnected(server.Tools.Count),
            McpState.Connecting => McpText.StatusConnecting,
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
    /// both files anyway; later on 2026-09-20, the user's ask), then <c>Skipped:</c> and the problems dim. The row's
    /// meaning beside it, null on a line that does nothing.
    /// </summary>
    public static IReadOnlyList<(string Markup, McpRow? Row)> ServerRows(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<(string, McpRow?)>(facts.Servers.Count + 8);
        if (!facts.ToolsEnabled)
        {
            rows.Add((Theme.DimMarkup(ToolsText.OffLine), null));
        }

        if (!facts.Enabled)
        {
            rows.Add((Theme.DimMarkup(McpText.OffLine), null));
        }

        int width = NameWidth(facts.Servers.Select(s => s.Name));
        if (facts.Servers.Count == 0)
        {
            rows.Add((Theme.DimMarkup(McpText.NoServersLine), null));
        }

        foreach (var server in facts.Servers)
        {
            string state = ToolsText.State(server.Enabled).PadRight(StateWidth);
            bool live = facts.ToolsEnabled && facts.Enabled && server.Startable;
            string row = live
                ? Styled(Theme.AccentSecondary, server.Name.PadRight(width)) + Theme.ColorMarkup(Theme.Ink, state) + Theme.DimMarkup(StatusText(server))
                : Theme.DimMarkup(server.Name.PadRight(width) + state + StatusText(server));
            rows.Add((row, new McpRow.Server(server.Name)));
        }

        rows.Add((Theme.DimMarkup(McpText.EditProfileRow), new McpRow.EditProfile()));
        rows.Add((Theme.DimMarkup(McpText.EditGlobalRow), new McpRow.EditGlobal()));
        if (facts.Enabled)
        {
            rows.Add((Theme.DimMarkup(McpText.ReloadRow), new McpRow.Reload()));
        }

        if (facts.Problems.Count > 0)
        {
            rows.Add((Styled(Theme.AccentSecondary, McpText.SkippedHeading), null));
            foreach (var problem in facts.Problems)
            {
                rows.Add((Theme.DimMarkup("  " + McpText.ProblemLine(problem)), null));
            }
        }

        return rows;
    }

    /// <summary>
    /// The Tools tab: a heading per connected server (<c>── docker · 14 ───…</c>, <c>── docker · 12 of 14 ───…</c>, a
    /// <see cref="SectionRule"/> with an empty row before every one but the first since 2026-10-03, the <c>/tools</c> look) in the
    /// section colour whatever the switches say (the <c>/tools</c> rule, later on 2026-09-20), then a row per tool in the <c>/tools</c> Offered shape — the prefixed name, <c>on</c> /
    /// <c>off</c>, the description dim — the whole row dim while the turn would not offer it;
    /// <see cref="McpText.NoToolsLine"/> with no server connected. The tool's prefixed name beside every tool row.
    /// </summary>
    public static IReadOnlyList<(string Markup, string? Tool, bool Heading)> ToolRows(McpFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<(string, string?, bool)>(32);
        var connected = facts.Servers.Where(s => s.State == McpState.Connected).ToList();
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
        int width = NameWidth(connected.SelectMany(s => s.Tools).Select(t => t.Name));
        foreach (var server in connected)
        {
            if (!ReferenceEquals(server, connected[0]))
            {
                rows.Add(("", null, false));
            }

            int left = server.Tools.Count(t => !facts.Disabled.Contains(t.Name));
            rows.Add((SectionRule.Markup(server.Name, SystemPromptSummary.GroupCount(left, server.Tools.Count)), null, true));
            foreach (var tool in server.Tools)
            {
                bool on = !facts.Disabled.Contains(tool.Name);
                string row = offered && on
                    ? Styled(Theme.AccentSecondary, tool.Name.PadRight(width)) + Theme.ColorMarkup(Theme.Ink, ToolsText.State(on).PadRight(StateWidth)) + Theme.DimMarkup(tool.Description)
                    : Theme.DimMarkup(tool.Name.PadRight(width) + ToolsText.State(on).PadRight(StateWidth) + tool.Description);
                rows.Add((row, tool.Name, false));
            }
        }

        return rows;
    }

    /// <summary>The first server row (the cursor's opening place); 0 when there is none.</summary>
    public static int FirstServerRow(IReadOnlyList<(string Markup, McpRow? Row)> rows)
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

        int width = NameWidth(connected.SelectMany(s => s.Tools).Select(t => t.Name));
        foreach (var server in connected)
        {
            int left = server.Tools.Count(t => !facts.Disabled.Contains(t.Name));
            yield return SystemPromptSummary.GroupName(server.Name, left, server.Tools.Count);
            foreach (var tool in server.Tools)
            {
                yield return "  " + tool.Name.PadRight(width) + ToolsText.State(!facts.Disabled.Contains(tool.Name)).PadRight(StateWidth) + tool.Description;
            }
        }
    }

    private static string Styled(Style style, string text) => $"[{style.ToMarkup()}]{Markup.Escape(text)}[/]";
}
