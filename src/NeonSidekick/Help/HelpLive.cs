using NeonSidekick.App;
using NeonSidekick.Plans;
using NeonSidekick.Settings;

namespace NeonSidekick.Help;

/// <summary>
/// What an MCP tool's name finds among the session's servers (2026-10-25, the neon_help plan's Step 2): the server it belongs to and that
/// server's state. Only the state is ever said, never the server's own detail or a config problem's message (they carry the server's text).
/// </summary>
public sealed record HelpMcpLookup(string Server, McpState State)
{
    /// <summary>
    /// The server a tool's name belongs to among <paramref name="servers"/>: the one listing a tool of that name, else the one whose sanitized
    /// name and <c>__</c> lead it (the longest such); null for none. Shared by the screen and headless.
    /// </summary>
    public static HelpMcpLookup? Find(IReadOnlyList<McpServerStatus> servers, string name)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(name);
        if (servers.FirstOrDefault(s => s.Tools.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal))) is { } exact)
        {
            return new HelpMcpLookup(exact.Name, exact.State);
        }

        var lead = servers
            .Select(s => (Server: s, Prefix: Mcp.McpToolName.Sanitize(s.Name) + "__"))
            .Where(p => name.StartsWith(p.Prefix, StringComparison.Ordinal))
            .OrderByDescending(p => p.Prefix.Length)
            .FirstOrDefault();
        return lead.Server is null ? null : new HelpMcpLookup(lead.Server.Name, lead.Server.State);
    }
}

