using System.Globalization;
using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// Every sentence the shell tools, the runner, the gate and the approval pane show or return
/// (2026-09-21): pure statics, pinned by <c>ShellTextTests</c>, the <see cref="Git.GitText"/> shape.
/// A result's first line is its header — <c>exit 0 in 1.2 s (powershell): git status</c> — and the
/// transcript shows that line alone (<see cref="Note"/>); the output follows, stderr under its own
/// separator, and a result over the cap keeps its head and tail with the cut named between them.
/// Every refusal starts with <c>Error:</c>, as the loop expects.
/// </summary>
public static class ShellText
{
    public const string NoOutput = "(no output)";
    public const string StderrSeparator = "--- stderr ---";
    public const string CutSuffix = " — output cut";

    /// <summary>
    /// The folder under the working directory a cut result's whole output is written to: <c>.neon\shell</c> (<c>.neon/shell</c> off Windows) since
    /// 2026-10-21, the user's ask — <c>.shell</c> until then, an app folder of its own beside <c>.neon</c>, which already held the plans and audits.
    /// Inside the app's folder it is also out of the Ralph loop's evidence and the code audit's walk, which skip <c>.neon</c>. An old <c>.shell</c>
    /// folder is left as it is. A field, not a constant: the separator is the platform's, as in every result's path.
    /// </summary>
    public static readonly string SpillFolderName = Path.Combine(Plans.PlanSlug.AppFolder, "shell");

    /// <summary>The share of the cap the head of a cut result keeps; the tail gets the rest.</summary>
    public const double HeadShare = 0.6;

    // ── Headers ──────────────────────────────────────────────────────────────

    /// <summary><c>exit 0 in 1.2 s (powershell): git status</c>; <see cref="Result"/> adds <see cref="CutSuffix"/> when the output was cut. Pinned.</summary>
    public static string ExitHeader(string kind, int exitCode, TimeSpan elapsed, string label) =>
        $"exit {N(exitCode)} in {Elapsed(elapsed)} ({kind}): {label}";

    /// <summary><c>timed out after 180.0 s (powershell, killed): sleep 999</c>. Pinned.</summary>
    public static string TimedOutHeader(string kind, TimeSpan timeout, string label) =>
        $"timed out after {Elapsed(timeout)} ({kind}, killed): {label}";

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>
    /// A whole result: the header, then the output — stdout as it came, then <see cref="StderrSeparator"/>
    /// and stderr when there was any, <see cref="NoOutput"/> for neither. Over <paramref name="maxChars"/>
    /// the text keeps its head (<see cref="HeadShare"/>) and tail, cut at line ends, with
    /// <see cref="CutLine"/> between; <paramref name="spill"/> is the relative path the whole text
    /// went to (null when it could not be written).
    /// </summary>
    /// <param name="dropped">The oldest lines the buffer no longer keeps (2026-10-11, the review): over 0, the output opens with
    /// <see cref="DroppedLine"/> and a cut says the spill holds the output kept, not the whole.</param>
    public static string Result(string header, IReadOnlyList<OutputLine> lines, int maxChars, string? spill, long dropped = 0)
    {
        ArgumentNullException.ThrowIfNull(header);
        string body = Body(lines, dropped);
        if (body.Length == 0)
        {
            return header + "\n" + NoOutput;
        }

        if (body.Length <= maxChars)
        {
            return header + "\n" + body;
        }

        return header + CutSuffix + "\n" + Cut(body, maxChars, spill, whole: dropped == 0);
    }

    /// <summary><see cref="Body(IReadOnlyList{OutputLine})"/> after <see cref="DroppedLine"/> when the buffer dropped its oldest lines: what the spill file gets too.</summary>
    public static string Body(IReadOnlyList<OutputLine> lines, long dropped) =>
        dropped > 0 ? DroppedLine(dropped) + "\n" + Body(lines) : Body(lines);

    /// <summary>
    /// <c>… (the first 15,000 lines were not kept) …</c>: the output's first line when the buffer (<see cref="OutputBuffer"/>, the last
    /// 5,000 lines within 2 MB) dropped the oldest (2026-10-11, the review: a long run's spill claimed to be the whole). Pinned.
    /// </summary>
    public static string DroppedLine(long dropped) => $"… (the first {Count(dropped)} lines were not kept) …";

    /// <summary>The output as one text: stdout lines, then the separator and the stderr lines when there are any. Pinned.</summary>
    public static string Body(IReadOnlyList<OutputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        foreach (var line in lines)
        {
            (line.IsError ? stderr : stdout).Append(line.Text).Append('\n');
        }

        if (stderr.Length == 0)
        {
            return stdout.ToString().TrimEnd('\n');
        }

        return (stdout.Length == 0 ? "" : stdout.ToString() + "\n") + StderrSeparator + "\n" + stderr.ToString().TrimEnd('\n');
    }

    /// <summary>The head and the tail of <paramref name="text"/> within <paramref name="maxChars"/>, cut at line ends, <see cref="CutLine"/> between them.</summary>
    /// <summary>The model's sentence for the trusted files' tripwire (2026-10-22, the fix plan's D8). Prompt text.</summary>
    public const string TrustedPoliced =
        "Error: refused by the shell police: the command names .git, .neon, the project file or the app's own folder, which only the app's tools change (the git tools, write_file with the user's yes for the project file); use those";

    /// <summary>The transcript's line for it: the word that tripped it, and the script it was in.</summary>
    public static string TrustedShown(string token, string? file) =>
        "refused: names " + token + (file is null ? "" : " (in " + file + ")") + ", a file only the app's tools change";

    public static string TrustedLogLine(CommandRequest request, string token, string? file) =>
        $"police: trusted files ('{token}'{(file is null ? "" : " in " + file)}) — {request.Kind} {Quote(request.Command)}";

    /// <summary>A line that runs a script that isn't there yet (2026-10-22, review L4). Prompt text.</summary>
    public static string ScriptMissing(string name) =>
        $"Error: refused by the shell police: {name} isn't there for the police to read before it runs; write it with write_file first, then run it in another call";

    public static string ScriptMissingLogLine(CommandRequest request, string name) =>
        $"police: script not there yet ('{name}') — {request.Kind} {Quote(request.Command)}";

    /// <summary>A bridge connection past every slot (2026-10-22, review F7). Prompt text.</summary>
    public const string BridgeBusy = "Error: the tool bridge is busy with as many calls as it takes at once; try the call again";

    public static string Cut(string text, int maxChars, string? spill, bool whole = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        int headChars = (int)(maxChars * HeadShare);
        int tailChars = maxChars - headChars;
        // Neither end inside a surrogate pair (2026-10-22, review L6).
        string head = Files.TextCut.Safe(text, Math.Min(headChars, text.Length));
        int lastBreak = head.LastIndexOf('\n');
        if (lastBreak > 0)
        {
            head = head[..lastBreak];
        }

        string tail = text[Files.TextCut.SafeStart(text, Math.Max(0, text.Length - tailChars))..];
        int firstBreak = tail.IndexOf('\n');
        if (firstBreak >= 0 && firstBreak < tail.Length - 1)
        {
            tail = tail[(firstBreak + 1)..];
        }

        int removed = text.Length - head.Length - tail.Length;
        return head + "\n" + CutLine(removed, spill, whole) + "\n" + tail;
    }

