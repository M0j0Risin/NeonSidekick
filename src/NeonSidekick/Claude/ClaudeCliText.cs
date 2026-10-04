using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Claude;

/// <summary>The words of the Claude CLI server (2026-09-30): the transcript's, the model's and the log's. Pinned where marked.</summary>
public static class ClaudeCliText
{
    // ── the model's ──────────────────────────────────────────────────────────

    /// <summary>
    /// The text ahead of a user message that carries the turn's seeded tool answers (the clock, the working directory, the
    /// memory, a process that ended): the app ran them, the model did not, and the CLI is sent no call for them. Pinned.
    /// </summary>
    public static string SeededContext(IReadOnlyList<string> lines) =>
        "(From the app, not typed by the user: what these tools answered at the start of this turn.)\n" + string.Join("\n", lines);

    /// <summary>One seeded answer: <c>get_time: …</c>. Pinned.</summary>
    public static string SeededLine(string tool, string result) => tool + ": " + result.Trim();

    /// <summary>
    /// The system prompt's last line when the turn offers tools: the rules above name them bare (<c>read_file</c>), the CLI
    /// shows them prefixed. Pinned.
    /// </summary>
    public const string ToolNamesNote =
        "Your tools are this app's, reached through its MCP server: each appears to you as " + ClaudeArguments.McpToolPrefix + "<name>, and the instructions above call it by <name> alone.";

    /// <summary>The opening line of a session that starts over with the conversation so far. Pinned.</summary>
    public const string PreambleHeader = "(The conversation so far, from before this session — for context, not a new request:)";

    /// <summary>A user's line in the preamble. Pinned.</summary>
    public static string PreambleUser(string text) => "User: " + text.Trim();

    /// <summary>The assistant's line in the preamble. Pinned.</summary>
    public static string PreambleAssistant(string text) => "Assistant: " + text.Trim();