/// <summary>
/// What the running app says about its tools right now, for <c>neon_help</c>'s "Right now:" line (2026-10-25, the user's call: the first
/// thing the manual reads from the app, reversing 2026-10-02's documentation-only rule, and narrow — whether a tool is offered and why
/// not, never a setting's value beyond whether the switch that offers it is on). Built by the screen and by headless, each from the list
/// the next turn would offer (<see cref="Offered"/>) and the reasons <c>/tools</c> already gives; bots and advisors get none (the advisors'
/// copy is the redacted twin, <see cref="Llm.Tools.NeonHelpTool.AsRedacted"/>). The funcs run only when an answer needs them.
/// </summary>
/// <param name="Offered">Every tool name the next turn would offer, MCP tools among them.</param>
/// <param name="ToolsEnabled"><c>LLM offer tools</c>.</param>
/// <param name="Headless">Whether this is a headless run (no pane, no window).</param>
/// <param name="Planning">Whether plan mode is on.</param>
/// <param name="DisabledByName">The names switched off on <c>/tools</c> › Offered (and <c>/mcp</c> › Tools) alone.</param>
/// <param name="PassReason">The pass's sentence for a name the pass in progress withholds, else null.</param>
/// <param name="GroupReason">Why the group a switch offers is off right now (its switch, then the device or setup), else null.</param>
/// <param name="ToolNote">Why one tool of an offered group is not offered, else null.</param>
public sealed record HelpLive(
    IReadOnlySet<string> Offered,
    bool ToolsEnabled,
    bool Headless,
    bool Planning,
    IReadOnlySet<string> DisabledByName,
    Func<string, string?> PassReason,
    Func<SettingsField, string?> GroupReason,
    Func<string, string?> ToolNote)
{
    /// <summary>The settings the needs are read in (<see cref="HelpToolCatalog.NeedMet"/>); null skips that last step.</summary>
    public AppSettingsData? Settings { get; init; }

    /// <summary>An MCP tool's name looked up among the session's servers; null when there is no session to ask.</summary>
    public Func<string, HelpMcpLookup?>? Mcp { get; init; }

    /// <summary>
    /// The live line for an app tool, in the order the screen and headless share: offered first, whatever the catalog says; then the tools
    /// off, a tool headless leaves out, a pass-only tool outside its pass, the pass in progress, plan mode, off by name, the group's reason,
    /// the tool's own note, and the catalog's needs.
    /// </summary>
    internal string Line(ToolHelp entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Offered.Contains(entry.Name))
        {
            return HelpText.LiveOffered;
        }

        string? why = !ToolsEnabled ? HelpText.LiveToolsOff
            : Headless && !entry.Headless ? HelpText.LiveNotHeadless
            : entry.Switch is null ? entry.Setup
            : PassReason(entry.Name) ?? (Planning && !PlanTools.Allowed(entry.Name) ? HelpText.LivePlanMode : null)
              ?? (DisabledByName.Contains(entry.Name) ? HelpText.LiveOffByName : null)
              ?? (GroupOffered(entry.Switch.Value) ? null : GroupReason(entry.Switch.Value))
              ?? ToolNote(entry.Name)
              ?? Need(entry);
        return HelpText.LiveNotOffered(why);
    }

    // A group with any tool offered is on: a missing tool of it has its own reason, never the group's (headless's reasons read no group state).
    private bool GroupOffered(SettingsField field) => HelpToolCatalog.All.Any(e => e.Switch == field && Offered.Contains(e.Name));

    /// <summary>The live line for a whole group (a group list's line, a group switch's answer): offered when any of its tools is.</summary>
    internal string GroupLine(SettingsField? field, IReadOnlyList<ToolHelp> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Any(t => Offered.Contains(t.Name)))
        {
            return HelpText.LiveOffered;
        }

        string? why = !ToolsEnabled ? HelpText.LiveToolsOff
            : Headless && tools.All(t => !t.Headless) ? HelpText.LiveNotHeadless
            : field is null ? tools[0].Setup
            : Planning && tools.All(t => !PlanTools.Allowed(t.Name)) ? HelpText.LivePlanMode
            : GroupReason(field.Value);
        return HelpText.LiveNotOffered(why);
    }

    /// <summary>
    /// The live line for an MCP tool: offered; else the tools off, plan mode, <c>MCP servers enabled</c> off, no server of that name, its
    /// server not connected (the state alone), the tool off on <c>/mcp</c> › Tools.
    /// </summary>
    internal string McpLine(string name, HelpMcpLookup? found)
    {
        if (Offered.Contains(name))
        {
            return HelpText.LiveOffered;
        }

        string? why = !ToolsEnabled ? HelpText.LiveToolsOff
            : Planning ? HelpText.LivePlanMode
            : Settings is { McpServers: false } ? ToolsText.SwitchOffReason(SettingsField.McpServers)
            : found is null ? HelpText.LiveNoMcpServer
            : found.State != McpState.Connected ? HelpText.LiveMcpState(found.State)
            : DisabledByName.Contains(name) ? HelpText.LiveMcpOffByName
            : null;
        return HelpText.LiveNotOffered(why);
    }

    /// <summary>
    /// The pass's reason for <paramref name="name"/> (2026-10-25), shared by the screen and headless: a code-audit pass offers only its read
    /// tools (the web's under <c>Code audit web research</c>); a Ralph pass with nobody to ask (the checkpoint at 0, or headless) leaves out the
    /// tools that ask and confirm; a gap or audit pass under its ask switch off leaves out the asking tools. Null for none.
    /// </summary>
    internal static string? PassReasonFor(string name, AppSettingsData effective, bool auditPass, bool ralphBarred, bool ralphHeadless, bool gapReviewing)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (auditPass && !Audits.AuditTools.Allowed(effective).Contains(name))
        {
            return Audits.AuditTools.Web.Contains(name, StringComparer.Ordinal) && !effective.CodeAuditWebResearch ? HelpText.LiveAuditWeb : HelpText.LiveAuditPass;
        }

        if (ralphBarred && (PlanTools.AskingTools.Contains(name) || PlanTools.ConfirmingTools.Contains(name)))
        {
            return ralphHeadless ? HelpText.LiveHeadlessRalphAsk : HelpText.LiveRalphAsk;
        }

        if (PlanTools.AskingTools.Contains(name))
        {
            if (gapReviewing && !effective.PlanGapAskUser)
            {
                return HelpText.LiveGapAsk;
            }

            if (auditPass && !effective.CodeAuditAskUser)
            {
                return HelpText.LiveAuditAsk;
            }
        }

        return null;
    }

    /// <summary>A group note or tool note without its <c>not offered: </c> lead, for the live line's brackets.</summary>
    internal static string? Bare(string? note)
    {
        string lead = SystemPromptSummary.NotOffered("");
        return string.IsNullOrEmpty(note) ? null : note.StartsWith(lead, StringComparison.Ordinal) ? note[lead.Length..] : note;
    }

    /// <summary>
    /// Why the group <c>/tools</c> switches with a field is off, from <paramref name="groups"/> (2026-10-25): null while it is offered or
    /// unknown; else <paramref name="offReason"/>'s one reason, else the group's own note.
    /// </summary>
    internal static Func<SettingsField, string?> GroupReasons(IReadOnlyList<ToolGroup> groups, Func<SettingsField, string?> offReason)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(offReason);
        return field => groups.FirstOrDefault(g => g.Switch == field) is { Offered: false } group ? offReason(field) ?? Bare(group.Note) : null;
    }

    /// <summary>A tool's own note in an offered group of <paramref name="groups"/> (2026-10-25), without its lead; null for none.</summary>
    internal static Func<string, string?> ToolNotes(IReadOnlyList<ToolGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return name => groups.Where(g => g.Offered).Select(g => g.ToolNotes.TryGetValue(name, out var note) ? Bare(note) : null).FirstOrDefault(n => n is not null);
    }

    private string? Need(ToolHelp entry)
    {
        if (Settings is null)
        {
            return entry.Setup;
        }

        foreach (var need in entry.Needs)
        {
            if (HelpToolCatalog.NeedMet(need, Settings) == false)
            {
                return HelpToolCatalog.NeedReason(need);
            }
        }

        return entry.Setup;
    }
}
