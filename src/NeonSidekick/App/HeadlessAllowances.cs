using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>One kind of <c>--allow</c> (2026-11-20, the mcp-ask review's #31, #113, #120, #33): its word, the log category its allowed calls go under, and the summary's nouns.</summary>
public sealed record AllowKindInfo(AllowKind Kind, string Word, string Category, string One, string Many);

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
    /// <summary>
    /// Every kind, in the order the help names them: the one table each word, category and noun is read from (2026-11-20, the mcp-ask
    /// review's #31, #113, #120, #33), so a new kind can't be missed by a switch's default.
    /// </summary>
    public static readonly IReadOnlyList<AllowKindInfo> Kinds =
    [
        new(AllowKind.Docker, "docker", Docker.DockerText.Category, "Docker change", "Docker changes"),
        new(AllowKind.Print, "print", Printing.PrintText.Category, "print", "prints"),
        new(AllowKind.Ha, "ha", HomeAssistant.HaText.Category, "Home Assistant call", "Home Assistant calls"),
        new(AllowKind.Advisor, "advisor", Advisors.AdvisorText.Category, "advisor call", "advisor calls"),
        new(AllowKind.Mcp, "mcp", McpConfigFile.Category, "MCP call", "MCP calls"),
    ];

    /// <summary>The words an entry may be, besides <c>mcp:&lt;server&gt;</c>, in the order the help names them.</summary>
    public static readonly IReadOnlyList<string> Words = Kinds.Select(k => k.Word).ToArray();

    /// <summary><paramref name="kind"/>'s row of <see cref="Kinds"/>.</summary>
    public static AllowKindInfo Info(AllowKind kind) => Kinds.First(k => k.Kind == kind);

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

    /// <summary>The kind an entry's word names, or null for a word no kind has.</summary>
    private static AllowKind? KindOf(string word) => Kinds.FirstOrDefault(k => k.Word == word)?.Kind;

    /// <summary>
    /// Every server name the files hold, read before anything connects (2026-11-20, the mcp-ask review's #9, #49, #124, #75, #111): the
    /// entries and the ones skipped for a problem, so a typo in a server's own entry isn't taken for a typo in <c>--allow</c>. What an
    /// <c>mcp:&lt;server&gt;</c> entry matches against: a name typed exactly picks that server alone, else one server without regard to case.
    /// The errors, one per entry, that end the run with exit 2: a name that matches no server (<see cref="AllowText.NamesNoServer"/>), or
    /// several (<see cref="AllowText.Ambiguous"/>).
    /// </summary>
    public IReadOnlyList<string> KnowServers(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _known = names.Distinct(StringComparer.Ordinal).ToList();
        var errors = new List<string>();
        foreach (string entry in _mcpServers)
        {
            if (_known.Contains(entry, StringComparer.Ordinal))
            {
                continue;
            }

            var matches = _known.Where(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                errors.Add(AllowText.NamesNoServer(entry));
            }
            else if (matches.Count > 1)
            {
                errors.Add(AllowText.Ambiguous(entry, matches));
            }
        }

        return errors;
    }

    /// <summary>The <c>mcp:&lt;server&gt;</c> entries as given, for the start's never-asks notices.</summary>
    public IReadOnlyList<string> McpServers => _mcpServers;

    /// <summary>Whether the run allows <paramref name="kind"/> (for MCP: every server). No log line.</summary>
    public bool Allows(AllowKind kind) => _kinds.Contains(kind);

    /// <summary>Whether the run allows <paramref name="server"/>'s calls that ask. No log line.</summary>
    public bool AllowsMcp(string server) => McpEntry(server) is not null;

    /// <summary>
    /// The entry that lets <paramref name="server"/>'s calls run: <c>mcp</c>, or <c>mcp:&lt;server&gt;</c> matched without regard to case,
    /// unless some server has the entry's exact name, which then matches that server alone. Null when none does, and (2026-11-20, the
    /// mcp-ask review's #75) for an entry that matches several servers without regard to case and none exactly: the run has ended with
    /// exit 2 by then, and this keeps a caller that skipped the check from guessing.
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
            bool match = exactElsewhere
                ? string.Equals(entry, server, StringComparison.Ordinal)
                : string.Equals(entry, server, StringComparison.OrdinalIgnoreCase) && _known.Count(name => string.Equals(name, entry, StringComparison.OrdinalIgnoreCase)) <= 1;
            if (match)
            {
                return McpServerPrefix + entry;
            }
        }

        return null;
    }

    /// <summary>The <c>confirm</c> hook for Docker, print or Home Assistant: true (logged) when allowed, else the refusal recorded and null.</summary>
    public Func<string, CancellationToken, Task<bool?>> Confirm(AllowKind kind) => (prompt, _) => Task.FromResult(Judge(kind, prompt));

    /// <summary>The advisor's confirm hook (<c>neon_advisor</c> under <c>Advisor confirm</c>).</summary>
    public Func<AdvisorEntry, string, CancellationToken, Task<bool?>> Advisor => (advisor, question, _) => Task.FromResult(Judge(AllowKind.Advisor, advisor.Profile + ": " + question));

    /// <summary>The one judge of the confirm hooks (#31): true, logged, when the run allows <paramref name="kind"/>; else the refusal recorded and null.</summary>
    private bool? Judge(AllowKind kind, string what)
    {
        if (Allows(kind))
        {
            LogAllowed(kind, what);
            return true;
        }

        Refused(kind, null, null);
        return null;
    }

    /// <summary>The MCP session's approver: the session audits an allowed call itself, with the entry that matched as the word.</summary>
    public Func<McpAsk, CancellationToken, Task<McpVerdict>> Mcp => (ask, _) =>
    {
        if (McpEntry(ask.Server) is { } entry)
        {
            return Task.FromResult<McpVerdict>(new McpVerdict.Allowed(AllowText.AllowedBy(entry)));
        }

        Refused(AllowKind.Mcp, ask.Server, ask.Name);
        return Task.FromResult<McpVerdict>(new McpVerdict.NotAsked(McpText.HeadlessAllowTail(ask.Server)));
    };

    /// <summary>
    /// One <c>allowed by --allow &lt;entry&gt;: &lt;what&gt;</c> line under <paramref name="kind"/>'s category: what a pass that calls past
    /// the confirm (a Ralph review by advisor) writes itself. <paramref name="what"/> on one line (Home Assistant's question has two), then
    /// redacted, cut and its hiding characters escaped by the one audit helper (2026-11-20, the mcp-ask review's #96, #41).
    /// </summary>
    public static void LogAllowed(AllowKind kind, string what)
    {
        ArgumentNullException.ThrowIfNull(what);
        string text = Llm.Tools.ToolRedaction.AuditText(what.ReplaceLineEndings(" "), McpText.AuditArgumentChars);
        var info = Info(kind);
        DiagnosticLog.Info(info.Category, AllowText.AllowedBy(info.Word) + ": " + text);
    }

    private void Refused(AllowKind kind, string? server, string? name)
    {
        lock (_refusals)
        {
            _refusals.Add((kind, server, name));
        }
    }

    /// <summary>Whether the run refused any call for want of an <c>--allow</c> (#31): exit 3's half of it.</summary>
    public bool AnyRefused
    {
        get
        {
            lock (_refusals)
            {
                return _refusals.Count > 0;
            }
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
        // MCP's line first, as since the start; the rest in the table's order.
        foreach (var info in Kinds.OrderBy(k => k.Kind == AllowKind.Mcp ? 0 : 1))
        {
            var of = refusals.Where(r => r.Kind == info.Kind).ToList();
            if (of.Count == 0)
            {
                continue;
            }

            if (info.Kind == AllowKind.Mcp)
            {
                var names = of.Select(r => r.Name!).Distinct(StringComparer.Ordinal).ToList();
                var servers = of.Select(r => McpText.AllowEntry(r.Server!)).Distinct(StringComparer.Ordinal).ToList();
                lines.Add(AllowText.McpRefusedSummary(of.Count, names, servers));
            }
            else
            {
                lines.Add(AllowText.RefusedSummary(info.Kind, of.Count));
            }
        }

        return lines;
    }
}
