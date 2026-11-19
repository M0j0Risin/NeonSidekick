using System.Text.Json;

namespace NeonSidekick.Mcp;

/// <summary>
/// A server's <c>"ask"</c> in <c>mcp.json</c> (2026-11-19, feature idea #28, MCP calls ask): <see cref="Writes"/>, the default, asks before
/// every tool the server doesn't mark read-only; <see cref="Always"/> asks before every call; <see cref="Never"/> asks before none.
/// <see cref="Writes"/> is first, so a default value is the default mode.
/// </summary>
public enum McpAskMode
{
    Writes,
    Always,
    Never,
}

/// <summary>
/// What the allow pane's caption may say of a tool's effects (2026-11-19, feature idea #28): only what the server itself said.
/// <see cref="SaysDestructive"/> for <c>destructiveHint: true</c> on a tool that isn't read-only; <see cref="Unstated"/> for a tool
/// that is <see cref="McpTool.Destructive"/> only by the spec's default (no <c>destructiveHint</c> at all); <see cref="None"/> for
/// <c>destructiveHint: false</c> or a read-only tool. A plain bool couldn't tell "the server says so" from "the server says nothing".
/// </summary>
public enum McpChangeNote
{
    None,
    SaysDestructive,
    Unstated,
}

/// <summary>
/// One call that asks (2026-11-19, feature idea #28), as the session hands it to its approver: the configured server's name, the tool's
/// name on the server, the prefixed name the model and the pane use (<see cref="McpToolName"/>, sanitised), the arguments as sent, and
/// what the caption may say of its effects.
/// </summary>
public sealed record McpAsk(string Server, string ServerTool, string Name, IReadOnlyDictionary<string, JsonElement> Arguments, McpChangeNote Note);

/// <summary>
/// An approver's answer (2026-11-19, feature idea #28). <see cref="Allowed"/> carries the audit's word for how (<c>allowed once</c>,
/// <c>allowed for the session</c>, <c>allowed earlier this session</c>, <c>allowed by --allow mcp:github</c>); <see cref="NotAsked"/>
/// carries the tail the nobody-to-ask text ends with (headless names its <c>--allow</c>; the screen without a pane has none). So the host
/// supplies both, and the session needs no headless flag.
/// </summary>
public abstract record McpVerdict
{
    private McpVerdict()
    {
    }

    /// <summary>The call runs; <paramref name="How"/> is the audit's word.</summary>
    public sealed record Allowed(string How) : McpVerdict;

    /// <summary>The user said no: nothing is sent.</summary>
    public sealed record Denied : McpVerdict;

    /// <summary>Nobody could be asked: nothing is sent, and the model's text ends with <paramref name="Tail"/> when there is one.</summary>
    public sealed record NotAsked(string? Tail) : McpVerdict;

    /// <summary>The one denial.</summary>
    public static readonly McpVerdict No = new Denied();

    /// <summary>Nobody to ask and no tail: what a session without an approver answers.</summary>
    public static readonly McpVerdict Nobody = new NotAsked(null);
}

/// <summary>
/// The one rule of who asks (2026-11-19, feature idea #28), in one place: the session's gate, <c>AskingNames()</c>, the <c>/mcp</c> tag
/// and <c>neon_help</c> all call it. Pure.
/// </summary>
public static class McpAskRule
{
    /// <summary>The words <c>"ask"</c> takes, as the problem text names them.</summary>
    public static readonly IReadOnlyList<string> Words = ["writes", "always", "never"];

    /// <summary>Whether a call to <paramref name="tool"/> asks under <paramref name="mode"/>.</summary>
    public static bool Asks(McpAskMode mode, McpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return mode switch
        {
            McpAskMode.Always => true,
            McpAskMode.Never => false,
            _ => !tool.ReadOnly,
        };
    }

    /// <summary>What the caption may say of <paramref name="tool"/>'s effects (<see cref="McpChangeNote"/>).</summary>
    public static McpChangeNote Note(McpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.Destructive)
        {
            return McpChangeNote.None;
        }

        return tool.ProtocolTool.Annotations?.DestructiveHint == true ? McpChangeNote.SaysDestructive : McpChangeNote.Unstated;
    }

    /// <summary><c>"ask"</c>'s value read without regard to case (absent or blank is <see cref="McpAskMode.Writes"/>), or false for any other word.</summary>
    public static bool TryParse(string? value, out McpAskMode mode)
    {
        mode = McpAskMode.Writes;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "writes":
                return true;
            case "always":
                mode = McpAskMode.Always;
                return true;
            case "never":
                mode = McpAskMode.Never;
                return true;
            default:
                return false;
        }
    }
}
