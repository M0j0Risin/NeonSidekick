using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Plans;

namespace NeonSidekick.Audits;

/// <summary>
/// One report as <c>/code-audit open</c> lists it (2026-10-18): its path, status, the open findings by severity and their count, the settled
/// count, the passes line, the last update, whether every finding is ticked, whether it could not be read whole, and whether it is the
/// audit running now (an <c>auditing</c> report with no live loop lists as stopped).
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
    bool Live = false)
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
    public static string? Save(WorkingDirectory files, string path, AuditReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(report);
        report.Header = report.Header with { Created = report.Header.Created ?? now, Updated = now, NextId = report.NextIdNumber };
        string text = report.Render();
        if (text.Length > WorkingDirectory.MaxWriteChars)
        {
            return AuditText.ReportFullResult;
        }

        var written = files.WriteText(path, text, overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
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
        return Exists(files, path) ? path : null;
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
            listings.Add((new AuditListing(path, report.Header.Status, report.OpenBySeverity(), report.OpenCount, report.SettledCount, report.Header.Passes, updated, report.Fixed, Live: live),
                updated ?? WrittenAt(full)));
        }

        return listings.OrderByDescending(l => l.Order).ThenBy(l => l.Listing.Path, StringComparer.Ordinal).Select(l => l.Listing).ToList();
    }

    // ── The coverage sidecar ─────────────────────────────────────────────────

    /// <summary>The sidecar beside <paramref name="reportPath"/>: <c>x.coverage.txt</c> for <c>x.md</c>.</summary>
    public static string CoveragePath(string reportPath)
    {
        ArgumentNullException.ThrowIfNull(reportPath);
        string bare = reportPath.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase) ? reportPath[..^PlanSlug.Extension.Length] : reportPath;
        return bare + CoverageExtension;
    }

    /// <summary>The files the passes read, as the sidecar keeps them; empty when there is none yet.</summary>
    public static HashSet<string> ReadCoverage(WorkingDirectory files, string reportPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        var read = files.ReadText(CoveragePath(reportPath), null, null, WorkingDirectory.MaxWriteChars);
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
        var known = ReadCoverage(files, reportPath);
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
        var written = files.WriteText(CoveragePath(reportPath), text, overwrite: true);
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

    /// <summary>The report and its sidecar deleted (through <see cref="WorkingDirectory.Delete"/>, the Recycle bin when the setting says): the report's result.</summary>
    public static DeleteResult Discard(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        var result = files.Delete(path);
        if (Exists(files, CoveragePath(path)))
        {
            files.Delete(CoveragePath(path));
        }

        return result;
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

    private static bool Taken(WorkingDirectory files, string path) =>
        files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok || File.Exists(full) || Directory.Exists(full);
}