    /// <summary>
    /// A request asked beside the chat, flattened for a one-shot <c>claude -p</c>'s stdin: a lone user message as it is,
    /// else each message on its own as <c>User:</c> / <c>Assistant:</c> / <c>Tool result:</c>. Pinned.
    /// </summary>
    public static string Flatten(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages is [{ Role: var role } only] && role == ChatRole.User)
        {
            return only.Text;
        }

        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            string text = message.Text;
            if (message.Role == ChatRole.Tool || message.Contents.Any(c => c is FunctionResultContent))
            {
                text = string.Join("\n", message.Contents.OfType<FunctionResultContent>().Select(ClaudeServerInput.ResultText));
                Append(sb, "Tool result: ", text);
            }
            else if (message.Role == ChatRole.Assistant)
            {
                Append(sb, "Assistant: ", text);
            }
            else
            {
                Append(sb, "User: ", text);
            }
        }

        return sb.ToString().TrimEnd();

        static void Append(StringBuilder sb, string label, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.Append(label).Append(text.Trim()).Append("\n\n");
            }
        }
    }

    /// <summary>An MCP call answered when the turn it belonged to ended first (an interrupt, a failure). Pinned.</summary>
    public const string CallCancelled = "Error: the turn was cancelled before this tool ran.";

    /// <summary>A call the loop gave no result for. Pinned.</summary>
    public const string NoResultError = "Error: the app returned no result for this call.";

    /// <summary>An MCP call that arrives with no turn to answer it. Pinned.</summary>
    public const string NoTurnError = "Error: no turn is waiting for this call.";

    /// <summary>An MCP call that could not be paired with a call of the stream. Pinned.</summary>
    public const string UnmatchedCallError = "Error: this call could not be matched to the model's turn.";

    // ── the transcript's ─────────────────────────────────────────────────────

    /// <summary>The CLI stopped mid-turn: its exit code and stderr's tail. Pinned.</summary>
    public static string EndedError(int? exitCode, string stderr) =>
        "the Claude CLI ended" + (exitCode is { } code ? " with exit code " + code.ToString(CultureInfo.InvariantCulture) : "")
        + (string.IsNullOrWhiteSpace(stderr) ? "" : ": " + stderr.ReplaceLineEndings(" ").Trim());

    /// <summary>A write to a CLI that is not running. Pinned.</summary>
    public const string NotRunningError = "the Claude CLI is not running";

    /// <summary>A response past <c>LLM request timeout</c>. Pinned.</summary>
    public static string RequestTimedOut(TimeSpan timeout) =>
        "the Claude CLI did not answer within " + Llm.LlmTimeouts.Format(timeout);

    /// <summary>The app's own path is unknown (no relay to name). Pinned.</summary>
    public const string NoOwnExecutable = "the app's own executable path is unknown, so the Claude CLI server has no way to reach its tools";

    /// <summary><c>/server claude-cli</c> while the Claude CLI server is off or the CLI is missing. Pinned.</summary>
    public const string NotOfferedError = "The Claude CLI server is not offered: turn on Claude CLI server on the Anthropic tab of /settings, and install Claude Code (or set Claude CLI executable).";

    /// <summary>A saved Claude CLI URL the switch or a missing CLI turns off: logged at connect, a blank URL's discovery follows. Pinned.</summary>
    public const string NotOfferedWarning = "The LLM URL names the Claude CLI server, which is not offered (Claude CLI server off, or Claude Code not found); finding a server instead.";

    /// <summary>The <c>/server</c> row's detail: the model words it takes. Pinned.</summary>
    public const string RowDetail = "Claude Code, the app's tools over MCP";

    // ── the relay's (stderr, which the CLI logs) ─────────────────────────────

    public static string RelayBadAddress(string address) => $"neonsidekick relay: '{address}' is no host:port";

    public static string RelayCouldNotConnect(string address, string why) => $"neonsidekick relay: could not connect to {address}: {why}";

    // ── the log ──────────────────────────────────────────────────────────────

    public static string StartedLog(int pid, ClaudeServerLaunch launch, bool resume, int tools) =>
        $"Claude CLI server: started pid {pid.ToString(CultureInfo.InvariantCulture)} ({(resume ? "resume" : "new")} {launch.SessionId}, {ClaudeServerHost.Describe(launch)}, {tools.ToString(CultureInfo.InvariantCulture)} tools) in {launch.WorkingDirectory}";

    public static string EndedLog(int? code) =>
        "Claude CLI server: ended" + (code is { } exit ? " (exit code " + exit.ToString(CultureInfo.InvariantCulture) + ")" : "");

    public const string StoppedLog = "Claude CLI server: stopped";

    public const string InterruptedLog = "Claude CLI server: turn interrupted";

    public const string StoppedMidTurnLog = "Claude CLI server: a turn left mid-way did not end on an interrupt; stopping the CLI (the next turn resumes the session)";

    public static string SessionLostLog(string id) => $"Claude CLI server: session {id} could not be resumed; starting it over with the conversation so far";

    public static string SessionInUseLog(string id) => $"Claude CLI server: session {id} exists already; resuming it";

    /// <summary>What the CLI says when <c>--resume</c> names a session it does not have (the same words <c>/claude</c> reads).</summary>
    public const string SessionLostMarker = "No conversation found";

    /// <summary>What the CLI says when <c>--session-id</c> names a session it has.</summary>
    public const string SessionInUseMarker = "already in use";

    public static string TurnLog(ClaudeServerEvent.Result result, decimal cost) =>
        $"Claude CLI server: turn {(result.IsError ? "ended with " + (result.TerminalReason ?? "an error") : "done")}, {ClaudeText.Dollars(cost)}";

    public static string ForeignToolsLog(IEnumerable<string> tools) =>
        "Claude CLI server: the CLI offers tools that are not the app's: " + string.Join(", ", tools.Where(t => !t.StartsWith(ClaudeArguments.McpToolPrefix, StringComparison.Ordinal)));

    public static string UnmatchedCallLog(string name) => $"Claude CLI server: an MCP call to {name} matched no call of the stream";

    public static string ToolNameTooLongLog(string name) =>
        $"Claude CLI server: {name} is left off the MCP list: with {ClaudeArguments.McpToolPrefix} its name passes {ClaudeMcpServer.MaxToolNameLength.ToString(CultureInfo.InvariantCulture)} characters";

    public const string RelayRefusedLog = "Claude CLI server: a connection to the MCP listener sent no valid key; closed";

    public const string RelayConnectedLog = "Claude CLI server: the MCP relay connected";

    public static string McpServerFailedLog(string why) => "Claude CLI server: the MCP server failed: " + why;

    public static string OneShotStartedLog(int pid, string model) =>
        $"Claude CLI server: one-shot request, pid {pid.ToString(CultureInfo.InvariantCulture)}, model {model}";
}
