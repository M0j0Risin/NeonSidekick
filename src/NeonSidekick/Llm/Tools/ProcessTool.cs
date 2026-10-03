using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>process(action, session_id?, data?, timeout?, offset?, limit?)</c> (2026-09-21): the model's
/// hands on the background processes <c>run_command</c> started (<see cref="ProcessRegistry"/>).
/// <c>list</c> shows them all; <c>poll</c> answers the state and the lines since the last poll;
/// <c>log</c> a numbered window of lines (the last <see cref="DefaultLogLimit"/> without
/// <c>offset</c>); <c>wait</c> blocks up to <c>timeout</c> seconds (on the tool's clock, so a manual
/// one drives it in tests) and answers as a poll would; <c>kill</c> ends the tree; <c>write</c> /
/// <c>submit</c> send text to stdin (submit with a newline); <c>close</c> forgets a finished one.
/// Every result is one text with a header line (<see cref="ShellText"/>), the output cut at
/// <c>Shell output max chars</c> without a spill — the ring keeps the last lines, <c>log</c> reaches
/// them. Any unique prefix of an id will do. Offered with <c>run_command</c>; the gate is not
/// consulted here — what runs was approved when it started. The police is (2026-09-22, the user's
/// call): with <c>Shell police outside paths</c> on, the text <c>write</c> / <c>submit</c> send is read by
/// <see cref="PathPolice"/> before it goes — a background shell typed <c>cd C:\</c> would be the one
/// hole left — and refused with <see cref="ShellText.OutsidePath"/>, nothing sent.
/// </summary>
public sealed class ProcessTool : AIFunction
{
    public const string ToolName = "process";
    public const string ActionArgument = "action";
    public const string SessionIdArgument = "session_id";
    public const string DataArgument = "data";
    public const string TimeoutArgument = "timeout";
    public const string OffsetArgument = "offset";
    public const string LimitArgument = "limit";

    public const string ListAction = "list";
    public const string PollAction = "poll";
    public const string LogAction = "log";
    public const string WaitAction = "wait";
    public const string KillAction = "kill";
    public const string WriteAction = "write";
    public const string SubmitAction = "submit";
    public const string CloseAction = "close";

    public static readonly IReadOnlyList<string> Actions = [ListAction, PollAction, LogAction, WaitAction, KillAction, WriteAction, SubmitAction, CloseAction];

    public const int MinWaitSeconds = 1;
    public const int MaxWaitSeconds = 600;
    public const int DefaultWaitSeconds = 60;
    public const int MinLogLimit = 1;
    public const int MaxLogLimit = 2000;
    public const int DefaultLogLimit = 200;

