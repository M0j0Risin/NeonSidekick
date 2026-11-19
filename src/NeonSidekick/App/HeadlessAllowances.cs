using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>The kinds of call <c>--allow</c> can let a headless run make (2026-11-19, feature idea #45).</summary>
public enum AllowKind
{
    Docker,
    Print,
    Ha,
    Advisor,
    Mcp,
}

/// <summary>
/// A headless run's <c>--allow</c> (2026-11-19, feature idea #45): the entries it was given, each hook in the shape its tool takes
/// (<see cref="Confirm"/> for Docker, print and Home Assistant; <see cref="Advisor"/>; <see cref="Mcp"/> for the MCP session's approver),
/// and what each refused, for the closing summary and exit 3. An allowed call is logged under its family's category; a refused one is
/// recorded and answers null (or <see cref="McpVerdict.NotAsked"/>), so the tool's own "nobody could be asked" text is what the model
/// reads. Pure apart from <see cref="DiagnosticLog"/>. The collector is locked: an MCP approver runs on a call's thread, a pass's advisor
/// on another.
/// </summary>
public sealed class HeadlessAllowances
{
    /// <summary>The words an entry may be, besides <c>mcp:&lt;server&gt;</c>, in the order the help names them.</summary>
    public static readonly IReadOnlyList<string> Words = ["docker", "print", "ha", "advisor", "mcp"];

    /// <summary>The prefix of a one-server MCP entry.</summary>
    public const string McpServerPrefix = "mcp:";

    private readonly HashSet<AllowKind> _kinds = [];
    private readonly List<string> _mcpServers = [];
    private readonly List<(AllowKind Kind, string? Server, string? Name)> _refusals = [];
    private IReadOnlyList<string> _known = [];

    /// <param name="entries">The parsed entries (<see cref="SidekickOptions.AllowList"/>): the words lower case, a server as typed.</param>
    public HeadlessAllowances(IReadOnlyList<string>? entries = null)
    {
        foreach (string entry in entries ?? [])
        {
            if (entry.StartsWith(McpServerPrefix, StringComparison.Ordinal))
            {
                _mcpServers.Add(entry[McpServerPrefix.Length..]);
            }
            else if (KindOf(entry) is { } kind)
            {
                _kinds.Add(kind);
            }
        }
    }

    /// <summary>
    /// One <c>--allow</c> value's entries, comma-separated, appended to <paramref name="into"/> once each: the words without regard to case,
    /// a server after <c>mcp:</c> kept as typed and trimmed. False with <paramref name="error"/> for an unknown entry or a blank server. Pure.
    /// </summary>
    public static bool TryParse(string value, List<string> into, out string? error)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(into);
        error = null;
        foreach (string part in value.Split(','))
        {
            string entry = part.Trim();
            string canonical;
            if (entry.StartsWith(McpServerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string server = entry[McpServerPrefix.Length..].Trim();
                if (server.Length == 0)
                {
                    error = SidekickOptions.AllowBlankServer;
                    return false;
                }

                canonical = McpServerPrefix + server;
            }
            else
            {
                canonical = entry.ToLowerInvariant();
                if (KindOf(canonical) is null)
                {
                    error = SidekickOptions.AllowUnknown(entry);
                    return false;
                }
            }

            if (!into.Contains(canonical, StringComparer.Ordinal))
            {
                into.Add(canonical);
            }
        }

        return true;
    }

    private static AllowKind? KindOf(string word) => word switch
    {
        "docker" => AllowKind.Docker,
        "print" => AllowKind.Print,
        "ha" => AllowKind.Ha,
        "advisor" => AllowKind.Advisor,
        "mcp" => AllowKind.Mcp,
        _ => null,
    };

    /// <summary>
    /// The catalog's server names, after the headless connect: what an <c>mcp:&lt;server&gt;</c> entry matches against (a name typed exactly
    /// picks that server alone). The notice lines for the entries that name no server, one each.
    /// </summary>
    public IReadOnlyList<string> KnowServers(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _known = names.ToList();
        return _mcpServers
            .Where(entry => !_known.Any(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase)))
            .Select(SidekickOptions.AllowNamesNoServer)
            .ToList();
    }

    /// <summary>Whether the run allows <paramref name="kind"/> (for MCP: every server). No log line.</summary>
    public bool Allows(AllowKind kind) => _kinds.Contains(kind);

    /// <summary>Whether the run allows <paramref name="server"/>'s calls that ask. No log line.</summary>
    public bool AllowsMcp(string server) => McpEntry(server) is not null;

    /// <summary>
    /// The entry that lets <paramref name="server"/>'s calls run: <c>mcp</c>, or <c>mcp:&lt;server&gt;</c> matched without regard to case,
    /// unless some server has the entry's exact name, which then matches that server alone. Null when none does.
    /// </summary>
    private string? McpEntry(string server)
    {
        if (_kinds.Contains(AllowKind.Mcp))
        {
            return "mcp";
        }

        foreach (string entry in _mcpServers)
        {
            bool exactElsewhere = _known.Contains(entry, StringComparer.Ordinal);
            bool match = exactElsewhere ? string.Equals(entry, server, StringComparison.Ordinal) : string.Equals(entry, server, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return McpServerPrefix + entry;
            }
        }

        return null;
    }

    /// <summary>The <c>confirm</c> hook for Docker, print or Home Assistant: true (logged) when allowed, else the refusal recorded and null.</summary>
    public Func<string, CancellationToken, Task<bool?>> Confirm(AllowKind kind) => (prompt, _) =>
    {
        if (Allows(kind))
        {
            LogAllowed(kind, prompt);
            return Task.FromResult<bool?>(true);
        }

        Refused(kind, null, null);
        return Task.FromResult<bool?>(null);
    };

    /// <summary>The advisor's confirm hook (<c>neon_advisor</c> under <c>Advisor confirm</c>).</summary>
    public Func<AdvisorEntry, string, CancellationToken, Task<bool?>> Advisor => (advisor, question, _) =>
    {
        if (Allows(AllowKind.Advisor))
        {
            LogAllowed(AllowKind.Advisor, advisor.Profile + ": " + question);
            return Task.FromResult<bool?>(true);
        }

        Refused(AllowKind.Advisor, null, null);
        return Task.FromResult<bool?>(null);
    };

    /// <summary>The MCP session's approver: the session audits an allowed call itself, with the entry that matched as the word.</summary>
    public Func<McpAsk, CancellationToken, Task<McpVerdict>> Mcp => (ask, _) =>
    {
        if (McpEntry(ask.Server) is { } entry)
        {
            return Task.FromResult<McpVerdict>(new McpVerdict.Allowed(McpText.AllowedBy(entry)));
        }

        Refused(AllowKind.Mcp, ask.Server, ask.Name);
        return Task.FromResult<McpVerdict>(new McpVerdict.NotAsked(McpText.HeadlessAllowTail(ask.Server)));
    };

    /// <summary>
    /// One <c>allowed by --allow &lt;entry&gt;: &lt;what&gt;</c> line under <paramref name="kind"/>'s category, <paramref name="what"/> redacted
    /// and its hiding characters escaped: what a pass that calls past the confirm (a Ralph review by advisor) writes itself.
    /// </summary>
    public static void LogAllowed(AllowKind kind, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        string text = Files.NameScrub.Display(Llm.Tools.ToolRedaction.Text(what));
        if (text.Length > McpText.AuditArgumentChars)
        {
            text = Files.TextCut.Safe(text, McpText.AuditArgumentChars) + "…";
        }

        DiagnosticLog.Info(CategoryOf(kind), McpText.AllowedBy(EntryWord(kind)) + ": " + text);
    }

    /// <summary>What a Ralph review by advisor logs as allowed: <c>Ralph review of pass 3</c>.</summary>
    public static string RalphReviewWhat(int pass) => "Ralph review of pass " + pass.ToString(CultureInfo.InvariantCulture);

    private static string EntryWord(AllowKind kind) => kind switch
    {
        AllowKind.Docker => "docker",
        AllowKind.Print => "print",
        AllowKind.Ha => "ha",
        AllowKind.Advisor => "advisor",
        _ => "mcp",
    };

    private static string CategoryOf(AllowKind kind) => kind switch
    {
        AllowKind.Docker => Docker.DockerText.Category,
        AllowKind.Print => Printing.PrintText.Category,
        AllowKind.Ha => HomeAssistant.HaText.Category,
        AllowKind.Advisor => Advisors.AdvisorText.Category,
        _ => McpConfigFile.Category,
    };

    private void Refused(AllowKind kind, string? server, string? name)
    {
        lock (_refusals)
        {
            _refusals.Add((kind, server, name));
        }
    }

    /// <summary>Every call the run refused for want of an <c>--allow</c>, in order.</summary>
    public IReadOnlyList<AllowKind> Refusals
    {
        get
        {
            lock (_refusals)
            {
                return _refusals.Select(r => r.Kind).ToList();
            }
        }
    }

    /// <summary>
    /// The closing summary's lines, one per kind refused, each naming its flag: <c>2 MCP calls were refused: github__create_issue,
    /// github__push_files (--allow mcp:github allows them)</c>, <c>1 Docker change was refused (--allow docker allows it)</c>.
    /// </summary>
    public IReadOnlyList<string> SummaryLines()
    {
        List<(AllowKind Kind, string? Server, string? Name)> refusals;
        lock (_refusals)
        {
            refusals = [.. _refusals];
        }

        var lines = new List<string>();
        foreach (var kind in new[] { AllowKind.Mcp, AllowKind.Docker, AllowKind.Print, AllowKind.Ha, AllowKind.Advisor })
        {
            var of = refusals.Where(r => r.Kind == kind).ToList();
            if (of.Count == 0)
            {
                continue;
            }

            if (kind == AllowKind.Mcp)
            {
                var names = of.Select(r => r.Name!).Distinct(StringComparer.Ordinal).ToList();
                var servers = of.Select(r => McpServerPrefix + r.Server).Distinct(StringComparer.Ordinal).ToList();
                lines.Add(SidekickOptions.McpRefusedSummary(of.Count, names, servers));
            }
            else
            {
                lines.Add(SidekickOptions.RefusedSummary(kind, of.Count, EntryWord(kind)));
            }
        }

        return lines;
    }
}
