using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.App;

// ── /cmd (2026-10-09) ─────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The id prefix of a <c>/cmd</c> run: its spill file's name and the log's.</summary>
    public const string CmdIdPrefix = "cmd_";

    /// <summary>How often the live output is drawn when the pumps are quiet, and the longest a new line waits for its draw.</summary>
    private static readonly TimeSpan CmdDrawInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// <c>/cmd [--shell &lt;shell&gt;] &lt;command&gt;</c> (2026-10-09, the user's ask): a shell line typed by the user, run in the
    /// working directory, its output drawn live in the transcript. It passes what <c>run_command</c>'s line passes (the user's
    /// call): <c>Shell command policy</c> off refuses, the police (<see cref="ShellPoliceChecks"/>) refuse whatever the policy, and
    /// under <c>ask</c> the gate puts a line not on the allow list to the approval pane — <c>Shell prefer native tools</c> alone is
    /// left out, being the model's guidance. The wait is <c>run_command</c>'s default (<c>Shell timeout (s)</c> within the
    /// foreground cap); ESC kills the line and everything it started. Then the pair goes into the history, tagged
    /// (<see cref="ShellText.CmdHistoryUser"/>, <see cref="ShellText.CmdHistoryReply"/>: the user's pick, the <c>/claude</c> shape),
    /// the reply the text <c>run_command</c> would have handed the model, cut at <c>Shell output max chars</c> with the whole in
    /// <c>.shell\cmd_….log</c>. Plan mode does not stop it: the user's own hand. Interactive only; headless sends the line to the model.
    /// <c>--bg</c> (2026-10-11, the user's ask) passes the same checks, then starts the line in the background
    /// (<see cref="StartCmdInBackground"/>) and its end is reported, the pair kept, at the next idle loop top (<see cref="ReportCmdRuns"/>).
    /// </summary>
    private async Task HandleCmdAsync(string args, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (!TryParseCmd(args, effective, out var kind, out string command, out bool background, out string? error))
        {
            _transcript.Error(error!);
            return;
        }

        if (kind == ShellKind.PowerShell && command.Length > ShellCommandLine.MaxCommandChars)
        {
            _transcript.Error(ShellText.CommandTooLong(ShellCommandLine.MaxCommandChars));
            return;
        }

        if (_interpreters.Locate(kind) is not { } executable)
        {
            _transcript.Error(ShellText.ShellNotInstalled(kind));
            return;
        }

        if (CommandPolicy.Resolve(effective) == CommandPolicyMode.Off)
        {
            _transcript.Error(ShellText.CmdPolicyOff);
            return;
        }

        string workdir;
        try
        {
            workdir = _files.EnsureExists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _transcript.Error(Files.FileText.CouldNot("create", _files.Root, ex.Message));
            return;
        }

        var request = new CommandRequest(ShellKinds.Name(kind), command, CommandPrefix.All(command));
        if (ShellPoliceChecks.Judge(request, command, workdir, _files, effective) is { } policed)
        {
            _transcript.PoliceNote(policed.Shown ?? ShellText.Note(policed.ModelText));
            return;
        }

        using var cmdCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stop = new CancellationTokenSource();
        // The reply's watch (the /test shape): the approval pane opens through it, ESC cancels the line, a line typed meanwhile goes through the mid-turn policy.
        var watcher = StartReplyWatch(cmdCts, stop.Token, cancellationToken);
        CmdRun? run = null;
        try
        {
            var verdict = await _gate.JudgeAsync(request, cmdCts.Token).ConfigureAwait(false);
            if (!verdict.Allowed)
            {
                _transcript.Notice(_pane.Enabled ? ShellText.CmdDenied : ShellText.CmdNotAskable);
                return;
            }

            if (background)
            {
                // --bg (2026-10-11): started and handed to the registry; the report and the history pair wait for its end.
                StartCmdInBackground(request, kind, executable, workdir);
            }
            else
            {
                run = await RunCmdAsync(request, kind, executable, workdir, effective, cmdCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cmdCts.IsCancellationRequested)
        {
            // ESC before the line ran (on the way to the pane): nothing ran, nothing to keep.
            _transcript.Notice(CancelledNotice);
        }
        finally
        {
            stop.Cancel();
            await watcher.ConfigureAwait(false);
            await EndTurnAsync(closePane: cmdCts.IsCancellationRequested, cancellationToken).ConfigureAwait(false);
        }

        if (run is { } done)
        {
            KeepCmdRun(ShellText.CmdWord + " " + args.Trim(), request.Kind, request.Command, background: false, done, effective);
        }

        DrainDiagnostics();
    }

    /// <summary><see cref="TryParseCmd(string, AppSettingsData, out ShellKind, out string, out bool, out string?)"/> without the background switch's answer.</summary>
    internal static bool TryParseCmd(string args, AppSettingsData effective, out ShellKind kind, out string command, out string? error) =>
        TryParseCmd(args, effective, out kind, out command, out _, out error);

    /// <summary>
    /// <c>/cmd</c>'s argument: the options first, in either order — <c>--bg</c> (2026-10-11, <paramref name="background"/>) and
    /// <c>--shell &lt;shell&gt;</c> — then the command; the shell otherwise the <c>Shell default</c>'s. False with the error line for a
    /// bare <c>/cmd</c>, options with no command after them, or a shell that is none of <see cref="ShellKinds.Names"/>.
    /// </summary>
    internal static bool TryParseCmd(string args, AppSettingsData effective, out ShellKind kind, out string command, out bool background, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(effective);
        kind = ShellKinds.Resolve(effective);
        command = args.Trim();
        background = false;
        error = null;
        while (true)
        {
            if (!background && IsOption(command, ShellText.CmdBgOption))
            {
                background = true;
                command = command[ShellText.CmdBgOption.Length..].TrimStart();
                continue;
            }

            if (IsOption(command, ShellText.CmdShellOption))
            {
                string rest = command[ShellText.CmdShellOption.Length..].TrimStart();
                int space = rest.IndexOfAny([' ', '\t']);
                string name = space < 0 ? rest : rest[..space];
                command = space < 0 ? "" : rest[(space + 1)..].Trim();
                if (name.Length == 0 || command.Length == 0)
                {
                    error = ShellText.CmdUsage;
                    return false;
                }

                if (!ShellKinds.TryParse(name, out kind))
                {
                    error = Files.FileText.BadChoice("shell", name, string.Join(", ", ShellKinds.Names));
                    return false;
                }

                continue;
            }

            break;
        }

        if (command.Length == 0)
        {
            error = ShellText.CmdUsage;
            return false;
        }

        return true;

        // The option alone or followed by a space or tab: "--shellish" is a command of that name.
        static bool IsOption(string text, string option) =>
            text.StartsWith(option, StringComparison.OrdinalIgnoreCase)
            && (text.Length == option.Length || text[option.Length] is ' ' or '\t');
    }

    /// <summary>
    /// <c>/cmd</c>'s argument list: the options not yet given (<c>--bg</c>, <c>--shell</c>) while a leading word is being typed, then
    /// <c>--shell &lt;name&gt;</c> for each of <see cref="ShellKinds.Names"/>, each after the options already typed; nothing once the command has begun.
    /// </summary>
    internal static IReadOnlyList<UI.CompletionItem> CmdChoices(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        string done = "";
        string rest = argText;
        bool bg = false;
        bool shell = false;
        while (true)
        {
            string bgSpace = ShellText.CmdBgOption + " ";
            string shellSpace = ShellText.CmdShellOption + " ";
            if (!bg && rest.StartsWith(bgSpace, StringComparison.OrdinalIgnoreCase))
            {
                done += rest[..bgSpace.Length];
                rest = rest[bgSpace.Length..];
                bg = true;
                continue;
            }

            if (!shell && rest.StartsWith(shellSpace, StringComparison.OrdinalIgnoreCase))
            {
                string after = rest[shellSpace.Length..];
                int space = after.IndexOf(' ', StringComparison.Ordinal);
                if (space < 0)
                {
                    // The shell's name is being typed.
                    string head = done + rest[..shellSpace.Length];
                    return UI.MentionCompleter.Matches(ShellKinds.Names.Select(name => new UI.CompletionItem(head + name, ShellKinds.Describe(name))).ToList(), argText);
                }

                done += rest[..(shellSpace.Length + space + 1)];
                rest = after[(space + 1)..];
                shell = true;
                continue;
            }

            break;
        }

        if (rest.Contains(' ', StringComparison.Ordinal))
        {
            return [];
        }

        var items = new List<UI.CompletionItem>();
        if (!bg)
        {
            items.Add(new(done + ShellText.CmdBgOption, ShellText.CmdBgNote));
        }

        if (!shell)
        {
            items.Add(new(done + ShellText.CmdShellOption, ShellText.CmdShellNote));
        }

        return UI.MentionCompleter.Matches(items, argText);
    }

    /// <summary>A finished <c>/cmd</c> run: what the history keeps (<see cref="ShellText.Result"/>), its body's length and whether ESC ended it.</summary>
    private sealed record CmdRun(string Result, long Chars, bool Cancelled);

    /// <summary>
    /// The line run and drawn as it prints: the work task (never a pump thread) writes what came since the last draw at each
    /// new line or <see cref="CmdDrawInterval"/>, until the body passes <c>Shell output max chars</c>, where one notice says so and
    /// the drawing stops — the line runs on. ESC kills it; so does the timeout. Null when it could not start (said on the transcript).
    /// </summary>
    private async Task<CmdRun?> RunCmdAsync(CommandRequest request, ShellKind kind, string executable, string workdir, AppSettingsData effective, CancellationToken cancellationToken)
    {
        int maxChars = Math.Clamp(effective.ShellOutputMaxChars, AppSettingsData.MinShellOutputMaxChars, AppSettingsData.MaxShellOutputMaxChars);
        int cap = Llm.Tools.RunCommandTool.ForegroundCap(effective);
        var timeout = TimeSpan.FromSeconds(Math.Min(cap, Math.Clamp(effective.ShellTimeoutSeconds, AppSettingsData.MinShellTimeoutSeconds, AppSettingsData.MaxShellTimeoutSeconds)));
        ProcessSession session;
        try
        {
            session = _runner.Start(ShellCommandLine.For(kind, request.Command, executable, workdir), ShellRunner.NewId(_random, CmdIdPrefix));
        }
        catch (ShellStartException ex)
        {
            _transcript.Error(ex.Message);
            return null;
        }

        using (session)
        {
            using var appended = new SemaphoreSlim(0);
            void Wake()
            {
                if (appended.CurrentCount == 0)
                {
                    appended.Release();
                }
            }

            session.Output.Appended += Wake;
            long cursor = 0;
            long shown = 0;
            bool cut = false;

            // What came since the last draw, drawn until the cap.
            void Draw() => DrawCmdLines(session.Output.Since(cursor, out cursor), maxChars, ref shown, ref cut);

            bool exited = false;
            bool cancelled = false;
            using (_transcript.BeginBusy(ShellText.CmdRunning(request.Kind)))
            {
                session.CloseInput();
                long started = _time.GetTimestamp();
                // One wait of each kind at a time, all ended with the loop, so none is left on the semaphore it outlives.
                using var waits = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task? line = null;
                Task? tick = null;
                try
                {
                    while (true)
                    {
                        if (session.HasExited)
                        {
                            // The pumps drain before Exited completes, so the draw after the loop sees the last line.
                            exited = true;
                            break;
                        }

                        if (_time.GetElapsedTime(started) >= timeout)
                        {
                            break;
                        }

                        line ??= appended.WaitAsync(waits.Token);
                        tick ??= Task.Delay(CmdDrawInterval, _time, waits.Token);
                        await Task.WhenAny(session.Exited, line, tick).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        line = line.IsCompleted ? null : line;
                        tick = tick.IsCompleted ? null : tick;
                        Draw();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                }
                finally
                {
                    session.Output.Appended -= Wake;
                    waits.Cancel();
                }

                if (!exited)
                {
                    session.Kill();
                    await session.WaitAsync(Llm.Tools.RunCommandTool.KillGrace, CancellationToken.None).ConfigureAwait(false);
                }

                Draw();
            }

            string header = cancelled
                ? ShellText.CmdCancelledHeader(request.Kind, session.Elapsed, request.Command)
                : exited
                    ? ShellText.ExitHeader(request.Kind, session.ExitCode ?? -1, session.Elapsed, request.Command)
                    : ShellText.TimedOutHeader(request.Kind, timeout, request.Command);
            var lines = session.Output.Snapshot(out long dropped);
            string body = ShellText.Body(lines, dropped);
            string? spill = body.Length > maxChars ? ShellPoliceChecks.Spill(_files.Root, session.Id, body) : null;
            if (exited && session.ExitCode == 0)
            {
                _transcript.Notice(header);
            }
            else
            {
                _transcript.Warning(header);
            }

            string outcome = cancelled ? "cancelled after " + ShellText.Elapsed(session.Elapsed)
                : exited ? "exit " + (session.ExitCode ?? -1).ToString(CultureInfo.InvariantCulture) + " in " + ShellText.Elapsed(session.Elapsed)
                : "timed out after " + ShellText.Elapsed(timeout);
            DiagnosticLog.Info(ShellKinds.Category, ShellText.CmdLogLine(request.Kind, request.Command, outcome, body.Length));
            return new CmdRun(ShellText.Result(header, lines, maxChars, spill, dropped), body.Length, cancelled);
        }
    }

    /// <summary>
    /// Output lines onto the transcript until <paramref name="shown"/> passes <paramref name="maxChars"/>, where one notice says so
    /// (<paramref name="cut"/>) and the rest are skipped: the foreground's live draw (a background run's report shows the tail instead, <see cref="CmdTailStart"/>).
    /// </summary>
    private void DrawCmdLines(IEnumerable<OutputLine> lines, int maxChars, ref long shown, ref bool cut)
    {
        foreach (var line in lines)
        {
            if (cut)
            {
                continue;
            }

            shown += line.Text.Length + 1;
            if (shown > maxChars)
            {
                cut = true;
                _transcript.Notice(ShellText.CmdOutputCut(maxChars));
                continue;
            }

            _transcript.ShellOutput(line.Text, line.IsError);
        }
    }

    /// <summary>
    /// <c>/cmd --bg</c>'s start (2026-10-11, the user's ask): the line under the <see cref="ProcessRegistry"/> as the user's own
    /// (<see cref="ProcessSession.Mine"/>: <c>/process</c> lists it and its window watches it, the model's <c>process</c> tool never
    /// sees it), stdin closed at once as the foreground's is (nothing can write to it), no timeout. Its end comes back as an alert
    /// the screen holds for the idle loop top (<see cref="ReportCmdRuns"/>). A refused start (too many running) is the error line.
    /// </summary>
    private void StartCmdInBackground(CommandRequest request, ShellKind kind, string executable, string workdir)
    {
        ProcessSession session;
        try
        {
            session = _processes.Start(ShellCommandLine.For(kind, request.Command, executable, workdir), notify: false, mine: true);
        }
        catch (ShellStartException ex)
        {
            _transcript.Error(ex.Message);
            return;
        }

        session.CloseInput();
        _cmdConversations[session.Id] = _conversation;
        _transcript.Notice(ShellText.CmdStarted(session.Id, request.Kind, session.Pid, request.Command));
        DiagnosticLog.Info(ShellKinds.Category, ShellText.CmdLogLine(request.Kind, request.Command, "started " + session.Id + " in the background", 0));
    }

    /// <summary>The ends of the user's background runs (<see cref="ProcessAlert.Mine"/>) held for the idle loop top: a reply may be running when one lands.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<ProcessAlert> _cmdReports = new();

    /// <summary>
    /// The conversation on screen, a count bumped whenever it is left (<see cref="ForgetSession"/>: <c>/new</c>, <c>/clear</c>, a profile
    /// switch; <see cref="RestoreSession"/>), and the one each <c>/cmd --bg</c> run started in (2026-10-11, the review): a run that ends
    /// in another is shown, never kept in that conversation's history or store.
    /// </summary>
    private int _conversation;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _cmdConversations = new(StringComparer.Ordinal);

    /// <summary>
    /// The held ends of <c>/cmd --bg</c> runs, at the idle loop top (2026-10-11): each one's header (a notice for exit 0, else a warning;
    /// <see cref="ShellText.CmdStoppedHeader"/> for a stop from <c>/process</c>'s window), the end of its output that fits <c>Shell output
    /// max chars</c>, the rest in <c>.shell\proc_….log</c> when over, then the tagged pair into the history (the model reads it on the next
    /// turn), <c>/copy</c>'s log and the store — <see cref="KeepCmdRun"/>, as a foreground run — unless it was started in another
    /// conversation (<see cref="_conversation"/>), which is shown and not kept. A session gone meanwhile (closed or evicted) leaves only its alert line.
    /// </summary>
    private void ReportCmdRuns()
    {
        while (_cmdReports.TryDequeue(out var alert))
        {
            bool here = _cmdConversations.TryRemove(alert.Id, out int started) && started == _conversation;
            if (_processes.Find(alert.Id, out var session, out _) != FindOutcome.Found || session is null)
            {
                _transcript.ProcessAlert(ShellText.AlertLine(alert));
                continue;
            }

            var effective = _effective();
            int maxChars = Math.Clamp(effective.ShellOutputMaxChars, AppSettingsData.MinShellOutputMaxChars, AppSettingsData.MaxShellOutputMaxChars);
            string header = alert.Killed
                ? ShellText.CmdStoppedHeader(alert.Kind, alert.Elapsed, alert.Label)
                : ShellText.ExitHeader(alert.Kind, alert.ExitCode, alert.Elapsed, alert.Label);
            if (!alert.Killed && alert.ExitCode == 0)
            {
                _transcript.Notice(header);
            }
            else
            {
                _transcript.Warning(header);
            }

            var lines = session.Output.Snapshot(out long dropped);
            string body = ShellText.Body(lines, dropped);
            string? spill = body.Length > maxChars ? ShellPoliceChecks.Spill(_files.Root, session.Id, body) : null;
            // The end of the output, not its start (2026-10-11, the review): a finished run's errors and summary are last, and the
            // buffer may already have dropped its first lines; the hidden ones are counted and the spill named.
            int from = CmdTailStart(lines, maxChars);
            if (from > 0 || dropped > 0)
            {
                _transcript.Notice(ShellText.CmdEarlierHidden(dropped + from, spill, whole: dropped == 0));
            }

            for (int i = from; i < lines.Count; i++)
            {
                _transcript.ShellOutput(lines[i].Text, lines[i].IsError);
            }

            string outcome = alert.Killed ? "stopped by the user after " + ShellText.Elapsed(alert.Elapsed)
                : "exit " + alert.ExitCode.ToString(CultureInfo.InvariantCulture) + " in " + ShellText.Elapsed(alert.Elapsed);
            DiagnosticLog.Info(ShellKinds.Category, ShellText.CmdLogLine(alert.Kind, alert.Label, outcome, body.Length));
            if (!here)
            {
                _transcript.Notice(ShellText.CmdOtherConversation);
                continue;
            }

            string line = $"{ShellText.CmdWord} {ShellText.CmdBgOption} {ShellText.CmdShellOption} {alert.Kind} {alert.Label}";
            KeepCmdRun(line, alert.Kind, alert.Label, background: true, new CmdRun(ShellText.Result(header, lines, maxChars, spill, dropped), body.Length, Cancelled: false), effective);
        }
    }

    /// <summary>
    /// The index of the first of <paramref name="lines"/> whose run to the end fits <paramref name="maxChars"/> (each line counted with
    /// its newline), the last line always shown: a background report's tail.
    /// </summary>
    internal static int CmdTailStart(IReadOnlyList<OutputLine> lines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(lines);
        long chars = 0;
        int from = lines.Count;
        while (from > 0)
        {
            chars += lines[from - 1].Text.Length + 1;
            if (chars > maxChars && from < lines.Count)
            {
                break;
            }

            from--;
        }

        return from;
    }

    /// <summary>
    /// A run into the history as a tagged pair (a cancelled one too: the line did run), the store as a turn, and <c>/copy</c>'s
    /// log, the reply <c>/copy</c> takes last. <paramref name="line"/> is the <c>/cmd</c> line as the log and the store show it.
    /// </summary>
    private void KeepCmdRun(string line, string kind, string command, bool background, CmdRun run, AppSettingsData effective)
    {
        string reply = ShellText.CmdHistoryReply(run.Result);
        _session.History.AddUser(ShellText.CmdHistoryUser(kind, command, background));
        _session.History.AddAssistant(reply);
        _log.Add(line, run.Result);
        _lastReply = run.Result;
        LogSideTurn(line, run.Result, default, run.Cancelled, effective);
    }
}
