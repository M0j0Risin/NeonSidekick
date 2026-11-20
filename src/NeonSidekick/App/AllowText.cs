using System.Globalization;
using NeonSidekick.Mcp;

namespace NeonSidekick.App;

/// <summary>
/// The words of a headless run's <c>--allow</c> (2026-11-20, the mcp-ask review's #52, #36, #96, #61: they were spread over
/// <c>SidekickOptions</c>, <c>McpText</c> and <c>HeadlessAllowances</c>): the closing summaries, the start's errors and notices, the audit's
/// "allowed by" word and the refusal tails the model reads. Each kind's word and nouns come from <see cref="HeadlessAllowances.Kinds"/>.
/// </summary>
public static class AllowText
{
    /// <summary>The flag, as every line names it.</summary>
    public const string Flag = SidekickOptions.AllowFlag;

    /// <summary>The audit's word for a call <c>--allow</c> let run: <c>allowed by --allow mcp:github</c>.</summary>
    public static string AllowedBy(string entry) => "allowed by " + Flag + " " + entry;

    /// <summary>The start's error (exit 2) for an <c>mcp:&lt;server&gt;</c> that names no server in either <c>mcp.json</c>, broken entries included.</summary>
    public static string NamesNoServer(string server) => Flag + " mcp:" + server + " names no MCP server";

    /// <summary>
    /// The start's error (exit 2, 2026-11-20, the mcp-ask review's #75) for an <c>mcp:&lt;server&gt;</c> that matches several servers without
    /// regard to case and none exactly: <c>--allow mcp:GITHUB matches GitHub and github; give the name as mcp.json spells it</c>.
    /// </summary>
    public static string Ambiguous(string server, IReadOnlyList<string> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        string listed = servers.Count <= 1 ? string.Join("", servers) : string.Join(", ", servers.Take(servers.Count - 1)) + " and " + servers[^1];
        return Flag + " mcp:" + server + " matches " + listed + "; give the name as " + McpConfigFile.FileName + " spells it";
    }

    /// <summary>The closing summary's MCP line: <c>2 MCP calls were refused: a, b (--allow mcp:github,mcp:notes allows them)</c>.</summary>
    public static string McpRefusedSummary(int count, IReadOnlyList<string> names, IReadOnlyList<string> entries) =>
        count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " MCP call was refused: " : " MCP calls were refused: ") + string.Join(", ", names)
        + " (" + Flag + " " + string.Join(",", entries) + (count == 1 ? " allows it)" : " allows them)");

    /// <summary>The closing summary's line for Docker, print, Home Assistant or an advisor: <c>1 Docker change was refused (--allow docker allows it)</c>.</summary>
    public static string RefusedSummary(AllowKind kind, int count)
    {
        var info = HeadlessAllowances.Info(kind);
        return count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? info.One : info.Many) + (count == 1 ? " was refused (" : " were refused (")
            + Flag + " " + info.Word + (count == 1 ? " allows it)" : " allows them)");
    }

    /// <summary>
    /// The model's tail after a Docker, print, Home Assistant or advisor refusal in a headless run (2026-11-20, the mcp-ask review's #24,
    /// #57), as MCP's names its entry: <c> A headless run allows it with --allow docker.</c> Pinned (the model reads it).
    /// </summary>
    public static string NotAskedTail(AllowKind kind) => " A headless run allows it with " + Flag + " " + HeadlessAllowances.Info(kind).Word + ".";

    /// <summary>
    /// The start's notice (2026-11-20, the mcp-ask review's #110) for an entry whose calls never ask under this run's settings, so it lets
    /// nothing through: <c>--allow print changes nothing: Print action policy isn't ask</c>.
    /// </summary>
    public static string ChangesNothing(string entry, string why) => Flag + " " + entry + " changes nothing: " + why;

    // The reasons ChangesNothing names.
    public const string DockerOff = "the Docker tools are off";
    public const string DockerWritesOff = "Docker writes are off";
    public const string PrintOff = "the print tools are off";
    public const string PrintNotAsk = "Print action policy isn't ask";
    public const string HaOff = "the Home Assistant tools are off";
    public const string HaNotAsk = "Home Assistant action policy isn't ask";
    public const string AdvisorOff = "the advisors are off";
    public const string AdvisorConfirmOff = "Advisor confirm is off";
    public const string McpOff = "MCP servers are off";

    /// <summary>The reason for one server whose <c>"ask"</c> is <c>never</c>: <c>github's ask is never</c>.</summary>
    public static string AskNever(string server) => server + "'s ask is never";

    /// <summary>What a Ralph review by advisor logs as allowed: <c>Ralph review of pass 3</c>.</summary>
    public static string RalphReviewWhat(int pass) => "Ralph review of pass " + pass.ToString(CultureInfo.InvariantCulture);
}
