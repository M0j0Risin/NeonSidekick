using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Shell;

/// <summary>What the user chose on the approval pane (2026-09-21, the user's four words).</summary>
public enum CommandChoice
{
    /// <summary>Not this time: the tool answers <see cref="ShellText.Denied"/> and the model is told never to work around it.</summary>
    Deny,

    /// <summary>This one call; the same command asks again next time.</summary>
    Once,

    /// <summary>The command's prefixes for the rest of the process (<see cref="CommandAllowList.AllowSession"/>).</summary>
    Session,

    /// <summary>The prefixes for good: saved into the profile's <c>Shell allowed commands</c> (<see cref="CommandAllowList.AllowPermanently"/>).</summary>
    Permanent,
}

/// <summary>
/// One thing to judge: the shell (or, for a script, the language) it runs under, the line as the
/// model sent it, its prefixes (<see cref="CommandPrefix.All"/>, or the one <c>code:&lt;language&gt;</c>
/// pseudo-prefix of a script), and whether it is a script — the pane words its rows by that.
/// </summary>
public sealed record CommandRequest(string Kind, string Command, IReadOnlyList<string> Prefixes, bool IsScript = false);

/// <summary>The gate's answer: run it, or the <c>Error:</c> sentence the tool returns instead.</summary>
public readonly record struct CommandVerdict(bool Allowed, string Error)
{
    public static readonly CommandVerdict Run = new(true, "");

    public static CommandVerdict Refuse(string error) => new(false, error);
}

/// <summary>
/// What stands between a shell tool and the shell (2026-09-21): the setting <c>Shell command
/// policy</c> read at every call (<see cref="CommandPolicy.Resolve"/>) — <c>yolo</c> runs
/// everything, <c>ask</c> runs what the allow list covers and puts the rest to the user through
/// <paramref name="asker"/> (the screen's approval pane; null headless, where nothing can ask and
/// the list alone decides), <c>off</c> refuses (the tools are not offered then; this is the guard
/// behind that). A pick of Session or Permanent widens the <see cref="CommandAllowList"/> before the
/// answer. Every judgement is one log line under <see cref="ShellKinds.Category"/>. The one
/// gate is shared by <c>run_command</c> and <c>execute_code</c>.
/// </summary>
public sealed class CommandGate
{
    private readonly Func<AppSettingsData> _effective;
    private readonly CommandAllowList _allowList;
    private readonly Func<CommandRequest, CancellationToken, Task<CommandChoice?>>? _asker;

    /// <param name="effective">The settings the policy is read from at every call.</param>
    /// <param name="allowList">The session's and the profile's allowed prefixes.</param>
    /// <param name="asker">Shows the request and waits: the choice, or null when nothing could ask (no watcher, the pane off) — read as <see cref="CommandChoice.Deny"/> with the no-screen sentence. Null headless.</param>
    public CommandGate(Func<AppSettingsData> effective, CommandAllowList allowList, Func<CommandRequest, CancellationToken, Task<CommandChoice?>>? asker)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _allowList = allowList ?? throw new ArgumentNullException(nameof(allowList));
        _asker = asker;
    }

    /// <summary>The allow list the gate judges by.</summary>
    public CommandAllowList AllowList => _allowList;

    private readonly Lock _refusalsLock = new();
    private readonly List<string> _refusals = new();

    /// <summary>
    /// Every command this gate kept from running (2026-09-26), in order: refused by the gate (not approved, never
    /// asked, denied) or by the path police before it (<see cref="NoteRefused"/>). Not the <c>off</c> policy's
    /// refusals — the tools are not offered then. Headless reads it at the end of the run for its summary notice
    /// and exit code 3; the screen never does. A snapshot; each entry is the command line, or <c>python script</c> for an <c>execute_code</c> script.
    /// </summary>
    public IReadOnlyList<string> Refusals
    {
        get { lock (_refusalsLock) { return _refusals.ToArray(); } }
    }

    /// <summary>Records <paramref name="request"/> in <see cref="Refusals"/>: the gate's own refusals, and the path police's in the shell tools.</summary>
    public void NoteRefused(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_refusalsLock)
        {
            _refusals.Add(request.IsScript ? request.Kind + " script" : request.Command);
        }
    }

    /// <summary>The policy as the settings stand now.</summary>
    public CommandPolicyMode Policy => CommandPolicy.Resolve(_effective());

    /// <summary>Whether anything can be asked: an asker was given (the screen; never headless).</summary>
    public bool CanAsk => _asker is not null;

    /// <summary>
    /// The verdict on <paramref name="request"/>: run, or the sentence to answer with. <c>ask</c> with
    /// every prefix allowed runs without asking; else the asker decides and a Session or Permanent
    /// pick is recorded first. The token is the turn's: cancelled, the wait throws as any tool's does.
    /// </summary>
    public async Task<CommandVerdict> JudgeAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = Policy;
        switch (policy)
        {
            case CommandPolicyMode.Off:
                DiagnosticLog.Info(ShellKinds.Category, ShellText.RefusedLogLine(request, "policy off"));
                return CommandVerdict.Refuse(ShellText.PolicyOff);
            case CommandPolicyMode.Yolo:
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ApprovedLogLine(request, "yolo"));
                return CommandVerdict.Run;
        }

        if (request.Prefixes.Count > 0 && _allowList.AllowsAll(request.Prefixes))
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.ApprovedLogLine(request, "on the allow list"));
            return CommandVerdict.Run;
        }

        if (_asker is null)
        {
            DiagnosticLog.Info(ShellKinds.Category, ShellText.RefusedLogLine(request, "no screen to ask on"));
            NoteRefused(request);
            return CommandVerdict.Refuse(ShellText.NotAskable(_allowList.Snapshot()));
        }

        var choice = await _asker(request, cancellationToken).ConfigureAwait(false);
        switch (choice)
        {
            case CommandChoice.Once:
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ApprovedLogLine(request, "allowed once"));
                return CommandVerdict.Run;
            case CommandChoice.Session:
                _allowList.AllowSession(request.Prefixes);
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ApprovedLogLine(request, "allowed for the session: " + string.Join(", ", request.Prefixes)));
                return CommandVerdict.Run;
            case CommandChoice.Permanent:
                _allowList.AllowPermanently(request.Prefixes);
                DiagnosticLog.Info(ShellKinds.Category, ShellText.ApprovedLogLine(request, "allowed always: " + string.Join(", ", request.Prefixes)));
                return CommandVerdict.Run;
            case CommandChoice.Deny:
                DiagnosticLog.Info(ShellKinds.Category, ShellText.RefusedLogLine(request, "denied by the user"));
                NoteRefused(request);
                return CommandVerdict.Refuse(ShellText.Denied(request));
            default:
                DiagnosticLog.Info(ShellKinds.Category, ShellText.RefusedLogLine(request, "never asked"));
                NoteRefused(request);
                return CommandVerdict.Refuse(ShellText.NotAskable(_allowList.Snapshot()));
        }
    }
}
