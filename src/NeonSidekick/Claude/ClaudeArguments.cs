namespace NeonSidekick.Claude;

/// <summary>
/// The command line of one <c>/claude</c> child (2026-09-27), pure and tested as data. Always
/// <c>-p</c> (headless, the prompt read from stdin), <c>stream-json</c> out with the partial messages (the reply
/// streams as the local model's does; <c>--verbose</c> is what the CLI requires for that format under <c>-p</c>),
/// and <c>--permission-prompts none</c>: nothing can answer a prompt here, so whatever the level does not allow is
/// denied on the spot rather than hanging the run. <c>--session-id</c> opens the thread with the id this app minted,
/// <c>--resume</c> carries it on — so the thread survives this app's restarts with the session that stores the id.
/// The user's own Claude Code configuration (CLAUDE.md, skills, MCP servers, hooks) is loaded as if they ran it
/// themselves: nothing here narrows it (the brainstorm's default, 2026-09-27).
/// </summary>
public static class ClaudeArguments
{
    /// <summary>The tools of <see cref="ClaudePermissionLevel.ReadOnly"/>: read, search, fetch. Pinned.</summary>
    public const string ReadOnlyTools = "Read,Grep,Glob,WebSearch,WebFetch";

    public static IReadOnlyList<string> Build(ClaudeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
            "--permission-prompts", "none",
            request.Resume ? "--resume" : "--session-id", request.SessionId,
        };

        switch (request.Permission)
        {
            case ClaudePermissionLevel.ReadOnly:
                arguments.Add("--tools");
                arguments.Add(ReadOnlyTools);
                break;
            case ClaudePermissionLevel.Edit:
                arguments.Add("--permission-mode");
                arguments.Add("acceptEdits");
                break;
            case ClaudePermissionLevel.Full:
                arguments.Add("--permission-mode");
                arguments.Add("bypassPermissions");
                break;
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model.Trim());
        }

        if (!string.IsNullOrWhiteSpace(request.Effort))
        {
            arguments.Add("--effort");
            arguments.Add(request.Effort.Trim());
        }

        return arguments;
    }

    /// <summary>The MCP server's name in the Claude CLI server's config: its tools reach the model as <c>mcp__neon__&lt;tool&gt;</c>. Pinned.</summary>
    public const string McpServerName = "neon";

    /// <summary>What the CLI puts in front of each of the app's tool names (<see cref="McpServerName"/>'s tools). Pinned.</summary>
    public const string McpToolPrefix = "mcp__" + McpServerName + "__";

    /// <summary>
    /// The command line of the Claude CLI server (2026-09-30): one long-lived child, fed one <c>stream-json</c> line per user
    /// message on a stdin that stays open (<c>--input-format stream-json</c>), its replies read as <c>/claude</c>'s are. The
    /// CLI's own tools are all off (<c>--tools ""</c>): the model gets the app's tools alone, through the one MCP server in
    /// <paramref name="mcpConfig"/> (<c>--strict-mcp-config</c>: none of the user's), allowed without a prompt
    /// (<c>--allowedTools mcp__neon</c>) since the app asks its own questions (<c>CommandGate</c>, <c>ask_user</c>). The
    /// user's Claude Code set-up stays out of it: no skills or slash commands (<c>--disable-slash-commands</c>), no settings
    /// files and so no hooks (<c>--restricted</c>). The system prompt is the app's own, from a file (Windows caps a command
    /// line at 32K characters), and rendered fresh on each request (<c>--system-prompt-snapshot off</c>): by default the CLI
    /// records a session's first prompt and a resume keeps it, which the spike of 2026-09-30 saw ignore a changed persona.
    /// </summary>
    public static IReadOnlyList<string> BuildServer(ClaudeServerLaunch launch, bool resume, string promptFile, string mcpConfig)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var arguments = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
            resume ? "--resume" : "--session-id", launch.SessionId,
            "--tools", "",
            "--strict-mcp-config",
            "--mcp-config", mcpConfig,
            "--allowedTools", "mcp__" + McpServerName,
            "--permission-prompts", "none",
            "--disable-slash-commands",
            "--restricted",
            "--system-prompt-snapshot", "off",
            "--system-prompt-file", promptFile,
            "--model", launch.Model,
        };
        AddEffort(arguments, launch.Effort);
        return arguments;
    }

    /// <summary>
    /// The command line of one request asked beside the conversation (2026-09-30: a session title, a skill reflection, a
    /// <c>/compact</c> summary, a <c>/botchat</c> bot over the Claude CLI server): a <c>claude -p</c> of its own, the
    /// conversation flattened into its stdin, no tools at all, no session kept — so nothing it asks lands in the chat's
    /// Claude session.
    /// </summary>
    public static IReadOnlyList<string> BuildOneShot(string model, string? effort, string promptFile)
    {
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--verbose",
            "--tools", "",
            "--strict-mcp-config",
            "--permission-prompts", "none",
            "--disable-slash-commands",
            "--restricted",
            "--no-session-persistence",
            "--system-prompt-file", promptFile,
            "--model", model,
        };
        AddEffort(arguments, effort);
        return arguments;
    }

    /// <summary>
    /// The <c>--mcp-config</c> JSON of the Claude CLI server: one stdio server, <see cref="McpServerName"/>, that is this
    /// app's own executable in its relay mode (<see cref="McpRelay"/>) pointed at the loopback listener with its key.
    /// </summary>
    public static string McpConfig(string command, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(args);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcpServers");
            writer.WriteStartObject(McpServerName);
            writer.WriteString("type", "stdio");
            writer.WriteString("command", command);
            writer.WriteStartArray("args");
            foreach (string arg in args)
            {
                writer.WriteStringValue(arg);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void AddEffort(List<string> arguments, string? effort)
    {
        if (!string.IsNullOrWhiteSpace(effort))
        {
            arguments.Add("--effort");
            arguments.Add(effort.Trim());
        }
    }
}

/// <summary>
/// What the Claude CLI server was started with (2026-09-30): the server is kept while the next turn asks for the same, and
/// restarted with <c>--resume</c> on the same session when anything here changed (<see cref="ClaudeServerHost"/>). Value
/// equality throughout: <see cref="ToolNames"/> is the offered tools' names joined, not a list.
/// </summary>
/// <param name="Executable">The CLI's full path (<see cref="ClaudeExecutable.Locate"/>).</param>
/// <param name="SessionId">The Claude session the chat runs in (a GUID this app minted).</param>
/// <param name="Model">The <c>--model</c> word.</param>
/// <param name="Effort">The <c>--effort</c> word, or null for the CLI's own.</param>
/// <param name="WorkingDirectory">The folder the child starts in; the CLI keeps its per-folder state under it.</param>
/// <param name="SystemPrompt">The app's system prompt, whole.</param>
/// <param name="ToolNames">The offered tools' names, joined with commas in order: the MCP server lists them at the child's start.</param>
public sealed record ClaudeServerLaunch(string Executable, string SessionId, string Model, string? Effort, string WorkingDirectory, string SystemPrompt, string ToolNames);
