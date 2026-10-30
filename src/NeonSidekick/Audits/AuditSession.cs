using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Audits;

/// <summary>How much of a scope the passes have read (2026-10-18): the files read out of the scope's, and the ones no pass has read yet.</summary>
public sealed record AuditCoverage(int Read, int Scope, IReadOnlyList<string> Unread, IReadOnlyList<string> ReadFiles)
{
    /// <summary>
    /// The scope's files viewed this session that the sidecar does not hold yet, to merge into it (2026-10-19, the second review: worked out by a
    /// second pass over the scope, every file stat'ed again, on every <c>present_audit</c> call).
    /// </summary>
    public IReadOnlyList<string> Viewed { get; init; } = [];
}

/// <summary>
/// A code audit's state (2026-10-18, <c>/code-audit</c>), one per screen and one per headless run: the loop while one runs
/// (<see cref="PlanGapLoop"/>, reused: "gaps" are any change to the findings), the report, the scope, the focus and the brief. Written on the
/// turn task (<c>present_audit</c>) and on the idle loop, never both at once, as <see cref="PlanSession"/>. Never kept in the session row: a
/// report left <c>auditing</c> after a restart lists as stopped, and <c>/code-audit resume</c> picks it up.
/// <para>It holds the report's lock too (2026-10-29, <see cref="AuditFiles.LockPathFor"/>: two apps auditing one report each saved the whole file,
/// and one overwrote the other's findings): <see cref="Hold"/> takes it for a start or resume about to begin (a pending hold), <see cref="Start"/>
/// makes it the running audit's, <see cref="Release"/> lets a pending one go when that start fails, and <see cref="Stop"/> lets go of both. The
/// running audit's and a pending one are kept apart because a start or resume can come while another audit of this screen still runs.</para>
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

    /// <summary>
    /// The folder the audit started in, null between audits (2026-10-19, after the Ralph loop's reviews: the audit read and wrote its report through
    /// the working directory as the settings have it, so a change mid-audit sent its writes and coverage to another folder's <c>.neon/audits/</c>).
    /// </summary>
    public string? Root { get; private set; }

    private WorkingDirectory? _pinned;

    /// <summary>What <see cref="Turn"/> last built its directive from, and that state (see <see cref="Turn"/>).</summary>
    private (PlanGapLoop Loop, int Pass, int Version, bool AskUser, int ToolBudget, int MaxToolIterations)? _turnKey;

    private AuditDirectiveState? _turnState;

    /// <summary>Bumped by <see cref="Changed"/>: a directive built before it is stale.</summary>
    private int _version;

    /// <summary>
    /// A report's lock as this session holds it: the working directory pinned where it was taken (a working-folder change mid-audit still lets go
    /// of the right one), the report, the lock, and whether the drive couldn't hold one (<see cref="LockTake.Unsupported"/>: the audit runs
    /// unlocked, and nothing is let go). <paramref name="Stamp"/> (2026-10-30, review F72): the report's length and write time after this
    /// process's last save of it, so a lock that went is taken back only over a report nobody else wrote since.
    /// </summary>
    private sealed record HeldLock(WorkingDirectory Files, string Report, string LockPath, bool Unlocked, (long Length, DateTime Written)? Stamp = null)
    {
        public bool Same(HeldLock other) => RalphSnapshot.SameRoot(Files.Root, other.Files.Root) && AuditPath.Comparer.Equals(LockPath, other.LockPath);

        /// <summary>Whether this is the lock of <paramref name="report"/> saved through <paramref name="files"/>.</summary>
        public bool Holds(WorkingDirectory files, string report) => RalphSnapshot.SameRoot(Files.Root, files.Root) && AuditPath.Comparer.Equals(Report, report);
    }

    /// <summary>The running audit's lock; null between audits (or one started with no hold).</summary>
    private HeldLock? _held;

    /// <summary>A lock taken for a start or resume that hasn't begun yet.</summary>
    private HeldLock? _pending;

    private const string LogCategory = NeonSidekick.App.ChatScreen.AppCategory;

    /// <summary>The working directory the report and its coverage sidecar are read and written through: pinned to <see cref="Root"/> while one runs.</summary>
    public WorkingDirectory Files => _pinned ?? _files;

    /// <summary>
    /// The working directory is no longer the one the audit started in: its passes would read another folder's code, so <c>present_audit</c>
    /// records nothing and the audit ends after the pass.
    /// </summary>
    public bool Moved => Root is not null && !RalphSnapshot.SameRoot(_files.Root, Root);

    /// <summary>The report's path, relative to the working directory.</summary>
    public string? Path { get; private set; }

    /// <summary>The report's name: its file name without <c>.md</c>.</summary>
    public string Name => Path is { } path ? System.IO.Path.GetFileNameWithoutExtension(path) : "";

    public AuditScope? Scope { get; private set; }

    public IReadOnlyList<string> Focus { get; private set; } = AuditFocus.Dimensions;

    public string Brief { get; private set; } = "";

    /// <summary>A pass under way: a loop past its first pass's start and not yet converged.</summary>
    public bool Reviewing => Running is { Pending: false, Converged: false };

    /// <summary>
    /// A pass started and the audit not stopped yet, the pass that converged included (2026-10-19, the second review: the tools' narrowing read
    /// <see cref="Reviewing"/>, false the moment <c>present_audit</c> converged, so an advisor asked later in that turn was offered the web).
    /// What the turn's tools and the asking tools' bar read; the directive still reads <see cref="Reviewing"/>.
    /// </summary>
    public bool InPass => Running is { Pending: false };

    /// <summary>An audit runs (its loop not stopped yet).</summary>
    public bool Active => Running is not null;

    /// <summary>The strip's pass while one is under way: <c>2/5</c>, <c>2</c> with no limit; null otherwise.</summary>
    public string? StripPass => Running is { Pending: false } loop ? AuditText.StripPass(loop.Pass, loop.Limit) : null;

    /// <summary>The lock the running audit holds, relative to the folder it was taken in; null with none (or on a drive that can't hold one).</summary>
    public string? HeldLockPath => _held is { Unlocked: false } held ? held.LockPath : null;

    /// <summary>
    /// The lock of the report at <paramref name="path"/> taken for a start or resume about to begin (2026-10-29): true when taken (free, stale, or
    /// this process's already), or when its drive can't hold one (<paramref name="notice"/> then says it runs unprotected, the user's call); false
    /// with <paramref name="error"/> while another live NeonSidekick holds it, or when it can't be written. A refused hold leaves the running audit
    /// and its lock as they were. The caller calls <see cref="Start"/> next, or <see cref="Release"/> when its step fails.
    /// </summary>
    public bool Hold(string path, out string? error, out string? notice) => Take(path, out error, out notice) is LockTake.Taken or LockTake.Unsupported;

    /// <summary><see cref="Hold"/>, with what the take found (a fresh start tries another name on <see cref="LockTake.Held"/>).</summary>
    private LockTake Take(string path, out string? error, out string? notice)
    {
        ArgumentNullException.ThrowIfNull(path);
        error = null;
        notice = null;
        Release();
        var files = _files.PinnedAt(_files.Root);
        string lockPath = AuditFiles.LockPathFor(path);
        var take = OwnerLock.TryLock(files, lockPath, OwnerLock.Me, OwnerLock.LiveOwner);
        switch (take)
        {
            case LockTake.Taken:
                _pending = new HeldLock(files, path, lockPath, Unlocked: false);
                DiagnosticLog.Info(LogCategory, $"code audit: locked {lockPath}");
                break;
            case LockTake.Unsupported:
                _pending = new HeldLock(files, path, lockPath, Unlocked: true);
                notice = AuditText.UnlockedNotice(path);
                DiagnosticLog.Info(LogCategory, $"code audit: {lockPath} can't be held on its drive; running unlocked");
                break;
            case LockTake.Unwritable:
                error = AuditText.LockWriteError(lockPath);
                DiagnosticLog.Info(LogCategory, $"code audit: refused {path}: {lockPath} couldn't be written");
                break;
            default:
                error = AuditFiles.HeldError(files, path);
                DiagnosticLog.Info(LogCategory, $"code audit: refused {path}: {lockPath} is held");
                break;
        }

        return take;
    }

    /// <summary>
    /// A start or resume begun under the report's lock (2026-10-30, review F80: the screen's start and resume and headless each spelled the
    /// hold-step-release dance out): the lock held (<see cref="Hold"/>), then <paramref name="step"/> run with the report's path, which writes
    /// the report and calls <see cref="Start"/>, its loop the answer. A step that answers null or throws lets the hold go. Null with
    /// <paramref name="error"/> when the hold was refused; <paramref name="notice"/> the unlocked drive's warning, to say once begun.
    /// <paramref name="another"/> (a fresh start's next free name, review F3): tried, up to three names in all, while the lock of the one chosen is
    /// held by another NeonSidekick that chose it meanwhile.
    /// </summary>
    public PlanGapLoop? Begin(string path, Func<string, PlanGapLoop?> step, out string? error, out string? notice, Func<string>? another = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(step);
        var take = Take(path, out error, out notice);
        for (int tries = 1; take == LockTake.Held && another is not null && tries < 3; tries++)
        {
            path = another();
            take = Take(path, out error, out notice);
        }

        if (take is not (LockTake.Taken or LockTake.Unsupported))
        {
            return null;
        }

        PlanGapLoop? loop = null;
        try
        {
            loop = step(path);
        }
        finally
        {
            if (loop is null)
            {
                Release();
            }
        }

        return loop;
    }

    /// <summary>
    /// <paramref name="report"/> saved at <paramref name="path"/> through <paramref name="files"/> (<see cref="Files"/> when null), as
    /// <see cref="AuditFiles.Save(WorkingDirectory, string, AuditReport, DateTimeOffset)"/>, its stamp kept for the lock that holds it
    /// (<see cref="Saved"/>). Every save of a report this session may hold goes through here.
    /// </summary>
    public string? Save(string path, AuditReport report, DateTimeOffset now, WorkingDirectory? files = null)
    {
        files ??= Files;
        string? failed = AuditFiles.Save(files, path, report, now, out var stamp);
        if (failed is null)
        {
            Saved(files, path, stamp);
        }

        return failed;
    }

    /// <summary>
    /// The report at <paramref name="path"/> saved by this process just now (2026-10-30, review F72): the running or pending lock that holds it keeps
    /// <paramref name="stamp"/>, so <see cref="StillHolds"/> tells this process's own save from another's write. A null stamp leaves the old one,
    /// and a lock that went is then not taken back: the safe side.
    /// </summary>
    internal void Saved(WorkingDirectory files, string path, (long Length, DateTime Written)? stamp)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (stamp is null)
        {
            return;
        }

        if (_pending is { } pending && pending.Holds(files, path))
        {
            _pending = pending with { Stamp = stamp };
        }

        if (_held is { } held && held.Holds(files, path))
        {
            _held = held with { Stamp = stamp };
        }
    }

    /// <summary>
    /// A pending hold let go (its start failed or threw); never the running audit's own lock, which a refused resume of that same audit must not
    /// unlock. Nothing for a drive that holds no lock.
    /// </summary>
    public void Release()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            if (_held is not { } held || !held.Same(pending))
            {
                Let(pending);
            }
        }
    }

    /// <summary>
    /// Whether the running audit still holds its report (2026-10-29): this process's lock still, or taken back when it went (a sync client or a hand
    /// deleted it); false while another live NeonSidekick holds it, and the audit then stops writing. True with no lock to hold (none taken, or a
    /// drive that holds none), and when a lock that went can't be written again: the report's own save then says what failed.
    /// <para>2026-10-30, review F5 and F72: the lock is read first, and this process's own is held with no take (each check was a take). One that
    /// is missing or stale is taken back only while the report is as this process last saved it (<see cref="Saved"/>): another NeonSidekick that
    /// took it over, wrote and let go left no lock, and the take-back then overwrote its findings. One that can't be read counts as another's.</para>
    /// </summary>
    public bool StillHolds()
    {
        if (_held is not { Unlocked: false } held)
        {
            return true;
        }

        var owner = OwnerLock.ReadOwner(held.Files, held.LockPath);
        if (owner == OwnerLock.Me)
        {
            return true;
        }

        string? lost = owner is not null && OwnerLock.LiveOwner(owner) ? "another NeonSidekick holds it"
            : owner is null && AuditFiles.Exists(held.Files, held.LockPath) ? "it can't be read"
            : !Unchanged(held) ? "the report changed since this audit last saved it"
            : OwnerLock.TryLock(held.Files, held.LockPath, OwnerLock.Me, OwnerLock.LiveOwner) == LockTake.Held ? "another NeonSidekick took it"
            : null;
        if (lost is not null)
        {
            DiagnosticLog.Info(LogCategory, $"code audit: lost {held.LockPath}: {lost}");
        }

        return lost is null;
    }

    /// <summary>Whether the report is as this process last saved it: the length and write time <see cref="Saved"/> kept.</summary>
    private static bool Unchanged(HeldLock held) =>
        held.Stamp is { } saved && held.Files.TryStamp(held.Report) is { } now && now.Length == saved.Length && now.Written == saved.Written;

    private static void Let(HeldLock held)
    {
        if (!held.Unlocked)
        {
            OwnerLock.Unlock(held.Files, held.LockPath, OwnerLock.Me);
        }
    }

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A fresh loop over the report at <paramref name="path"/>: <paramref name="reviewer"/>, at most <paramref name="limit"/> passes (0 for no
    /// limit), ending on <paramref name="cleanPasses"/> clean passes in a row, the confirming passes rotating through the focus. One already
    /// running is replaced: a pending hold on this report becomes the running one, and the replaced audit's lock goes when it names another.
    /// </summary>
    public PlanGapLoop Start(string path, AuditScope scope, IReadOnlyList<string> focus, string brief, PlanReviewer reviewer, int limit, int cleanPasses)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(focus);
        var promoted = _pending is { } pending && AuditPath.Comparer.Equals(pending.Report, path) ? pending : null;
        if (_held is { } running && (promoted is null || !running.Same(promoted)))
        {
            Let(running);
        }

        _held = promoted;
        if (promoted is null)
        {
            Release();
        }

        _pending = null;
        Path = path;
        Root = _files.Root;
        _pinned = _files.PinnedAt(Root);
        Scope = scope;
        Focus = AuditFocus.Normalize(focus);
        Brief = brief ?? "";
        Changed();
        return Running = new PlanGapLoop(reviewer, limit, cleanPasses, AuditFocus.FocusCount(Focus));
    }

    /// <summary>The report or the loop changed (a <c>present_audit</c> call recorded something): the next <see cref="Turn"/> reads them again.</summary>
    public void Changed()
    {
        _version++;
        _turnKey = null;
        _turnState = null;
    }

    /// <summary>The audit over, however it ended: everything forgotten (the report stays), its lock and any pending one let go.</summary>
    public void Stop()
    {
        Release();
        if (_held is { } held)
        {
            _held = null;
            Let(held);
        }

        Running = null;
        Path = null;
        Root = null;
        _pinned = null;
        Scope = null;
        Focus = AuditFocus.Dimensions;
        Brief = "";
        Changed();
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

        // A file viewed counts only in the audit's own folder: after a move, a read of the same name was another folder's file.
        var sidecar = AuditFiles.ReadCoverage(Files, path);
        bool here = !Moved;
        var read = new List<string>();
        var unread = new List<string>();
        var viewed = new List<string>();
        foreach (string file in scope.Files)
        {
            if (sidecar.Contains(file))
            {
                read.Add(file);
            }
            else if (here && _files.WasViewed(file))
            {
                read.Add(file);
                viewed.Add(file);
            }
            else
            {
                unread.Add(file);
            }
        }

        return new AuditCoverage(read.Count, scope.Files.Count + scope.MoreFiles, unread, read) { Viewed = viewed };
    }

    /// <summary>The scope's files viewed this session that the sidecar does not hold yet, to merge into it (<see cref="AuditCoverage.Viewed"/>).</summary>
    public IReadOnlyList<string> ViewedNow() => Coverage().Viewed;

    /// <summary>
    /// The turn's audit part while a pass is under way, else null: the directive (with the advisors and web lines through <c>Compose</c>) and
    /// <paramref name="tool"/>, <c>present_audit</c>. <paramref name="askUser"/> is <c>Code audit ask user</c>; <paramref name="toolBudget"/>
    /// <c>Code audit pass tool calls</c>, named against <paramref name="maxToolIterations"/>.
    /// <para>2026-10-19, the review: built afresh on every call it parsed the report, read the sidecar and checked every scope file (up to
    /// 5,000) each time the turn's inputs were built — every advisor call and <c>/sys</c> too. Now the directive's state is built once per pass
    /// and kept until <see cref="Changed"/> (a recorded <c>present_audit</c> call, a start or a stop) or a setting it names changes; an edit
    /// made in an editor mid-pass reaches the next pass, as the files read mid-pass reach its unread list.</para>
    /// </summary>
    public PlanTurn? Turn(AIFunction tool, bool askUser, int toolBudget, int maxToolIterations)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!Reviewing || Running is not { } loop || Path is not { } path || Scope is not { } scope)
        {
            return null;
        }

        var key = (loop, loop.Pass, _version, askUser, toolBudget, maxToolIterations);
        if (_turnState is { } kept && _turnKey == key)
        {
            return new PlanTurn(AuditText.Directive(kept), tool) { Compose = (advisors, research) => AuditText.Directive(kept, advisors, research) };
        }

        var report = AuditFiles.Read(Files, path);
        var coverage = Coverage();
        List<(string Id, string Title)> open = report?.Open.Select(f => (f.Id, f.Title)).ToList() ?? [];
        var passReviewer = loop.PassReviewer;
        var state = new AuditDirectiveState(path, scope.Label, scope.Files, scope.MoreFiles, scope.Skipped, coverage.Unread,
            AuditFocus.PassFocus(Focus, loop.Confirmation), Brief, open, report?.SettledCount ?? 0,
            loop.Pass, loop.Limit, loop.Reviewer, passReviewer, loop.CleanStreak, loop.CleanPasses, loop.Confirmation,
            askUser, toolBudget, maxToolIterations, scope.Deleted.Count > 0, scope.IsGit, scope.Cut);
        _turnKey = key;
        _turnState = state;
        return new PlanTurn(AuditText.Directive(state), tool) { Compose = (advisors, research) => AuditText.Directive(state, advisors, research) };
    }
}
