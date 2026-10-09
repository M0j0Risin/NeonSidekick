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
    /// </summary>
    private async Task HandleCmdAsync(string args, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (!TryParseCmd(args, effective, out var kind, out string command, out string? error))
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

            run = await RunCmdAsync(request, kind, executable, workdir, effective, cmdCts.Token).ConfigureAwait(false);
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
            KeepCmdRun(args, request, done, effective);
        }

        DrainDiagnostics();
    }

    /// <summary>
    /// <c>/cmd</c>'s argument: <c>--shell &lt;shell&gt;</c> first when given, then the command; the shell otherwise the
    /// <c>Shell default</c>'s. False with the error line for a bare <c>/cmd</c> or a shell that is none of <see cref="ShellKinds.Names"/>.
    /// </summary>
    internal static bool TryParseCmd(string args, AppSettingsData effective, out ShellKind kind, out string command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(effective);
        kind = ShellKinds.Resolve(effective);
        command = args.Trim();
        error = null;
        if (command.StartsWith(ShellText.CmdShellOption + " ", StringComparison.OrdinalIgnoreCase) || string.Equals(command, ShellText.CmdShellOption, StringComparison.OrdinalIgnoreCase))
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
        }

        if (command.Length == 0)
        {
            error = ShellText.CmdUsage;
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>/cmd</c>'s argument list: <c>--shell</c> while the first word is being typed, then <c>--shell &lt;name&gt;</c> for each of
    /// <see cref="ShellKinds.Names"/>; nothing once the command has begun.
    /// </summary>
    internal static IReadOnlyList<UI.CompletionItem> CmdChoices(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        string optionSpace = ShellText.CmdShellOption + " ";
        if (argText.StartsWith(optionSpace, StringComparison.OrdinalIgnoreCase))
        {
            string rest = argText[optionSpace.Length..];
            return rest.Contains(' ', StringComparison.Ordinal)
                ? []
                : UI.MentionCompleter.Matches(ShellKinds.Names.Select(name => new UI.CompletionItem(optionSpace + name, ShellKinds.Describe(name))).ToList(), argText);
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches([new(ShellText.CmdShellOption, ShellText.CmdShellNote)], argText);
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
            void Draw()
            {
                foreach (var line in session.Output.Since(cursor, out cursor))
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
            var lines = session.Output.Lines();
            string body = ShellText.Body(lines);
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
            return new CmdRun(ShellText.Result(header, lines, maxChars, spill), body.Length, cancelled);
        }
    }

    /// <summary>
    /// A run into the history as a tagged pair (a cancelled one too: the line did run), the store as a turn, and <c>/copy</c>'s
    /// log, the reply <c>/copy</c> takes last.
    /// </summary>
    private void KeepCmdRun(string args, CommandRequest request, CmdRun run, AppSettingsData effective)
    {
        string reply = ShellText.CmdHistoryReply(run.Result);
        _session.History.AddUser(ShellText.CmdHistoryUser(request.Kind, request.Command));
        _session.History.AddAssistant(reply);
        string line = ShellText.CmdWord + " " + args.Trim();
        _log.Add(line, run.Result);
        _lastReply = run.Result;
        LogSideTurn(line, run.Result, default, run.Cancelled, effective);
    }
}
