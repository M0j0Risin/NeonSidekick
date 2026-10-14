using System.Globalization;

namespace NeonSidekick.Claude;

/// <summary>The words of <c>/claude</c> (2026-09-27): the transcript's, the log's and the history's. Pinned where marked.</summary>
public static class ClaudeText
{
    /// <summary>The log category.</summary>
    public const string Category = "Claude";

    /// <summary>The name over a Claude reply in the transcript, as a <c>/botchat</c> speaker's is. Pinned.</summary>
    public const string SpeakerName = "Claude";

    /// <summary>The spinner's label while the child starts and Claude thinks (<c>asking Claude</c> until 2026-09-28, the user's call). Pinned.</summary>
    public const string AskingLabel = "claude";

    /// <summary>The spinner's label while Claude uses a tool: <c>Claude: Read</c>. Pinned.</summary>
    public static string ToolLabel(string name) => "Claude: " + name;

    /// <summary>A tool Claude used, dim in the transcript: <c>Claude › Read src/Foo.cs</c>, the detail left off when there is none. Pinned.</summary>
    public static string ToolNote(string name, string detail) =>
        "Claude › " + name + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail.ReplaceLineEndings(" ").Trim());

    /// <summary>A bare <c>/claude</c>. Pinned.</summary>
    public const string UsageError = "Usage: /claude <message> — or /claude new to start a new Claude conversation";

    /// <summary>The word that starts a new thread: <c>/claude new</c>. Pinned.</summary>
    public const string NewWord = "new";

    /// <summary>The note beside <see cref="NewWord"/> on <c>/claude</c>'s argument list (2026-10-04). Pinned.</summary>
    public const string NewNote = "start a new Claude conversation";

    /// <summary>After <c>/claude new</c>. Pinned.</summary>
    public const string NewThreadNotice = "The next /claude starts a new Claude conversation.";

    /// <summary>A <c>--resume</c> the CLI refused (the thread was deleted, or another machine's): the id dropped and one fresh try. Pinned.</summary>
    public const string ResumeLostNotice = "The Claude conversation could not be resumed; starting a new one.";

    /// <summary>The CLI is on no path this app looks at. Pinned.</summary>
    public static string NotFound => OperatingSystem.IsMacOS() ? MacNotFound : WindowsNotFound;

    /// <summary><see cref="NotFound"/> on Windows (and off macOS). Pinned.</summary>
    public const string WindowsNotFound = "Claude Code was not found on the PATH or in %USERPROFILE%\\.local\\bin. Install it, or set Claude Code executable on /settings › Servers › Anthropic.";

    /// <summary><see cref="NotFound"/> on macOS (2026-10-06): <c>ClaudeProcess</c> looks in <c>~/.local/bin</c> there. Pinned.</summary>
    public const string MacNotFound = "Claude Code was not found on the PATH or in ~/.local/bin. Install it, or set Claude Code executable on /settings › Servers › Anthropic.";

    /// <summary>The <c>Claude Code executable</c> setting names no file. Pinned.</summary>
    public static string ConfiguredNotFound(string path) => $"Claude Code executable '{path}' does not exist. Fix it on /settings › Servers › Anthropic, or clear it to look on the PATH.";

    /// <summary>The OS refused the start. Pinned.</summary>
    public static string CouldNotStart(string executable, string why) => $"Could not start Claude Code ({Path.GetFileName(executable)}): {why}";

    /// <summary>A failed run's line: <c>Claude failed: …</c>. Pinned.</summary>
    public static string Failed(string error) => "Claude failed: " + error;

    /// <summary>The child exited without a result line: its code and stderr's tail (or that it said nothing). Pinned.</summary>
    public static string NoResult(int exitCode, string stderr) =>
        $"exit code {exitCode.ToString(CultureInfo.InvariantCulture)}" + (string.IsNullOrWhiteSpace(stderr) ? ", nothing on stderr" : ": " + stderr.ReplaceLineEndings(" ").Trim());

    /// <summary>A failed result with neither errors nor text.</summary>
    public const string UnknownFailure = "unknown error";

    /// <summary>The tools the permission level turned away, once under the reply. Pinned.</summary>
    public static string DeniedNotice(IReadOnlyList<string> tools, string level) =>
        $"Claude was denied {string.Join(", ", tools.Distinct(StringComparer.Ordinal))} (Claude Code command permissions: {level}).";

    /// <summary>The reply's footer: <c>Claude · $0.0256 · 6,254 in · 5 out</c> (the Claude Code advisor tool's said its own name until 2026-10-10, when the tool was retired). Pinned.</summary>
    public static string Footer(decimal costUsd, Llm.TokenUsage usage) =>
        $"{SpeakerName} · {Dollars(costUsd)} · {usage.Input.ToString("N0", CultureInfo.InvariantCulture)} in · {usage.Output.ToString("N0", CultureInfo.InvariantCulture)} out";

    /// <summary>A cost as the footer and <c>/usage</c> say it: <c>$0.0256</c>, four places under a dollar, two from one up. Pinned.</summary>
    public static string Dollars(decimal usd) =>
        "$" + usd.ToString(usd < 1m ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// The user's side of a <c>/claude</c> exchange in the local model's history (2026-09-27, shared history, the
    /// user's pick): tagged, so the model reads it as a question put to someone else. Pinned.
    /// </summary>
    public static string HistoryUser(string prompt) => "[to Claude] " + prompt;

    /// <summary>Claude's side in the local model's history: tagged, so the model never takes it for its own words. Pinned.</summary>
    public static string HistoryReply(string reply) => "[Claude] " + reply;

    /// <summary>
    /// <see cref="HistoryUser"/> read back (2026-09-30, <c>/rewind</c>): the prompt of a history line that is a <c>/claude</c>
    /// message, so the line can return to the input as typed (<c>/claude &lt;prompt&gt;</c>). Null for any other line.
    /// </summary>
    public static string? HistoryUserPrompt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string tag = HistoryUser("");
        return text.StartsWith(tag, StringComparison.Ordinal) ? text[tag.Length..] : null;
    }

    /// <summary><see cref="HistoryReply"/> read back: Claude's words without the tag, the text unchanged when it carries none.</summary>
    public static string HistoryReplyText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string tag = HistoryReply("");
        return text.StartsWith(tag, StringComparison.Ordinal) ? text[tag.Length..] : text;
    }

    /// <summary>Whether a stored turn's line was a <c>/claude</c> message — a restore puts the speaker's name back over its reply.</summary>
    public static bool IsClaudeLine(string userText) =>
        userText.StartsWith("/claude ", StringComparison.OrdinalIgnoreCase) && !string.Equals(userText.Trim(), "/claude " + NewWord, StringComparison.OrdinalIgnoreCase);

    // ── the log ─────────────────────────────────────────────────────────────

    public static string StartedLog(int pid, ClaudeRequest request) =>
        $"Started pid {pid.ToString(CultureInfo.InvariantCulture)} ({ClaudePermission.Name(request.Permission)}, {(request.Resume ? "resume" : "new")} {request.SessionId}) in {request.WorkingDirectory}";

    public static string ExitedLog(int code) => "Exited " + code.ToString(CultureInfo.InvariantCulture);

    public const string KilledLog = "Killed (canceled)";

    public static string UnreadableLineLog(string why) => "A stream line that is not JSON was skipped: " + why;

    public static string ResultLog(ClaudeEvent.Result result) =>
        $"Result: {(result.IsError ? "error" : "ok")}, {Dollars(result.CostUsd)}, {result.Usage.Input.ToString(CultureInfo.InvariantCulture)} in, {result.Usage.Output.ToString(CultureInfo.InvariantCulture)} out"
        + (result.Denied.Count > 0 ? ", denied " + string.Join(",", result.Denied) : "");
}