    /// <summary>
    /// <c>… (412,345 chars cut; the whole output is in .neon\shell\run_9c04e1.log) …</c>, or without the file when none could be written;
    /// <c>the output kept is in</c> when the buffer had dropped lines (<paramref name="whole"/> false, 2026-10-11). Pinned.
    /// </summary>
    public static string CutLine(int chars, string? spill, bool whole = true) =>
        spill is null
            ? $"… ({Count(chars)} characters cut) …"
            : $"… ({Count(chars)} characters cut; the {(whole ? "whole output" : "output kept")} is in {spill}) …";

    // ── Background processes (phase B, 2026-09-21) ───────────────────────────

    /// <summary><c>started proc_3f2a1b (powershell, pid 1234): npm run dev</c>, then the poll hint; with notify, the second line says the exit will be told. Pinned.</summary>
    public static string Started(string id, string kind, int pid, string label, bool notify) =>
        $"started {id} ({kind}, pid {N(pid)}): {label}\n" + PollHint(id, notify);

    /// <summary><c>started proc_… in the background (timeout 900 s is over the 600 s foreground cap): …</c>. Pinned.</summary>
    public static string Promoted(string id, string kind, int pid, string label, int timeout, int cap, bool notify) =>
        $"started {id} in the background (timeout {N(timeout)} s is over the {N(cap)} s foreground cap; {kind}, pid {N(pid)}): {label}\n" + PollHint(id, notify);

    public static string PollHint(string id, bool notify) =>
        $"poll it with process(action: \"poll\", session_id: \"{id}\")" + (notify ? "; you will be told when it exits." : ".");

    /// <summary><c>2 processes (1 running)</c>, <c>0 processes</c>. Pinned.</summary>
    public static string ListHeader(int count, int running) =>
        count == 0 ? "0 processes" : $"{N(count)} {(count == 1 ? "process" : "processes")} ({N(running)} running)";

    /// <summary>One row of <c>list</c>: the id, <see cref="ModelState"/>, the elapsed, the kind, the command. Pinned.</summary>
    public static string ListRow(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SessionRow(session, ModelState(session), 9, session.Label);
    }

    /// <summary>
    /// A session's state in the model's words: <c>running</c>, <c>exit N</c>, or <see cref="StoppedByUserWords"/> once the user stopped
    /// it (2026-10-05, the code review: <c>list</c> and <c>log</c> said <c>exit 1</c> after the poll had said "stopped by the user", and
    /// a model reading a crash there may start again what the user stopped on purpose). Pinned.
    /// </summary>
    public static string ModelState(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return !session.HasExited ? "running" : session.StoppedByUser ? StoppedByUserWords : "exit " + N(session.ExitCode ?? -1);
    }

