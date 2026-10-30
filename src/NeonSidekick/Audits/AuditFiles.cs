using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Audits;

/// <summary>
/// One report as <c>/code-audit open</c> lists it (2026-10-18): its path, status, the open findings by severity and their count, the settled
/// count, the passes line, the last update, whether every finding is ticked, whether it could not be read whole, and whether it is the
/// audit running now (an <c>auditing</c> report with no live loop lists as stopped). <paramref name="Elsewhere"/> (2026-10-29): another live
/// NeonSidekick holds its lock, so it lists as running elsewhere instead.
/// </summary>
public sealed record AuditListing(
    string Path,
    AuditStatus Status,
    IReadOnlyList<(AuditSeverity Severity, int Count)> Open,
    int OpenCount,
    int Settled,
    string Passes,
    DateTimeOffset? Updated,
    bool Fixed,
    bool Unreadable = false,
    bool Live = false,
    bool Elsewhere = false)
{
    /// <summary>The name the commands take: the file name without <c>.neon/audits/</c> or <c>.md</c>.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The code audit's reports under the working directory (2026-10-18, <see cref="PlanFiles"/>' shape): a report's path chosen, read whole,
/// saved, found by name and listed newest first, with its coverage sidecar (<c>&lt;name&gt;.coverage.txt</c>, the files the passes read,
/// merged across runs, grouped by folder). Every path stays inside <see cref="Folder"/>; every write goes through
/// <see cref="WorkingDirectory.WriteText"/>, atomic and sandboxed, and a report past <see cref="WorkingDirectory.MaxWriteChars"/> is refused
/// before it is written.
/// </summary>
public static class AuditFiles
{
    /// <summary>The folder the reports go in: <c>.neon/audits</c>.</summary>
    public const string Folder = PlanSlug.AppFolder + "/audits";

    /// <summary>The coverage sidecar's ending, in place of <c>.md</c>.</summary>
    public const string CoverageExtension = ".coverage.txt";

    /// <summary>The lock's ending, in place of <c>.md</c> (2026-10-29).</summary>
    public const string LockExtension = ".lock";

    /// <summary>The first free path for <paramref name="label"/> and <paramref name="now"/>'s date: <c>src-web-2026-10-18</c>, then <c>-2</c>…</summary>
    public static string Choose(WorkingDirectory files, string label, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(label);
        string slug = PlanSlug.From(label + " " + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return PlanSlug.Choose(Folder, slug, path => Taken(files, path), now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    }

    /// <summary>The report's whole text, or null when it cannot be read whole (up to <see cref="WorkingDirectory.MaxWriteChars"/>, never a cut read).</summary>
    public static string? ReadWhole(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        var read = files.ReadText(path, null, null, WorkingDirectory.MaxWriteChars);
        return read.Outcome == FileOutcome.Ok && !read.Truncated && !read.LineCut ? read.Text : null;
    }

    /// <summary>The report at <paramref name="path"/>, parsed, or null when it cannot be read whole.</summary>
    public static AuditReport? Read(WorkingDirectory files, string path) => ReadWhole(files, path) is { } text ? AuditReport.Parse(text) : null;

    /// <summary>Whether a file is at <paramref name="path"/> in the sandbox.</summary>
    public static bool Exists(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full);
    }

    /// <summary>
    /// <paramref name="report"/> written to <paramref name="path"/>, its <c>updated:</c> stamped <paramref name="now"/> (and <c>created:</c> once):
    /// null when done, else the sentence (<see cref="AuditText.ReportFullResult"/> past the size, <c>FileText</c>'s for a write that failed).
    /// </summary>
    public static string? Save(WorkingDirectory files, string path, AuditReport report, DateTimeOffset now) => Save(files, path, report, now, out _);

    /// <summary>
    /// <see cref="Save(WorkingDirectory, string, AuditReport, DateTimeOffset)"/>, <paramref name="stamp"/> the file's length and write time just
    /// after (null when it failed, or the stamp couldn't be read): what <see cref="AuditSession.Saved"/> keeps, so a lock that went is taken back
    /// only over a report nobody else wrote since (2026-10-30, review F72).
    /// </summary>
    public static string? Save(WorkingDirectory files, string path, AuditReport report, DateTimeOffset now, out (long Length, DateTime Written)? stamp)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(report);
        stamp = null;
        report.Header = report.Header with { Created = report.Header.Created ?? now, Updated = now, NextId = report.NextIdNumber };
        string text = report.Render();
        if (text.Length > WorkingDirectory.MaxWriteChars)
        {
            return AuditText.ReportFullResult;
        }

        var written = WriteGuard.AsApp(() => files.WriteText(path, text, overwrite: true));
        if (written.Outcome != FileOutcome.Ok)
        {
            return FileText.Error(written.Outcome, path, "write", written.Detail);
        }

        stamp = files.TryStamp(path) is { } after ? (after.Length, after.Written) : null;
        return null;
    }

    /// <summary>
    /// The path of the report <paramref name="name"/> names — <c>x</c>, <c>x.md</c>, <c>audits/x.md</c> or <c>.neon/audits/x.md</c>, case aside as
    /// the file system has it — when it exists directly under <see cref="Folder"/>; null otherwise.
    /// </summary>
    public static string? Resolve(WorkingDirectory files, string name)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(name);
        string bare = name.Trim().Replace('\\', '/');
        if (bare.StartsWith(Folder + "/", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[(Folder.Length + 1)..];
        }
        else if (bare.StartsWith("audits/", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare["audits/".Length..];
        }

        if (bare.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[..^PlanSlug.Extension.Length];
        }

        if (bare.Length == 0 || bare.Contains('/', StringComparison.Ordinal) || bare.Contains("..", StringComparison.Ordinal) || bare.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        string path = Folder + "/" + bare + PlanSlug.Extension;
        return Exists(files, path) ? OnDisk(files, path) : null;
    }

    /// <summary>
    /// <paramref name="path"/> in the spelling its file has on disk (2026-10-30, review F82: on a case-blind file system a name typed in another case
    /// found the report, and the lock held under the typed spelling and the running audit's then read as two, so a resume let go of its own lock).
    /// The path as given when the folder can't be listed or holds no such name.
    /// </summary>
    private static string OnDisk(WorkingDirectory files, string path)
    {
        if (files.Resolve(Folder, forWrite: false, out string folder) != FileOutcome.Ok)
        {
            return path;
        }

        string name = Path.GetFileName(path);
        try
        {
            foreach (string full in Directory.EnumerateFiles(folder, "*" + PlanSlug.Extension))
            {
                string found = Path.GetFileName(full);
                if (string.Equals(found, name, StringComparison.Ordinal))
                {
                    return path;
                }

                if (string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                {
                    name = found;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }

        return Folder + "/" + name;
    }

    /// <summary>
    /// Every <c>.neon/audits/*.md</c>, newest first: by the header's <c>updated:</c>, else the file's write time (a hand-made or damaged
    /// header), a tie by name so the pick is stable. <paramref name="livePath"/> is the report of the audit running now, if any.
    /// </summary>
    public static IReadOnlyList<AuditListing> List(WorkingDirectory files, string? livePath = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Resolve(Folder, forWrite: false, out string folder) != FileOutcome.Ok || !Directory.Exists(folder))
        {
            return [];
        }

        List<string> found;
        try
        {
            found = Directory.EnumerateFiles(folder, "*" + PlanSlug.Extension).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var listings = new List<(AuditListing Listing, DateTimeOffset Order)>();
        foreach (string full in found)
        {
            string path = Folder + "/" + Path.GetFileName(full);
            bool live = livePath is not null && AuditPath.Comparer.Equals(livePath, path);
            if (ReadWhole(files, path) is not { } text)
            {
                listings.Add((new AuditListing(path, AuditStatus.Stopped, [], 0, 0, "", null, false, Unreadable: true, Live: live), WrittenAt(full)));
                continue;
            }

            var report = AuditReport.Parse(text);
            var updated = report.HasHeader ? report.Header.Updated : null;
            // The lock is read only for an auditing report no loop here runs, so the listing stays one read a report otherwise.
            bool elsewhere = !live && report.Header.Status == AuditStatus.Auditing && OwnerLock.HeldElsewhere(files, LockPathFor(path), out _);
            listings.Add((new AuditListing(path, report.Header.Status, report.OpenBySeverity(), report.OpenCount, report.SettledCount, report.Header.Passes, updated, report.Fixed, Live: live, Elsewhere: elsewhere),
                updated ?? WrittenAt(full)));
        }

        return listings.OrderByDescending(l => l.Order).ThenBy(l => l.Listing.Path, StringComparer.Ordinal).Select(l => l.Listing).ToList();
    }

    // ── The lock ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The lock beside <paramref name="reportPath"/>: <c>x.lock</c> for <c>x.md</c> (2026-10-29: two apps auditing one report each saved the whole
    /// file, and one overwrote the other's findings). Made as the Ralph loop's is (<see cref="OwnerLock"/>); <see cref="AuditSession"/> holds it.
    /// </summary>
    public static string LockPathFor(string reportPath)
    {
        ArgumentNullException.ThrowIfNull(reportPath);
        string bare = reportPath.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase) ? reportPath[..^PlanSlug.Extension.Length] : reportPath;
        return bare + LockExtension;
    }

    /// <summary>
    /// The error sentence when another live NeonSidekick holds <paramref name="reportPath"/>'s lock (or it can't be read), else null: what a fix
    /// and a discard ask first. Another machine's lock names that machine and where the lock is.
    /// </summary>
    public static string? HeldElsewhere(WorkingDirectory files, string reportPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        string lockPath = LockPathFor(reportPath);
        return OwnerLock.HeldElsewhere(files, lockPath, out var owner) ? LockError(reportPath, lockPath, owner, unreadable: owner is null) : null;
    }

    /// <summary>
    /// The refusal for a lock another holds: <see cref="AuditText.LockedOnHostError"/> for another machine's; <see cref="AuditText.UncheckedLockError"/>
    /// for one that can't be read (<paramref name="unreadable"/>) or whose owner's start time can't be read (2026-10-30, review F76/F86); else
    /// <see cref="AuditText.LockedError"/>.
    /// </summary>
    public static string LockError(string reportPath, string lockPath, LockOwner? owner, bool unreadable = false) =>
        owner is { Foreign: true, Host: { } host } ? AuditText.LockedOnHostError(reportPath, host, lockPath)
        : unreadable || (owner is not null && OwnerLock.CannotCheck(owner)) ? AuditText.UncheckedLockError(reportPath, lockPath)
        : AuditText.LockedError(reportPath);

    /// <summary>The refusal for <paramref name="reportPath"/>'s lock as it stands now, read once more (a take that came back held).</summary>
    internal static string HeldError(WorkingDirectory files, string reportPath)
    {
        string lockPath = LockPathFor(reportPath);
        var owner = OwnerLock.ReadOwner(files, lockPath);
        return LockError(reportPath, lockPath, owner, unreadable: owner is null && Exists(files, lockPath));
    }

    /// <summary>
    /// <paramref name="act"/> run while holding <paramref name="reportPath"/>'s lock (2026-10-30, review F2: a fix checked the lock, then read,
    /// marked and saved the report, and another NeonSidekick starting an audit in between had its findings overwritten): false with
    /// <paramref name="refusal"/> and nothing run while another holds it. A lock this process already held (its own audit of the report) stays
    /// held after; one taken here goes. A drive that holds no lock, or a lock that can't be written, runs <paramref name="act"/> unlocked, as before
    /// (a save that fails says so itself).
    /// </summary>
    public static bool WithLock(WorkingDirectory files, string reportPath, Action act, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(reportPath);
        ArgumentNullException.ThrowIfNull(act);
        refusal = null;
        string lockPath = LockPathFor(reportPath);
        bool ours = OwnerLock.ReadOwner(files, lockPath) == OwnerLock.Me;
        var take = OwnerLock.TryLock(files, lockPath, OwnerLock.Me, OwnerLock.LiveOwner);
        if (take == LockTake.Held)
        {
            refusal = HeldError(files, reportPath);
            return false;
        }

        try
        {
            act();
        }
        finally
        {
            if (take == LockTake.Taken && !ours)
            {
                OwnerLock.Unlock(files, lockPath, OwnerLock.Me);
            }
        }

        return true;
    }

    /// <summary>
    /// The report a bare <c>/code-audit resume|fix</c> picks: the newest that reads whole and that no other NeonSidekick is auditing (2026-10-30,
    /// review F4: the newest was picked, then refused as running elsewhere, while an older free one stood there). Null with none.
    /// </summary>
    public static AuditListing? Newest(IReadOnlyList<AuditListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);
        return listings.FirstOrDefault(r => !r.Unreadable && !r.Elsewhere);
    }

    // ── The coverage sidecar ─────────────────────────────────────────────────

    /// <summary>The sidecar beside <paramref name="reportPath"/>: <c>x.coverage.txt</c> for <c>x.md</c>.</summary>
    public static string CoveragePath(string reportPath)
    {
        ArgumentNullException.ThrowIfNull(reportPath);
        string bare = reportPath.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase) ? reportPath[..^PlanSlug.Extension.Length] : reportPath;
        return bare + CoverageExtension;
    }

    /// <summary>The files the passes read, as the sidecar keeps them; empty when there is none yet (or it cannot be read now).</summary>
    public static HashSet<string> ReadCoverage(WorkingDirectory files, string reportPath) => ReadCoverage(files, reportPath, out _);

    /// <summary>
    /// <see cref="ReadCoverage(WorkingDirectory, string)"/>, <paramref name="problem"/> the error sentence when a sidecar is there that cannot be read
    /// (locked by a scan or a sync client): a merge then leaves it as it is (2026-10-20, a review: read as empty, it was written over with this
    /// call's files alone, and every earlier run's were lost).
    /// </summary>
    public static HashSet<string> ReadCoverage(WorkingDirectory files, string reportPath, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(files);
        string path = CoveragePath(reportPath);
        var read = files.ReadText(path, null, null, WorkingDirectory.MaxWriteChars);
        problem = read.Outcome is FileOutcome.Ok or FileOutcome.Missing ? null : FileText.Error(read.Outcome, path, "read", read.Detail);
        return read.Outcome == FileOutcome.Ok ? ParseCoverage(read.Text) : new HashSet<string>(AuditPath.Comparer);
    }

    /// <summary>
    /// <paramref name="read"/> merged into the sidecar and written back when anything is new: null when done or nothing changed, else the
    /// write's sentence. A list past <see cref="WorkingDirectory.MaxWriteChars"/> keeps what fits, folder by folder.
    /// </summary>
    public static string? MergeCoverage(WorkingDirectory files, string reportPath, IEnumerable<string> read)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(read);
        var known = ReadCoverage(files, reportPath, out string? unread);
        if (unread is not null)
        {
            return unread;
        }

        int before = known.Count;
        known.UnionWith(read.Where(p => p.Length > 0));
        if (known.Count == before && before > 0)
        {
            return null;
        }

        if (known.Count == 0)
        {
            return null;
        }

        string text = RenderCoverage(known);
        var written = WriteGuard.AsApp(() => files.WriteText(CoveragePath(reportPath), text, overwrite: true));
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, CoveragePath(reportPath), "write", written.Detail);
    }

    /// <summary>
    /// The sidecar's text: grouped by folder, a <c>folder/</c> line then its file names indented two spaces, the root's files under <c>./</c>;
    /// folders and names in order. At most <see cref="WorkingDirectory.MaxWriteChars"/>. Pure.
    /// </summary>
    public static string RenderCoverage(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sb = new StringBuilder();
        foreach (var group in paths.Where(p => p.Length > 0).Distinct(AuditPath.Comparer)
            .GroupBy(p => p.Contains('/', StringComparison.Ordinal) ? p[..(p.LastIndexOf('/') + 1)] : "./", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var block = new StringBuilder(group.Key).Append('\n');
            foreach (string path in group.OrderBy(p => p, StringComparer.Ordinal))
            {
                block.Append("  ").Append(group.Key == "./" ? path : path[group.Key.Length..]).Append('\n');
            }

            if (sb.Length + block.Length > WorkingDirectory.MaxWriteChars)
            {
                break;
            }

            sb.Append(block);
        }

        return sb.ToString();
    }

    /// <summary>The sidecar's paths back from <see cref="RenderCoverage"/>'s text (CRLF or LF). Pure.</summary>
    public static HashSet<string> ParseCoverage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paths = new HashSet<string>(AuditPath.Comparer);
        string folder = "./";
        foreach (string raw in AuditReport.Normalize(text).Split('\n'))
        {
            if (raw.Trim().Length == 0)
            {
                continue;
            }

            if (!raw.StartsWith(' ') && raw.TrimEnd().EndsWith('/'))
            {
                folder = raw.TrimEnd();
                continue;
            }

            string name = raw.Trim();
            paths.Add(folder == "./" ? name : folder + name);
        }

        return paths;
    }

    /// <summary>
    /// The report and its sidecar deleted (through <see cref="WorkingDirectory.Delete"/>, the Recycle bin when the setting says): the report's result.
    /// The sidecar goes only with the report (2026-10-20, a review: a report that would not go kept its place with its coverage gone). Done holding
    /// the report's lock, taken first (2026-10-30, review F1: the caller checked, then this deleted and only then took the lock, so a resume elsewhere
    /// in between lost its report): while another live NeonSidekick holds it nothing is deleted and <paramref name="refusal"/> says so, the result
    /// <see cref="FileOutcome.Failed"/>. The lock goes after. A drive that holds no lock, or a lock that can't be written, deletes unlocked.
    /// </summary>
    public static DeleteResult Discard(WorkingDirectory files, string path, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        refusal = null;
        string lockPath = LockPathFor(path);
        var take = OwnerLock.TryLock(files, lockPath, OwnerLock.Me, OwnerLock.LiveOwner);
        if (take == LockTake.Held)
        {
            refusal = HeldError(files, path);
            return new DeleteResult(FileOutcome.Failed, path, IsDirectory: false, refusal);
        }

        try
        {
            var result = WriteGuard.AsApp(() => files.Delete(path));
            if (result.Outcome == FileOutcome.Ok && Exists(files, CoveragePath(path)))
            {
                WriteGuard.AsApp(() => files.Delete(CoveragePath(path)));
            }

            return result;
        }
        finally
        {
            if (take == LockTake.Taken)
            {
                OwnerLock.Unlock(files, lockPath, OwnerLock.Me);
            }
        }
    }

    private static DateTimeOffset WrittenAt(string full)
    {
        try
        {
            return File.GetLastWriteTime(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Whether a report's name is taken: its file, or a sidecar left behind (2026-10-20, a review: a report deleted by hand left its
    /// <c>.coverage.txt</c>, and a new audit of the same name took its coverage as its own), or a lock another live NeonSidekick holds (2026-10-29:
    /// one that has just chosen the name and not saved yet). A stale lock doesn't take a name: the next audit of it takes the lock over.
    /// </summary>
    private static bool Taken(WorkingDirectory files, string path) =>
        files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok || File.Exists(full) || Directory.Exists(full) || Exists(files, CoveragePath(path))
        || OwnerLock.HeldElsewhere(files, LockPathFor(path), out _);
}
