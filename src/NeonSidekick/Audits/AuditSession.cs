using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Audits;

/// <summary>How much of a scope the passes have read (2026-10-18): the files read out of the scope's, and the ones no pass has read yet.</summary>
public sealed record AuditCoverage(int Read, int Scope, IReadOnlyList<string> Unread, IReadOnlyList<string> ReadFiles);

/// <summary>
/// A code audit's state (2026-10-18, <c>/code-audit</c>), one per screen and one per headless run: the loop while one runs
/// (<see cref="PlanGapLoop"/>, reused: "gaps" are any change to the findings), the report, the scope, the focus and the brief. Written on the
/// turn task (<c>present_audit</c>) and on the idle loop, never both at once, as <see cref="PlanSession"/>. Never kept in the session row: a
/// report left <c>auditing</c> after a restart lists as stopped, and <c>/code-audit resume</c> picks it up.
/// </summary>
public sealed class AuditSession
{
    private readonly WorkingDirectory _files;

    public AuditSession(WorkingDirectory files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>The loop while an audit runs; null otherwise.</summary>
    public PlanGapLoop? Running { get; private set; }

    /// <summary>The report's path, relative to the working directory.</summary>
    public string? Path { get; private set; }

    /// <summary>The report's name: its file name without <c>.md</c>.</summary>
    public string Name => Path is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : "";

    public AuditScope? Scope { get; private set; }

    public IReadOnlyList<string> Focus { get; private set; } = AuditFocus.Dimensions;

    public string Brief { get; private set; } = "";

    /// <summary>A pass under way: a loop past its first pass's start and not yet converged.</summary>
    public bool Reviewing => Running is { Pending: false, Converged: false };

    /// <summary>An audit runs (its loop not stopped yet).</summary>
    public bool Active => Running is not null;

    /// <summary>The strip's pass while one is under way: <c>2/5</c>, <c>2</c> with no limit; null otherwise.</summary>
    public string? StripPass => Running is { Pending: false } loop ? AuditText.StripPass(loop.Pass, loop.Limit) : null;

    /// <summary>
    /// A fresh loop over the report at <paramref name="path"/>: <paramref name="reviewer"/>, at most <paramref name="limit"/> passes (0 for no
    /// limit), ending on <paramref name="cleanPasses"/> clean passes in a row, the confirming passes rotating through the focus. One already
    /// running is replaced.
    /// </summary>
    public PlanGapLoop Start(string path, AuditScope scope, IReadOnlyList<string> focus, string brief, PlanReviewer reviewer, int limit, int cleanPasses)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(focus);
        Path = path;
        Scope = scope;
        Focus = AuditFocus.Normalize(focus);
        Brief = brief ?? "";
        return Running = new PlanGapLoop(reviewer, limit, cleanPasses, AuditFocus.FocusCount(Focus));
    }

    /// <summary>The audit over, however it ended: everything forgotten (the report stays).</summary>
    public void Stop()
    {
        Running = null;
        Path = null;
        Scope = null;
        Focus = AuditFocus.Dimensions;
        Brief = "";
    }

    /// <summary>
    /// The coverage now: the scope's files the sidecar holds or that <c>read_file</c> returned text of this session (<see cref="WorkingDirectory.WasViewed"/>),
    /// and the rest. Empty with no audit.
    /// </summary>
    public AuditCoverage Coverage()
    {
        if (Scope is not { } scope || Path is not { } path)
        {
            return new AuditCoverage(0, 0, [], []);
        }

        var sidecar = AuditFiles.ReadCoverage(_files, path);
        var read = new List<string>();
        var unread = new List<string>();
        foreach (string file in scope.Files)
        {
            (sidecar.Contains(file) || _files.WasViewed(file) ? read : unread).Add(file);
        }

        return new AuditCoverage(read.Count, scope.Files.Count + scope.MoreFiles, unread, read);
    }

    /// <summary>The scope's files viewed this session, to merge into the sidecar.</summary>
    public IReadOnlyList<string> ViewedNow() => Scope is { } scope ? scope.Files.Where(_files.WasViewed).ToList() : [];

    /// <summary>
    /// The turn's audit part while a pass is under way, else null: the directive (with the advisors and web lines through <c>Compose</c>) and
    /// <paramref name="tool"/>, <c>present_audit</c>. <paramref name="askUser"/> is <c>LLM code audit ask user</c>; <paramref name="toolBudget"/>
    /// <c>LLM code audit pass tool calls</c>, named against <paramref name="maxToolIterations"/>.
    /// </summary>
    public PlanTurn? Turn(AIFunction tool, bool askUser, int toolBudget, int maxToolIterations)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!Reviewing || Running is not { } loop || Path is not { } path || Scope is not { } scope)
        {
            return null;
        }

        var report = AuditFiles.Read(_files, path);
        var coverage = Coverage();
        List<(string Id, string Title)> open = report?.Open.Select(f => (f.Id, f.Title)).ToList() ?? [];
        var passReviewer = loop.PassReviewer;
        var state = new AuditDirectiveState(path, scope.Label, scope.Files, scope.MoreFiles, scope.Skipped, coverage.Unread,
            AuditFocus.PassFocus(Focus, loop.Confirmation), Brief, open, report?.SettledCount ?? 0,
            loop.Pass, loop.Limit, loop.Reviewer, passReviewer, loop.CleanStreak, loop.CleanPasses, loop.Confirmation,
            askUser, toolBudget, maxToolIterations, scope.Deleted.Count > 0, scope.IsGit);
        return new PlanTurn(AuditText.Directive(state), tool) { Compose = (advisors, research) => AuditText.Directive(state, advisors, research) };
    }
}