    /// <summary>How long a killed child gets to drain before the kill line is answered.</summary>
    public static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "poll", "log", "wait", "kill", "write", "submit", "close"], "description": "list: every background process. poll: its state and the output since the last poll. log: a range of its lines. wait: block until it exits. kill: stop it. write / submit: text to its stdin (submit adds a newline). close: forget a finished one." },
            "session_id": { "type": "string", "description": "The id from run_command (proc_ and hex); a unique prefix is enough. Every action but list needs it." },
            "data": { "type": "string", "description": "For write and submit: the text to send." },
            "timeout": { "type": "integer", "description": "For wait: seconds to block, {{MinWaitSeconds}} to {{MaxWaitSeconds}} (default {{DefaultWaitSeconds}}); what arrived so far comes back on a timeout." },
            "offset": { "type": "integer", "description": "For log: the first line to show, 1-based; leave it out for the last lines." },
            "limit": { "type": "integer", "description": "For log: how many lines, {{MinLogLimit}} to {{MaxLogLimit}} (default {{DefaultLogLimit}})." }
          },
          "required": ["action"]
        }
        """);

    private readonly ProcessRegistry _registry;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;
    private readonly CommandGate? _gate;

    /// <param name="gate">Where a write the police refuses is noted (<see cref="CommandGate.NoteRefused"/>, 2026-09-26: headless ends with exit code 3 on any refusal); null notes nothing. Writes are never judged by it — the process was approved when it started.</param>
    public ProcessTool(ProcessRegistry registry, WorkingDirectory files, Func<AppSettingsData> effective, CommandGate? gate = null)
    {
        _gate = gate;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description =>
        "The background processes run_command started: list them, poll or wait for one, read its log, send it input, kill it or forget it. " +
        "poll returns only what arrived since your last poll; log a numbered window of its lines.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The arguments of the seeded poll a notified exit rides into the next turn (<c>Assistant.PendingCalls</c>).</summary>
    public static IReadOnlyDictionary<string, object?> PollArguments(string sessionId) =>
        new Dictionary<string, object?> { [ActionArgument] = PollAction, [SessionIdArgument] = sessionId };

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string action = ToolArguments.ReadString(arguments, ActionArgument).Trim().ToLowerInvariant();
        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            return ShellText.BadAction(action, string.Join(", ", Actions));
        }

        int maxChars = Math.Clamp(_effective().ShellOutputMaxChars, AppSettingsData.MinShellOutputMaxChars, AppSettingsData.MaxShellOutputMaxChars);
        if (action == ListAction)
        {
            var all = _registry.List();
            var lines = new List<string> { ShellText.ListHeader(all.Count, all.Count(s => !s.HasExited)) };
            lines.AddRange(all.Select(ShellText.ListRow));
            return string.Join("\n", lines);
        }

        string prefix = ToolArguments.ReadString(arguments, SessionIdArgument).Trim();
        if (prefix.Length == 0)
        {
            return ShellText.SessionRequired;
        }

        switch (_registry.Find(prefix, out var session, out var matches))
        {
            case FindOutcome.None:
                return ShellText.NoProcess(prefix);
            case FindOutcome.Ambiguous:
                return ShellText.Ambiguous(prefix, matches);
        }

        switch (action)
        {
            case PollAction:
                return Poll(session!, maxChars);

            case WaitAction:
            {
                if (!ToolArguments.TryReadInt32(arguments, TimeoutArgument, out int? seconds, out _) || (seconds is { } given && (given < MinWaitSeconds || given > MaxWaitSeconds)))
                {
                    return ShellText.BadWait(MinWaitSeconds, MaxWaitSeconds);
                }

                int wait = seconds ?? DefaultWaitSeconds;
                bool exited = await session!.WaitAsync(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
                if (exited)
                {
                    return Poll(session, maxChars);
                }

                var fresh = session.Output.Since(session.PollCursor, out long next);
                session.PollCursor = next;
                return ShellText.Background(ShellText.StillRunning(session, wait, fresh.Count), fresh, maxChars);
            }

            case LogAction:
            {
                if (!ToolArguments.TryReadInt32(arguments, LimitArgument, out int? limitGiven, out _) || (limitGiven is { } l && (l < MinLogLimit || l > MaxLogLimit)))
                {
                    return ShellText.BadLimit(MinLogLimit, MaxLogLimit);
                }

                if (!ToolArguments.TryReadInt32(arguments, OffsetArgument, out int? offset, out _) || (offset is { } o && o < 1))
                {
                    return ShellText.BadOffset;
                }

                int limit = limitGiven ?? DefaultLogLimit;
                var window = offset is { } from
                    ? session!.Output.Slice(from, limit, out long first, out long last)
                    : session!.Output.Tail(limit, out first, out last);
                string header = ShellText.LogHeader(session, first, last);
                long firstKept = session.Output.FirstKeptLine;
                if (firstKept > 1 && (offset is null || offset < firstKept))
                {
                    header += "\n" + ShellText.LogGone(firstKept, OutputBuffer.DefaultMaxLines);
                }

                return ShellText.Background(header, window, maxChars);
            }

            case KillAction:
            {
                if (session!.HasExited)
                {
                    return ShellText.HasExited(session.Id, action);
                }

                session.Kill();
                await session.WaitAsync(KillGrace, cancellationToken).ConfigureAwait(false);
                return ShellText.KilledLine(session);
            }

            case WriteAction:
            case SubmitAction:
            {
                if (session!.HasExited)
                {
                    return ShellText.HasExited(session.Id, action);
                }

                string data = ToolArguments.ReadString(arguments, DataArgument);
                if (data.Length == 0 && action == WriteAction)
                {
                    return ShellText.DataRequired;
                }

                // The police (Shell police outside paths, 2026-09-22): what goes to a process's stdin is read like a command line, relative paths from where it started.
                if (_effective().ShellPoliceOutsidePaths && PathPolice.Judge(data, _files, session.Launch.WorkingDirectory, isScript: false, session.Kind) is { } outside)
                {
                    var refused = new CommandRequest(session.Kind, data, []);
                    DiagnosticLog.Info(ShellKinds.Category, ShellText.PolicedLogLine(refused, outside));
                    _gate?.NoteRefused(refused);
                    return ShellText.OutsidePath(outside);
                }

                bool line = action == SubmitAction;
                bool sent = await session.WriteAsync(line ? data + "\n" : data, cancellationToken).ConfigureAwait(false);
                return sent ? ShellText.Sent(session.Id, data.Length, line) : ShellText.HasExited(session.Id, action);
            }

            case CloseAction:
                return _registry.Close(session!) ? ShellText.Closed(session!) : ShellText.StillRunningError(session!.Id);

            default:
                return ShellText.BadAction(action, string.Join(", ", Actions));
        }
    }

    private static string Poll(ProcessSession session, int maxChars)
    {
        var fresh = session.Output.Since(session.PollCursor, out long next);
        session.PollCursor = next;
        return ShellText.Background(ShellText.PollHeader(session, fresh.Count), fresh, maxChars);
    }
}