    /// <summary>
    /// One session as a row, the model's <c>list</c> (<see cref="ListRow"/>) and <c>/process</c>'s (<c>ProcessWindowText.Row</c>) alike
    /// (2026-10-05, the code review: two copies of it had drifted): the id, <paramref name="state"/> padded to
    /// <paramref name="stateCells"/>, the elapsed, the kind and <paramref name="label"/>.
    /// </summary>
    public static string SessionRow(ProcessSession session, string state, int stateCells, string label)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(state);
        return $"{session.Id}  {state.PadRight(stateCells)} {Elapsed(session.Elapsed),-9} {session.Kind,-10} {label}";
    }

    /// <summary>
    /// <c>proc_3f2a1b running for 3 m 12 s (powershell): npm run dev — 14 new lines</c> / <c>proc_… exited 0 after 34.2 s (cmd): … — no new output</c>;
    /// <c>proc_… stopped by the user after 2 m 5 s (cmd): …</c> once the user stopped it from <c>/process</c>'s window (2026-10-05). Pinned.
    /// </summary>
    public static string PollHeader(ProcessSession session, int newLines)
    {
        ArgumentNullException.ThrowIfNull(session);
        string state = !session.HasExited
            ? $"running for {Elapsed(session.Elapsed)}"
            : session.StoppedByUser
                ? $"{StoppedByUserWords} after {Elapsed(session.Elapsed)}"
                : $"exited {N(session.ExitCode ?? -1)} after {Elapsed(session.Elapsed)}";
        string fresh = newLines == 0 ? "no new output" : $"{Count(newLines)} new {(newLines == 1 ? "line" : "lines")}";
        return $"{session.Id} {state} ({session.Kind}): {session.Label} — {fresh}";
    }

    /// <summary><c>proc_… running for 3.2 s, 'listening' seen (cmd): … — 5 new lines</c>: <c>wait</c>'s <c>until</c> found its text (2026-10-12). Pinned.</summary>
    public static string Matched(ProcessSession session, string until, int newLines)
    {
        ArgumentNullException.ThrowIfNull(session);
        string fresh = newLines == 0 ? "no new output" : $"{Count(newLines)} new {(newLines == 1 ? "line" : "lines")}";
        return $"{session.Id} running for {Elapsed(session.Elapsed)}, '{until}' seen ({session.Kind}): {session.Label} — {fresh}";
    }

    /// <summary><c>proc_… still running after 60 s (powershell): … — 5 new lines</c>. Pinned.</summary>
    public static string StillRunning(ProcessSession session, int waited, int newLines)
    {
        ArgumentNullException.ThrowIfNull(session);
        string fresh = newLines == 0 ? "no new output" : $"{Count(newLines)} new {(newLines == 1 ? "line" : "lines")}";
        return $"{session.Id} still running after {N(waited)} s ({session.Kind}): {session.Label} — {fresh}";
    }

    /// <summary><c>proc_… lines 1,201-1,400 of 1,400 (running): …</c>, <c>proc_… no lines (running): …</c>. Pinned.</summary>
    public static string LogHeader(ProcessSession session, long first, long last)
    {
        ArgumentNullException.ThrowIfNull(session);
        string state = ModelState(session);
        string range = first == 0 ? "no lines" : $"lines {Count(first)}-{Count(last)} of {Count(session.Output.TotalLines)}";
        return $"{session.Id} {range} ({state}): {session.Label}";
    }

    /// <summary>Under a <c>log</c> header when older lines were dropped: <c>(lines 1-800 are gone: the log keeps the last 5,000)</c>. Pinned.</summary>
    public static string LogGone(long firstKept, int kept) =>
        $"(lines 1-{Count(firstKept - 1)} are gone: the log keeps the last {Count(kept)})";

    /// <summary><c>killed proc_3f2a1b (powershell, pid 1234) after 5 m 2 s: npm run dev</c>. Pinned.</summary>
    public static string KilledLine(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return $"killed {session.Id} ({session.Kind}, pid {N(session.Pid)}) after {Elapsed(session.Elapsed)}: {session.Label}";
    }

    /// <summary><c>sent 12 chars to proc_…</c> / <c>sent a line to proc_…</c>. Pinned.</summary>
    public static string Sent(string id, int chars, bool line) => line ? $"sent a line to {id}" : $"sent {Count(chars)} characters to {id}";

    /// <summary><c>closed proc_3c9d00 (exit 0, 1,400 lines forgotten)</c>. Pinned.</summary>
    public static string Closed(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return $"closed {session.Id} (exit {N(session.ExitCode ?? -1)}, {Count(session.Output.TotalLines)} {(session.Output.TotalLines == 1 ? "line" : "lines")} forgotten)";
    }

    /// <summary>
    /// The transcript's alert when a notified process exits: <c>proc_3f2a1b exited 0 after 34.2 s: npm test</c>; <c>… was stopped by you
    /// after …</c> when the user stopped it from <c>/process</c>'s window (2026-10-05). Pinned.
    /// </summary>
    public static string AlertLine(ProcessAlert alert) =>
        $"{alert.Id} {(alert.ByUser ? "was stopped by you" : alert.Killed ? "was killed" : "exited " + N(alert.ExitCode))} after {Elapsed(alert.Elapsed)}: {alert.Label}";

    /// <summary>The model's words for a process the user stopped (2026-10-05, <see cref="PollHeader"/>). Pinned.</summary>
    public const string StoppedByUserWords = "stopped by the user";

    /// <summary>The lines of a poll or a log as one text, oldest first, empty for none.</summary>
    public static string Lines(IReadOnlyList<OutputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join("\n", lines.Select(l => l.Text));
    }

    /// <summary>A background result: the header, then the lines within <paramref name="maxChars"/> (the head and the tail, no spill), <see cref="NoOutput"/> for none.</summary>
    public static string Background(string header, IReadOnlyList<OutputLine> lines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(header);
        string body = Lines(lines);
        if (body.Length == 0)
        {
            return header + "\n" + NoOutput;
        }

        return body.Length <= maxChars ? header + "\n" + body : header + CutSuffix + "\n" + Cut(body, maxChars, null);
    }

    public static string NoProcess(string prefix) => $"Error: no process matches '{prefix}'";
    public static string Ambiguous(string prefix, IReadOnlyList<string> matches) => $"Error: '{prefix}' matches {N(matches.Count)} processes: {string.Join(", ", matches)}";
    public static string HasExited(string id, string action) => $"Error: {id} has exited; {action} needs a running process";
    public static string StillRunningError(string id) => $"Error: {id} is still running; kill it first";
    public static string TooManyProcesses(int max) => $"Error: too many background processes ({N(max)}); kill or close one";
    public const string SessionRequired = "Error: session_id is required for every action but list";
    public const string DataRequired = "Error: data is required for write and submit";
    public static string BadAction(string action, string choices) => $"Error: '{action}' is not one of {choices} for 'action'";
    public static string BadWait(int min, int max) => $"Error: timeout must be {N(min)} to {N(max)} for wait";
    public static string BadLimit(int min, int max) => $"Error: limit must be {N(min)} to {N(max)}";
    public const string BadOffset = "Error: offset must be 1 or more";
    public const string BackgroundNotFromScript = "Error: background is not available from a script";

    // ── Scripts (phase C, 2026-09-21) ────────────────────────────────────────

    /// <summary><c>exit 0 in 2.3 s (python, 3 tool calls): the first line</c>; <see cref="Result"/> adds the cut suffix. With <paramref name="toolCalls"/> null (the bridge off, later on 2026-09-21) the clause is left out: <c>exit 0 in 2.3 s (python): …</c>. Pinned.</summary>
    public static string ScriptExitHeader(string language, int exitCode, TimeSpan elapsed, int? toolCalls, string firstLine) =>
        $"exit {N(exitCode)} in {Elapsed(elapsed)} ({language}{ToolCallsClause(toolCalls)}): {firstLine}";

    /// <summary><c>timed out after 5 m 0 s (python, killed, 12 tool calls): …</c>; <c>(python, killed): …</c> with the bridge off. Pinned.</summary>
    public static string ScriptTimedOutHeader(string language, TimeSpan timeout, int? toolCalls, string firstLine) =>
        $"timed out after {Elapsed(timeout)} ({language}, killed{ToolCallsClause(toolCalls)}): {firstLine}";

    public static string ToolCalls(int count) => $"{Count(count)} tool {(count == 1 ? "call" : "calls")}";

    /// <summary><c>, 3 tool calls</c>, or nothing for null: the headers' and the log line's optional clause.</summary>
    private static string ToolCallsClause(int? count) => count is { } n ? ", " + ToolCalls(n) : "";

    /// <summary>The script's first non-blank line, trimmed, for the header and the log; <c>(empty)</c> for none.</summary>
    public static string FirstLine(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        foreach (string line in code.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return "(empty)";
    }

    /// <summary>The pseudo-prefix a script's approval is recorded under: <c>code:python</c>.</summary>
    public static string ScriptPrefix(string language) => "code:" + language;

    public const string CodeRequired = "Error: code is required";
    public static string LanguageNotInstalled(CodeLanguage language) => $"Error: {CodeLanguages.Name(language)} is not installed (no {CodeLanguages.FileName(language)} found)";
    public static string LanguageNotEnabled(string language) => $"Error: {language} is not enabled; the user can tick it in Shell code languages on the Shell tab of /tools";
    public static string UnknownTool(string tool) => $"Error: unknown tool {tool}";
    public static string ToolCallLimit(int max) => $"Error: this run's tool call limit ({N(max)}) is reached";
    public const string BadToken = "Error: the bridge token did not match";

    /// <summary><c>Error: the request is over 32 MB</c>: a bridge request past <see cref="Bridge.BridgeServer.MaxRequestBytes"/> (2026-10-12). Pinned.</summary>
    public static string RequestTooLarge(int maxBytes) =>
        maxBytes >= 1024 * 1024 ? $"Error: the request is over {N(maxBytes / (1024 * 1024))} MB" : $"Error: the request is over {N(maxBytes)} bytes";
    public const string BadRequest = "Error: the request is not {\"token\", \"tool\", \"arguments\"} on one line";
    public static string CouldNotWriteScript(string detail) => $"Error: could not write the script ({detail})";

    /// <summary><c>execute_code: python "import os" → exit 0 in 2.3 s, 3 tool calls (2,340 chars)</c>; no tool-call clause with the bridge off.</summary>
    public static string ScriptLogLine(string language, string firstLine, string outcome, int? toolCalls, long chars) =>
        $"execute_code: {language} {Quote(firstLine)} → {outcome}{ToolCallsClause(toolCalls)} ({Count(chars)} chars)";

    /// <summary><c>bridge: read_file → 1,234 chars</c> / <c>bridge: nope → Error: unknown tool nope</c>.</summary>
    public static string BridgeLogLine(string tool, string result) =>
        $"bridge: {tool} → " + (result.StartsWith("Error:", StringComparison.Ordinal) ? result.ReplaceLineEndings(" ") : Count(result.Length) + " chars");

    // ── Errors (every one starts with Error:) ────────────────────────────────

    public const string CommandRequired = "Error: command is required";
    public const string PolicyOff = "Error: Shell command policy is off: no command runs";

    public static string Denied(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.IsScript
            ? $"Error: the script was denied by the user ({request.Kind}); do not retry it or work around the refusal"
            : $"Error: the command was denied by the user: {request.Command}; do not retry it or work around the refusal";
    }

    /// <summary>
    /// Under <c>ask</c> with no screen to ask on (headless, the pane off): the setting, the flag and the variable,
    /// and what is allowed. Pinned. Since 2026-09-26 it ends as <see cref="Denied"/> does (the user's call): told
    /// only that a command was refused, a headless model reached for another command doing the same job, burning
    /// tool calls and sometimes reporting a half-done job as done. The allowed prefixes stay, so a job an allowed
    /// command really covers can still be done.
    /// </summary>
    public static string NotAskable(IReadOnlyList<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        string list = allowed.Count == 0 ? "none" : string.Join(", ", allowed);
        return $"Error: the command was not run: there was no screen to ask the user on (Shell command policy is ask; {App.SidekickOptions.YoloFlag}, {Settings.EnvironmentOverrides.CommandPolicyVariable}=yolo or the profile's Shell allowed commands would let it run). Allowed prefixes: {list}. Do not retry it or work around the refusal; tell the user what could not run";
    }

    /// <summary>
    /// The notice a headless run ends with when anything was refused (2026-09-26), beside exit code 3:
    /// <c>2 commands were not run: "npm install", "choco upgrade"</c>, each once, in the order first refused. Pinned.
    /// </summary>
    public static string RefusedSummary(IReadOnlyList<string> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        var distinct = refused.Distinct(StringComparer.Ordinal).ToList();
        string noun = distinct.Count == 1 ? "command was" : "commands were";
        return $"{N(distinct.Count)} {noun} not run: {string.Join(", ", distinct.Select(Quote))}";
    }

    /// <summary>
    /// What a result the police refused opens with (<see cref="PathPolice"/>, 2026-09-22); the transcript keys its
    /// 👮 line on it (<see cref="IsOutside"/>). The sentence names the token and the rule, never the setting: told a
    /// switch is on, the model reasons about a switch it cannot reach (the File safe edits lesson).
    /// </summary>
    public const string OutsideHead = "Error: outside the working directory: ";

    /// <summary><c>Error: outside the working directory: 'C:\Windows\win.ini' — a command or a script may only name paths under it</c>. Pinned.</summary>
    public static string OutsidePath(string token) => OutsideHead + $"'{token}' — a command or a script may only name paths under it";

    /// <summary>Whether <paramref name="result"/> is the outside-paths police's refusal.</summary>
    public static bool IsOutside(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.StartsWith(OutsideHead, StringComparison.Ordinal);
    }

    /// <summary>What a result the forbidden-strings police refused opens with (<see cref="ForbiddenStrings"/>, 2026-10-03).</summary>
    public const string ForbiddenHead = "Error: forbidden by the shell police";

    /// <summary>
    /// What the model is told when a <c>Shell police forbidden strings</c> entry tripped (2026-10-03): refused, by the user, and
    /// not to be worked around — never the string (the user's call: a named string is one to spell around), never the setting.
    /// The user reads the string on the 👮 line (<see cref="ForbiddenShown"/>). Pinned.
    /// </summary>
    public const string Forbidden = ForbiddenHead + " — the user does not allow this command or script. Do not try another way to do the same thing; tell the user it was refused.";

    /// <summary>The 👮 line the user reads for <see cref="Forbidden"/>, the string named: <c>forbidden string 'rm -rf' — not run</c>. Never the model's. Pinned.</summary>
    public static string ForbiddenShown(string entry) => $"forbidden string '{entry}' — not run";

    /// <summary>What a result opens with when a script file the line runs is too big for the police to read (later on 2026-10-19, the third shell review).</summary>
    public const string ScriptTooLargeHead = "Error: refused by the shell police — script too large";

    /// <summary>What the model is told for <see cref="SqlitePolice.OversizedScript"/>: the limit, and that a smaller script runs. Pinned.</summary>
    public static string ScriptTooLarge(long maxBytes) => string.Create(CultureInfo.InvariantCulture,
        $"{ScriptTooLargeHead}: a script file this command runs is over {maxBytes / 1_000_000} MB, more than the shell police reads while the database or admin tools are on. Run a smaller script; do not pad or split one to get past this.");

    /// <summary>The 👮 line the user reads for <see cref="ScriptTooLarge"/>: <c>script big.py too large to check — not run</c>. Pinned.</summary>
    public static string ScriptTooLargeShown(string file) => $"script {file} too large to check — not run";

    /// <summary><c>police: script too large (big.py) — powershell "python big.py"</c>.</summary>
    public static string ScriptTooLargeLogLine(CommandRequest request, string file)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: script too large ({file}) — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary>What a result the SQLite police refused opens with (<see cref="SqlitePolice"/>, 2026-10-05).</summary>
    public const string SqliteHead = "Error: refused by the shell police — SQLite";

    /// <summary>
    /// What the model is told when the SQLite police tripped (2026-10-05, the user's call): while the SQLite tools are on, a
    /// database is theirs alone, and no other way is to be tried. Pinned.
    /// </summary>
    public const string SqlitePoliced = SqliteHead + ": while the SQLite tools are on, a SQLite database is reached only through them — " +
        "sqlite_query to read, and sqlite_execute to change one when SQLite mode allows it. Do not try another way (a script, another driver, " +
        "the file tools); tell the user if a change is needed that the tools refuse. Installing or removing a package is allowed: run the package command on its own.";

    /// <summary>The 👮 line the user reads for <see cref="SqlitePoliced"/>: <c>SQLite: 'sqlite3' in insert.py — not run</c>. Pinned.</summary>
    public static string SqliteShown(string token, string? file) =>
        file is null ? $"SQLite: '{token}' — not run" : $"SQLite: '{token}' in {file} — not run";

    /// <summary><c>police: sqlite ('sqlite3' in insert.py) — powershell "python insert.py"</c>: the SQLite police's line, before the gate is asked (2026-10-05).</summary>
    public static string SqliteLogLine(CommandRequest request, string token, string? file)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: sqlite ('{token}'{(file is null ? "" : " in " + file)}) — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary>What a result the server-database police refused opens with (<see cref="ServerDatabasePolice"/>, 2026-10-05).</summary>
    public const string ServerDatabaseHead = "Error: refused by the shell police — database";

    /// <summary>
    /// What the model is told when the server-database police tripped (2026-10-05, the user's call): while a family's tools are on,
    /// its databases are theirs alone, and no other way is to be tried. Pinned.
    /// </summary>
    public static string ServerDatabasePoliced(ServerDatabaseGuard.Family family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return $"{ServerDatabaseHead}: while the {family.Title} tools are on, a {family.Title} database is reached only through them — " +
            $"{family.QueryTool} to read, and {family.ExecuteTool} to change one when its mode allows it. Do not try another way (a client, a script, " +
            "another driver); tell the user if a change is needed that the tools refuse. Installing or removing a package is allowed: run the package command on its own.";
    }

    /// <summary>The 👮 line the user reads for <see cref="ServerDatabasePoliced"/>: <c>PostgreSQL: 'psql' in load.py — not run</c>. Pinned.</summary>
    public static string ServerDatabaseShown(string family, string token, string? file) =>
        file is null ? $"{family}: '{token}' — not run" : $"{family}: '{token}' in {file} — not run";

    /// <summary><c>police: PostgreSQL ('psql' in load.py) — powershell "python load.py"</c>: the server-database police's line, before the gate is asked.</summary>
    public static string ServerDatabaseLogLine(CommandRequest request, string family, string token, string? file)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: {family} ('{token}'{(file is null ? "" : " in " + file)}) — {request.Kind} {Quote(request.Command)}";
    }

    // ── The admin police (2026-10-15, the admin-tools plan) ──────────────────

    /// <summary>
    /// What changes a service from a shell or a script (<see cref="AdminPolice"/>): <c>sc</c> with a changing verb (a server name allowed before
    /// it), <c>net start|stop|pause|continue</c>, PowerShell's service cmdlets that change one. Since 2026-10-20 (a review): a quoted <c>sc.exe</c>
    /// or <c>net.exe</c> path, a ServiceController's own methods on a service read into a variable or a line, WMI's and the Win32 API's service
    /// calls (<c>Invoke-CimMethod … StopService</c>, <c>wmic service … call</c>, <c>ControlService</c>, <c>win32serviceutil.StopService</c>), and a
    /// write to a service's registry key. Regular expressions, case-blind. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> AdminServiceWords =
    [
        @"\bsc(?:\.exe)?[""']?\s+(?:\\\\\S+\s+)?(?:config|start|stop|delete|create|failure|failureflag|pause|continue|control|sdset|privs|sidtype)\b",
        @"\bnet1?(?:\.exe)?[""']?\s+(?:start|stop|pause|continue)\s+\S",
        @"\b(?:Start|Stop|Restart|Set|New|Remove|Suspend|Resume)-Service\b",
        @"\b(?:Get-Service|gsv|ServiceController)\b[^\r\n;]*\.(?:Start|Stop|Pause|Continue|ExecuteCommand)\s*\(",
        @"\$(?<v>\w+)\s*=\s*[^\r\n;]*\b(?:Get-Service|gsv|ServiceController)\b[\s\S]{0,4000}?\$\k<v>\.(?:Start|Stop|Pause|Continue|ExecuteCommand)\s*\(",
        @"\b(?:StartService|StopService|RestartService|PauseService|ResumeService|ChangeStartMode|ControlService(?:Ex)?|ChangeServiceConfig2?|DeleteService|CreateService|UserControlService)[AW]?\b",
        @"\bwmic(?:\.exe)?\b[^\r\n;&|]*\bservice\b[^\r\n;&|]*\b(?:call|delete|set)\b",
        @"\b(?:Invoke-(?:Cim|Wmi)Method|Remove-(?:Cim|Wmi)(?:Instance|Object)|Set-(?:Cim|Wmi)Instance)\b[^\r\n]*\bWin32_(?:Base)?Service\b",
        @"\bWin32_(?:Base)?Service\b[^\r\n]*\b(?:Invoke-(?:Cim|Wmi)Method|Remove-(?:Cim|Wmi)(?:Instance|Object)|Set-(?:Cim|Wmi)Instance)\b",
        @"\breg(?:\.exe)?[""']?\s+(?:add|delete|import|copy|restore|load)\b[^\r\n;&|]*\\(?:CurrentControlSet|ControlSet\d{3})\\Services\\",
        @"\b(?:Set|New|Remove|Rename|Clear|Copy|Move)-Item(?:Property)?\b[^\r\n;|]*\\(?:CurrentControlSet|ControlSet\d{3})\\Services\\",
    ];

    /// <summary>
    /// What changes a scheduled task: <c>schtasks</c> with <c>/run</c>, <c>/end</c>, <c>/change</c>, <c>/create</c> or <c>/delete</c> (or the
    /// same with a dash, 2026-10-20); the task cmdlets that change one; and since 2026-10-20 (a review) the Task Scheduler's COM object
    /// (<c>Schedule.Service</c>) with a member that changes a task, and WMI's <c>MSFT_ScheduledTask</c> with a method call. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> AdminTaskWords =
    [
        @"\bschtasks(?:\.exe)?\b[^\r\n;&|]*?\s[/-](?:run|end|change|create|delete)\b",
        @"\b(?:Start|Stop|Enable|Disable|Set|Register|Unregister)-ScheduledTask\b",
        @"\bSchedule\.Service\b[\s\S]{0,4000}?(?:\.(?:Run|RunEx|Stop|RegisterTask|RegisterTaskDefinition|DeleteTask|DeleteFolder|CreateFolder|SetSecurityDescriptor)\s*\(|\.Enabled\s*=)",
        @"\bInvoke-CimMethod\b[^\r\n]*\bMSFT_ScheduledTask\b",
        @"\bMSFT_ScheduledTask\b[^\r\n]*\bInvoke-CimMethod\b",
    ];

    /// <summary>
    /// What changes the directory: the ActiveDirectory module's changing cmdlets (<c>Set-AD…</c>, <c>New-AD…</c>, <c>Unlock-ADAccount</c>,
    /// <c>Add-ADGroupMember</c> …), the ds tools, the LDAP clients that write, an <c>ldifde</c>/<c>csvde</c> import; <c>net user|group … /domain</c>
    /// with a change is judged in <see cref="AdminPolice"/>. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> AdminDirectoryWords =
    [
        @"\b(?:Set|New|Remove|Add|Unlock|Enable|Disable|Move|Rename|Clear|Reset|Grant|Revoke|Install|Uninstall)-AD[A-Za-z]+\b",
        @"\b(?:dsmod|dsadd|dsrm|dsmove)(?:\.exe)?\b",
        @"\b(?:ldapmodify|ldapadd|ldapdelete|ldapmodrdn|ldappasswd)\b",
        @"\b(?:ldifde|csvde)(?:\.exe)?\b[^\r\n;&|]*?\s-i\b",
    ];

    /// <summary>
    /// A Mac's own directory writers (2026-10-18, the Mac AD plan: the directory tools now run there), judged in <see cref="AdminPolice"/> by
    /// these words on a Mac alone, beside <see cref="AdminDirectoryWords"/>, so Windows' list is unchanged. <c>dscl</c> refused when it writes
    /// (one of these verbs, with or without its dash) to any node but the local one, or on a line that names an Active Directory node or
    /// <c>/Search</c> (which can resolve to one) anywhere. Since 2026-10-20 (the code review: the node had to come before the verb, a
    /// <c>/Active\ Directory</c> escaped by zsh and <c>-createpl</c>, <c>-mcxset</c> and the rest got past the old pattern), the whole list from
    /// <c>dscl</c>'s help. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> DsclWriteVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "create", "createpl", "createpli", "delete", "deletepl", "deletepli", "append", "appendpl", "appendpli", "merge", "mergepl", "mergepli",
        "change", "changei", "passwd", "mcxset", "mcxedit", "mcxdelete", "mcxdeleteall", "mcximport",
    };

    /// <summary>
    /// The <c>dscl</c> verbs that only read. A <c>dscl</c> whose command is neither these nor <see cref="DsclWriteVerbs"/> (none, a redirection,
    /// <c>| dscl</c>) runs the commands it reads from its input, interactively: refused while the directory tools are on, since a later
    /// <c>process</c> write of <c>create …</c> names neither <c>dscl</c> nor the node (2026-10-20, the code review). Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> DsclReadVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "read", "readall", "readpl", "readpli", "list", "search", "diff", "auth", "authonly", "mcxread", "mcxexport", "mcxhelp",
    };

    /// <summary>
    /// What names Active Directory or the search node to the Mac's directory tools, case-blind: <c>/Active Directory</c> however its space is
    /// written (quoted apart, escaped as zsh's <c>/Active\ Directory</c>) and <c>/Search</c>. Pinned.
    /// </summary>
    public const string MacDirectoryNodePattern = @"Active[\s\\""']*Directory|/Search\b";

    /// <summary>
    /// The <c>dsconfigad</c> options that only read (2026-10-20, the code review: <c>-add</c>, <c>-remove</c>, <c>-force</c> and <c>-leave</c>
    /// were refused, while every advanced option, <c>-groups</c>, <c>-alldomains</c>, <c>-passinterval</c>…, changes the binding too). Any
    /// other option is refused. Pinned.
    /// </summary>
    public static readonly IReadOnlySet<string> DsconfigadReadOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-show", "-xml", "-help", "-h" };

    /// <summary>The <c>dseditgroup</c> operations that change a group, refused on a directory node (<c>-n</c> not the local one) or a line naming one. Pinned.</summary>
    public static readonly IReadOnlySet<string> DseditgroupWriteOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "edit", "create", "delete" };

    /// <summary>What a result the admin police refused opens with.</summary>
    public const string AdminHead = "Error: refused by the shell police — admin";

    /// <summary>What the model is told when the admin police tripped: the family's changes go through its tools alone. Pinned.</summary>
    public static string AdminPoliced(string family) => family switch
    {
        AdminPolice.ServicesFamily => $"{AdminHead}: while the service tools are on, a service is changed only through {Llm.Tools.ServiceControlTool.ToolName} and {Llm.Tools.ServiceStartTypeTool.ToolName}, which ask the user. Do not try another way (sc, net, a cmdlet, a script); reading with sc query or Get-Service is allowed.",
        AdminPolice.TasksFamily => $"{AdminHead}: while the task tools are on, a scheduled task is changed only through {Llm.Tools.TaskControlTool.ToolName}, which asks the user. Do not try another way (schtasks, a cmdlet, a script); reading with schtasks /query is allowed.",
        _ => $"{AdminHead}: while the directory tools are on, nothing in Active Directory is changed from here: the ad_ tools read only. Do not try to change it another way; tell the user what change is needed.",
    };

    /// <summary>The 👮 line the user reads: <c>Services: 'Stop-Service' in fix.ps1 — not run</c>. Pinned.</summary>
    public static string AdminShown(string family, string token, string? file) =>
        file is null ? $"{family}: '{token}' — not run" : $"{family}: '{token}' in {file} — not run";

    public static string AdminLogLine(CommandRequest request, string family, string token, string? file)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: {family} ('{token}'{(file is null ? "" : " in " + file)}) — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary>Whether <paramref name="result"/> is any police's refusal: the transcript draws it behind 👮 rather than 🛠️.</summary>
    public static bool IsPoliced(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return IsOutside(result) || result.StartsWith(ForbiddenHead, StringComparison.Ordinal) || result.StartsWith(SqliteHead, StringComparison.Ordinal) || result.StartsWith(ServerDatabaseHead, StringComparison.Ordinal)
            || result.StartsWith(AdminHead, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a <c>run_command</c> line a native tool covers answers (<see cref="NativeRedirect"/>, the setting <c>Shell prefer
    /// native tools</c>, 2026-09-26): not run, the tool to call, and the way back — the same line again goes to the user.
    /// Not an <c>Error:</c> on purpose: the rules forbid retrying a refused command, and this one may be retried once the
    /// tool cannot do it. <c>Not run: 'cat' has a tool of its own — call read_file instead. …</c> Pinned.
    /// </summary>
    public static string UseNative(string prefix, string tool) =>
        $"Not run: '{prefix}' has a tool of its own — call {tool} instead. If {tool} cannot do this, say why and call run_command again with the same command; the user will be asked.";

    public static string ShellNotInstalled(ShellKind kind) => $"Error: {ShellKinds.Name(kind)} is not installed (no {ShellKinds.FileName(kind)} found)";
    public static string WorkdirOutside(string path) => $"Error: workdir '{path}' is outside the working directory";
    public static string WorkdirNotFolder(string path) => $"Error: workdir '{path}' is not a folder";
    public static string BadTimeout(int min, int max) => $"Error: timeout must be {N(min)} to {N(max)}";
    public static string CommandTooLong(int max) => $"Error: the command is longer than {Count(max)} characters; put it in a script file and run that";
    public static string CouldNotStart(string executable, string detail) => $"Error: could not start {executable} ({detail})";

    /// <summary>The start's sweep killing a background command's group a dead app left running on a Mac (2026-10-12, <see cref="ShellRecords.Sweep"/>). Pinned.</summary>
    public static string GroupLeftBehind(int pgid, int ownerPid) =>
        $"A background command left running by NeonSidekick {N(ownerPid)}, which is gone: its process group {N(pgid)} killed";

    /// <summary>The sweep leaving a group whose leader is gone (2026-10-12): its id can no longer be proven the app's. Pinned.</summary>
    public static string GroupUnproven(int pgid, int ownerPid) =>
        $"A background command's process group {N(pgid)}, left by NeonSidekick {N(ownerPid)}, may still run; its leader is gone, so it was not killed (pgrep -l -g {N(pgid)} lists it)";

    /// <summary>The exit signals' kill of a command's group on a Mac (2026-10-12, <see cref="ShellGroups.KillTracked"/>). Pinned.</summary>
    public static string GroupKilledAtExit(int pgid) => $"group {N(pgid)}: killed at exit";

    /// <summary>The crash watcher could not be started (2026-10-12, <see cref="ShellGroups"/>): the run goes on unwatched. Pinned.</summary>
    public static string NoGuard(string reason) => $"No shell guard (background commands are not stopped should the app crash): {reason}";

    /// <summary>A background command's group the watcher could not record (2026-10-12): still killed by the exit signals. Pinned.</summary>
    public static string GroupNotRecorded(int pgid, string reason) => $"group {N(pgid)}: not recorded for the shell guard ({reason})";

    /// <summary>The guard's start and its output, at debug (2026-10-12). Pinned.</summary>
    public static string GuardStarted(int pid) => $"shell guard: pid {N(pid)}";

    /// <inheritdoc cref="GuardStarted"/>
    public static string GuardSaid(string line) => "shell guard: " + line;

    /// <summary><see cref="CouldNotStart"/>'s detail for a raw argument string on a Mac (2026-10-12, <see cref="GroupChild"/>): only cmd.exe's launch has one. Pinned.</summary>
    public const string RawArgumentsAreCmds = "a raw argument string is cmd's";

    /// <summary><see cref="GroupNotRecorded"/>'s reason when this process's own start time cannot be read (later on 2026-10-19, out of <see cref="ShellRecords.Write"/>). Pinned.</summary>
    public const string OwnStartUnreadable = "this process's start time could not be read";

    /// <summary><see cref="CouldNotStart"/>'s detail when libc's spawn is missing on a Mac (later on 2026-10-19, out of <see cref="GroupChild"/>). Pinned.</summary>
    public const string PosixSpawnMissing = "posix_spawnp is not available";

    /// <summary>The warning beside <see cref="PosixSpawnMissing"/>: what the runtime said. Pinned.</summary>
    public static string PosixSpawnFailed(string detail) => "posix_spawnp: " + detail;

    // ── Log lines ────────────────────────────────────────────────────────────

    public static string ApprovedLogLine(CommandRequest request, string how)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"approval: {how} — {request.Kind} {Quote(request.Command)}";
    }

    public static string RefusedLogLine(CommandRequest request, string why)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"approval: refused ({why}) — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary><c>police: refused ('C:\Windows') — powershell "type C:\Windows\win.ini"</c>: the police's line, before the gate is asked (2026-09-22).</summary>
    public static string PolicedLogLine(CommandRequest request, string token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: refused ('{token}') — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary><c>police: forbidden ('rm -rf') — powershell "rm -rf build"</c>: a forbidden string's line, before the gate is asked (2026-10-03).</summary>
    public static string ForbiddenLogLine(CommandRequest request, string entry)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"police: forbidden ('{entry}') — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary><c>native: read_file for 'cat' — powershell "cat README.md"</c>: a line sent back to its tool, before the gate is asked (2026-09-26).</summary>
    public static string NativeLogLine(CommandRequest request, string prefix, string tool)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"native: {tool} for '{prefix}' — {request.Kind} {Quote(request.Command)}";
    }

    /// <summary><c>run_command: powershell "git status" → exit 0 in 1.2 s (2,340 chars)</c>.</summary>
    public static string RunLogLine(string kind, string label, string outcome, long chars) =>
        $"run_command: {kind} {Quote(label)} → {outcome} ({Count(chars)} chars)";

    // ── The approval pane ────────────────────────────────────────────────────

    public const string ApprovalTitle = "Run this command?";
    public const string ScriptApprovalTitle = "Run this script?";
    public const string DenyRow = "Deny";
    public const string OnceRow = "Allow once";
    public const string ApprovalKeys = "d / o / s / a = pick · v = view · Enter = choose · Esc = deny";

    /// <summary>
    /// The approval pane's title-row button (2026-10-07, the user's ask: the caption cut a long command at three rows and showed a
    /// script's first line alone, so part of what was approved could go unseen): the whole command in a scrolling view. Pinned.
    /// </summary>
    public const string ViewButton = "≡ view";

    /// <summary>The key that is <see cref="ViewButton"/>.</summary>
    public const char ViewKey = 'v';

    /// <summary>The view's label: <c>Run this command? › powershell</c>, the script's language and size for a script. Pinned.</summary>
    public static string WholeCommandLabel(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsScript)
        {
            return ApprovalTitle + " › " + request.Kind;
        }

        int lines = request.Command.Split('\n').Length;
        return ScriptApprovalTitle + " › " + request.Kind + " · " + Count(lines) + (lines == 1 ? " line" : " lines");
    }

    /// <summary>
    /// The view's lines: a command as it is (the view wraps it); a script's lines numbered, the numbers right-aligned, so a line the
    /// view wraps still reads as one. Pinned.
    /// </summary>
    public static IReadOnlyList<string> WholeCommandLines(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lines = request.Command.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (!request.IsScript)
        {
            return lines;
        }

        int width = lines.Length.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
        return lines.Select((line, i) => (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width) + "  " + line).ToList();
    }

    /// <summary>The pane's caption: <c>powershell › git push origin main</c>; a script's names the language and its size. Pinned.</summary>
    public static string Caption(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsScript)
        {
            return $"{request.Kind} › {request.Command}";
        }

        var lines = request.Command.Split('\n');
        string first = lines.Length > 0 ? lines[0].TrimEnd('\r') : "";
        return $"{request.Kind} · {Count(lines.Length)} {(lines.Length == 1 ? "line" : "lines")} · first line: {first}";
    }

    /// <summary><c>Allow "git push" for this session</c>; several prefixes listed; a script's reads <c>Allow python scripts for this session</c>. Pinned.</summary>
    public static string SessionRow(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"Allow {What(request)} for this session";
    }

    /// <summary><c>Allow "git push" always (saved to the profile)</c>. Pinned.</summary>
    public static string PermanentRow(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return $"Allow {What(request)} always (saved to the profile)";
    }

    private static string What(CommandRequest request) =>
        request.IsScript ? $"{request.Kind} scripts" : string.Join(", ", request.Prefixes.Select(Quote));

    /// <summary>The transcript's line after a Session pick: <c>(allowed for this session: git push)</c>. Pinned.</summary>
    public static string SessionAllowedNotice(IReadOnlyList<string> prefixes) => $"({App.NoticeGlyphs.Allowed}allowed for this session: {string.Join(", ", prefixes)})";

    /// <summary>The transcript's line after a Permanent pick: <c>(allowed always: git push — the Shell tab of /tools)</c>. Pinned.</summary>
    public static string PermanentAllowedNotice(IReadOnlyList<string> prefixes) => $"({App.NoticeGlyphs.Allowed}allowed always: {string.Join(", ", prefixes)} — the Shell tab of /tools)";

    // ── /cmd (2026-10-09, the user's own line) ──────────────────────────────

    /// <summary><c>/cmd</c>'s word.</summary>
    public const string CmdWord = "/cmd";

    /// <summary><c>/cmd</c>'s shell option, before the command.</summary>
    public const string CmdShellOption = "--shell";

    /// <summary>The completion note beside <see cref="CmdShellOption"/>. Pinned.</summary>
    public const string CmdShellNote = "run it in another shell than Shell default";

    /// <summary><c>/cmd</c>'s background switch (2026-10-11), before the command, with or around <see cref="CmdShellOption"/>.</summary>
    public const string CmdBgOption = "--bg";

    /// <summary>The completion note beside <see cref="CmdBgOption"/>. Pinned.</summary>
    public const string CmdBgNote = "run it in the background; its output is reported when it ends";

    /// <summary>A bare <c>/cmd</c>, or an option with no command after it. Pinned.</summary>
    public const string CmdUsage = "/cmd takes a shell line: /cmd [--bg] [--shell <shell>] <command>";

    /// <summary>
    /// <c>/cmd</c> while <c>Shell command policy</c> is <c>off</c> (the user's pick: off means no shell at all, the user's line
    /// included). Pinned.
    /// </summary>
    public const string CmdPolicyOff = "Shell command policy is off: no command runs. /tools shell sets it to ask or yolo.";

    /// <summary>The user's line, denied on the approval pane (or ESC there). Pinned.</summary>
    public const string CmdDenied = "not run: denied";

    /// <summary>The user's line under <c>ask</c> with no pane to ask on, and not on the allow list. Pinned.</summary>
    public const string CmdNotAskable = "not run: Shell command policy is ask and there is no pane to ask on; the profile's Shell allowed commands or yolo would let it run";

    /// <summary>The spinner's label while the line runs: <c>running in powershell…</c>. Pinned.</summary>
    public static string CmdRunning(string kind) => $"running in {kind}…";

    /// <summary>
    /// The one notice where the live output stops being drawn (over <c>Shell output max chars</c>): the line runs on, and the
    /// whole output goes to the spill file at its end. Pinned.
    /// </summary>
    public static string CmdOutputCut(int maxChars) =>
        $"(output over {Count(maxChars)} characters: not shown from here; the whole output goes to {SpillFolderName}{Path.DirectorySeparatorChar} when it ends)";

    /// <summary><c>cancelled after 2.1 s (powershell, killed): ping -n 30 127.0.0.1</c>: a line ESC stopped. Pinned.</summary>
    public static string CmdCancelledHeader(string kind, TimeSpan elapsed, string label) =>
        $"canceled after {Elapsed(elapsed)} ({kind}, killed): {label}";

    /// <summary>
    /// A <c>/cmd --bg</c> line started (2026-10-11): <c>started proc_3f2a1b in the background (powershell, pid 1234): npm run build;
    /// /process proc_3f2a1b watches it</c>. Pinned.
    /// </summary>
    public static string CmdStarted(string id, string kind, int pid, string label) =>
        $"started {id} in the background ({kind}, pid {N(pid)}): {label}; /process {id} watches it";

    /// <summary><c>stopped by you after 12.0 s (powershell, killed): ping -n 60 127.0.0.1</c>: a <c>/cmd --bg</c> line stopped from <c>/process</c>'s window. Pinned.</summary>
    public static string CmdStoppedHeader(string kind, TimeSpan elapsed, string label) =>
        $"stopped by you after {Elapsed(elapsed)} ({kind}, killed): {label}";

    /// <summary>
    /// Before a <c>/cmd --bg</c> report's output when only its end fits <c>Shell output max chars</c> (2026-10-11, the review: the report
    /// drew the start and cut the ending, the errors and the summary): <c>(the first 14,880 lines are not shown here; the whole output is
    /// in .neon\shell\proc_3f2a1b.log)</c>, <c>the output kept</c> when the buffer had dropped lines, no file when none could be written. Pinned.
    /// </summary>
    public static string CmdEarlierHidden(long lines, string? spill, bool whole) =>
        $"(the first {Count(lines)} {(lines == 1 ? "line is" : "lines are")} not shown here"
        + (spill is null ? ")" : $"; the {(whole ? "whole output" : "output kept")} is in {spill})");

    /// <summary>
    /// After a <c>/cmd --bg</c> run's report when it was started in another conversation (2026-10-11, the review: a run that ended
    /// after <c>/new</c>, <c>/resume</c> or a profile switch was kept in the new conversation and its store): shown, never kept.
    /// </summary>
    public const string CmdOtherConversation = "(started in an earlier conversation: shown here, not added to this one)";

    /// <summary>What the user's side of a <c>/cmd</c> run opens with in the history.</summary>
    public const string CmdHistoryHead = "[I ran in the shell";

    /// <summary>The words after the shell's name in a background run's <see cref="CmdHistoryUser"/> (2026-10-11).</summary>
    public const string CmdHistoryBackground = ", in the background";

    /// <summary>
    /// The user's side of a <c>/cmd</c> run in the history (the user's pick: the model sees it, the <c>/claude</c> shape):
    /// <c>[I ran in the shell (powershell)] git status</c>, tagged, so the model reads it as the user's own run and not a
    /// request; a <c>/cmd --bg</c> run (2026-10-11) <c>[I ran in the shell (powershell, in the background)] npm run build</c>. Pinned.
    /// </summary>
    public static string CmdHistoryUser(string kind, string command, bool background = false) =>
        $"{CmdHistoryHead} ({kind}{(background ? CmdHistoryBackground : "")})] {command}";

    /// <summary>The run's side: <c>[shell output] exit 0 in 0.3 s (powershell): git status</c> and the output, as <c>run_command</c> would have it. Pinned.</summary>
    public static string CmdHistoryReply(string result) => "[shell output] " + result;

    /// <summary>
    /// <see cref="CmdHistoryUser"/> read back (<c>/rewind</c>): the line that runs it again in the same shell,
    /// <c>/cmd --shell powershell git status</c> — the shell named, so a changed <c>Shell default</c> runs the same thing — and
    /// <c>/cmd --bg --shell …</c> for a background run, or null for any other text. Pinned.
    /// </summary>
    public static string? CmdHistoryCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string open = CmdHistoryHead + " (";
        if (!text.StartsWith(open, StringComparison.Ordinal))
        {
            return null;
        }

        int close = text.IndexOf(")] ", open.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }

        string kind = text[open.Length..close];
        string command = text[(close + 3)..];
        bool background = kind.EndsWith(CmdHistoryBackground, StringComparison.Ordinal);
        if (background)
        {
            kind = kind[..^CmdHistoryBackground.Length];
        }

        return $"{CmdWord} {(background ? CmdBgOption + " " : "")}{CmdShellOption} {kind} {command}";
    }

    /// <summary><c>/cmd: powershell "git status" → exit 0 in 1.2 s (2,340 chars)</c>.</summary>
    public static string CmdLogLine(string kind, string label, string outcome, long chars) =>
        $"{CmdWord}: {kind} {Quote(label)} → {outcome} ({Count(chars)} chars)";

    // ── Formatting ───────────────────────────────────────────────────────────

    /// <summary><c>0.4 s</c>, <c>12.0 s</c>, <c>3 m 12 s</c>, <c>1 h 2 m</c>. Pinned.</summary>
    public static string Elapsed(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalMinutes < 1)
        {
            return value.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }

        if (value.TotalHours < 1)
        {
            return $"{N(value.Minutes)} m {N(value.Seconds)} s";
        }

        return $"{N((int)value.TotalHours)} h {N(value.Minutes)} m";
    }

    /// <summary>A count with thousands separators: <c>412,345</c>. Invariant.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string text) => "\"" + text.ReplaceLineEndings(" ") + "\"";
}
