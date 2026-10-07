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

    /// <summary>The folder under the working directory a cut result's whole output is written to.</summary>
    public const string SpillFolderName = ".shell";

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
    public static string Result(string header, IReadOnlyList<OutputLine> lines, int maxChars, string? spill)
    {
        ArgumentNullException.ThrowIfNull(header);
        string body = Body(lines);
        if (body.Length == 0)
        {
            return header + "\n" + NoOutput;
        }

        if (body.Length <= maxChars)
        {
            return header + "\n" + body;
        }

        return header + CutSuffix + "\n" + Cut(body, maxChars, spill);
    }

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
    public static string Cut(string text, int maxChars, string? spill)
    {
        ArgumentNullException.ThrowIfNull(text);
        int headChars = (int)(maxChars * HeadShare);
        int tailChars = maxChars - headChars;
        string head = text[..Math.Min(headChars, text.Length)];
        int lastBreak = head.LastIndexOf('\n');
        if (lastBreak > 0)
        {
            head = head[..lastBreak];
        }

        string tail = text[Math.Max(0, text.Length - tailChars)..];
        int firstBreak = tail.IndexOf('\n');
        if (firstBreak >= 0 && firstBreak < tail.Length - 1)
        {
            tail = tail[(firstBreak + 1)..];
        }

        int removed = text.Length - head.Length - tail.Length;
        return head + "\n" + CutLine(removed, spill) + "\n" + tail;
    }

    /// <summary><c>… (412,345 chars cut; the whole output is in .shell\run_9c04e1.log) …</c>, or without the file when none could be written. Pinned.</summary>
    public static string CutLine(int chars, string? spill) =>
        spill is null
            ? $"… ({Count(chars)} characters cut) …"
            : $"… ({Count(chars)} characters cut; the whole output is in {spill}) …";

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

    /// <summary>What a result the SQLite police refused opens with (<see cref="SqlitePolice"/>, 2026-10-05).</summary>
    public const string SqliteHead = "Error: refused by the shell police — SQLite";

    /// <summary>
    /// What the model is told when the SQLite police tripped (2026-10-05, the user's call): while the SQLite tools are on, a
    /// database is theirs alone, and no other way is to be tried. Pinned.
    /// </summary>
    public const string SqlitePoliced = SqliteHead + ": while the SQLite tools are on, a SQLite database is reached only through them — " +
        "sqlite_query to read, and sqlite_execute to change one when SQLite mode allows it. Do not try another way (a script, another driver, " +
        "the file tools); tell the user if a change is needed that the tools refuse.";

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
            "another driver); tell the user if a change is needed that the tools refuse.";
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

    /// <summary>Whether <paramref name="result"/> is any police's refusal: the transcript draws it behind 👮 rather than 🛠️.</summary>
    public static bool IsPoliced(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return IsOutside(result) || result.StartsWith(ForbiddenHead, StringComparison.Ordinal) || result.StartsWith(SqliteHead, StringComparison.Ordinal) || result.StartsWith(ServerDatabaseHead, StringComparison.Ordinal);
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
    public const string ApprovalKeys = "d / o / s / a = pick · v = view · Enter = choose · ESC = deny";

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
