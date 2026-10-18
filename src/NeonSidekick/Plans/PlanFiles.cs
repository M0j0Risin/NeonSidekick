using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Plans;

/// <summary>
/// One plan file as <c>/plan open</c> lists it: its path, title, status, ticked steps and last update. <paramref name="Unreadable"/>
/// (2026-10-17, the user's report: a long plan was missing from the list) is a <c>.md</c> that cannot be read whole, listed by its
/// file name with no status or steps rather than left out.
/// </summary>
/// <paramref name="Review"/> (2026-10-17) is what its <c>review:</c> line and body say of its last gap review (<see cref="PlanReview.Mark"/>).
public sealed record PlanListing(string Path, string Title, PlanStatus? Status, int Done, int Total, DateTimeOffset? Updated, bool Unreadable = false, PlanReviewMark Review = PlanReviewMark.None)
{
    /// <summary>The name <c>/plan open</c> takes: the file name without <c>.neon/plans/</c> or <c>.md</c>.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The plan files under the working directory (2026-09-26, round two): saving a plan — for <c>present_plan</c>
/// and <c>/plan save</c> alike —, rewriting a file's status, reading one whole, finding one by name and listing
/// them all. Every path stays inside <see cref="PlanSlug.Folder"/>; every write goes through
/// <see cref="WorkingDirectory.WriteText"/>, atomic and sandboxed.
/// </summary>
public static class PlanFiles
{
    /// <summary>
    /// <paramref name="markdown"/> saved as the session's plan: the first save picks the path (<see cref="PlanSlug"/>
    /// over <paramref name="name"/>, else the title), every later one overwrites it as the next revision, the header
    /// the app's (<see cref="PlanDocument.Render"/>); the session then holds the path. The presentation, or the error sentence.
    /// </summary>
    public static (PlanPresentation? Saved, string? Error) Save(PlanSession session, WorkingDirectory files, TimeProvider time, string title, string markdown, string? name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(markdown);
        var now = time.GetLocalNow();
        string path = session.Path ?? PlanSlug.Choose(PlanSlug.From(string.IsNullOrWhiteSpace(name) ? title : name), p => Taken(files, p), now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        int revision = session.Path is null ? 1 : session.Revision + 1;
        // /plan chat (2026-10-17): while the requirement is only the conversation's opening, the plan's title is written in its place, and
        // Presented settles the session on the same words, so the file and the session agree.
        var header = new PlanHeader(PlanStatus.Draft, revision, session.Created ?? now, now, session.RequirementFor(title));
        var written = files.WriteText(path, PlanDocument.Render(header, title, markdown), overwrite: true);
        if (written.Outcome != FileOutcome.Ok)
        {
            return (null, FileText.Error(written.Outcome, path, "write", written.Detail));
        }

        session.Presented(path, title, revision, now);
        return (new PlanPresentation(title, path, revision, PlanDocument.Body(markdown)), null);
    }

    /// <summary>
    /// The plan file's status rewritten (<see cref="PlanDocument.WithStatus"/>), the body untouched, with its
    /// <c>progress:</c> line when <paramref name="progress"/> is given. Null when done, else the error sentence.
    /// </summary>
    public static string? MarkFile(WorkingDirectory files, string path, PlanStatus status, DateTimeOffset now, string requirement, (int Done, int Total)? progress = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        if (ReadWhole(files, path) is not { } text)
        {
            // A file that is there but cannot be read whole is not "nothing at" it (2026-10-17).
            return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full)
                ? PlanText.StatusUnwrittenWarning(path)
                : FileText.Missing(path);
        }

        var written = files.WriteText(path, PlanDocument.WithStatus(text, status, now, requirement, progress), overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
    }

    /// <summary>
    /// The plan file's <c>review:</c> line set (2026-10-17, <see cref="PlanDocument.WithReview"/>), the body and the status untouched: the
    /// <see cref="MarkFile"/> shape, null when done, else the same warnings.
    /// </summary>
    public static string? MarkReview(WorkingDirectory files, string path, string review, DateTimeOffset now, string requirement)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(review);
        if (ReadWhole(files, path) is not { } text)
        {
            return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full)
                ? PlanText.StatusUnwrittenWarning(path)
                : FileText.Missing(path);
        }

        var written = files.WriteText(path, PlanDocument.WithReview(text, review, now, requirement), overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
    }

    /// <summary>
    /// The plan file's whole text, or null when it cannot be read whole. Read up to <see cref="WorkingDirectory.MaxWriteChars"/>, the most a
    /// plan can have been written with (2026-10-17, the user's report: a 40,000-character plan "could not be read", missing from
    /// <c>/plan open</c>'s list, its status never written, its progress dropped; the model's 32,000-character <c>read_file</c> window was
    /// the wrong limit for a file the app wrote); a line cut is not the whole text either.
    /// </summary>
    public static string? ReadWhole(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        var read = files.ReadText(path, null, null, WorkingDirectory.MaxWriteChars);
        return read.Outcome == FileOutcome.Ok && !read.Truncated && !read.LineCut ? read.Text : null;
    }

    /// <summary>
    /// The relative path of the plan <paramref name="name"/> names — <c>x</c>, <c>x.md</c>, <c>plans/x.md</c> or <c>.neon/plans/x.md</c>, case aside
    /// as the file system has it — when that file exists directly under <see cref="PlanSlug.Folder"/>; null otherwise,
    /// and for anything with a folder of its own or a way out.
    /// </summary>
    public static string? Resolve(WorkingDirectory files, string name)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(name);
        string bare = name.Trim().Replace('\\', '/');
        if (bare.StartsWith(PlanSlug.Folder + "/", StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[(PlanSlug.Folder.Length + 1)..];
        }
        else if (bare.StartsWith(BareFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            // plans/x typed as the folder was before .neon (2026-09-26): still the plan in .neon/plans.
            bare = bare[(BareFolder.Length + 1)..];
        }

        if (bare.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase))
        {
            bare = bare[..^PlanSlug.Extension.Length];
        }

        if (bare.Length == 0 || bare.Contains('/', StringComparison.Ordinal) || bare.Contains("..", StringComparison.Ordinal) || bare.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        string path = PlanSlug.Folder + "/" + bare + PlanSlug.Extension;
        return files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full) ? path : null;
    }

    /// <summary>
    /// Every <c>.neon/plans/*.md</c>, the most recently updated first (the file's time when its header has none); one that cannot be
    /// read whole is listed as <see cref="PlanListing.Unreadable"/> (2026-10-17), so <c>/plan open</c> names why rather than not finding it.
    /// </summary>
    public static IReadOnlyList<PlanListing> List(WorkingDirectory files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Resolve(PlanSlug.Folder, forWrite: false, out string folder) != FileOutcome.Ok || !Directory.Exists(folder))
        {
            return [];
        }

        var listings = new List<(PlanListing Listing, DateTimeOffset Order)>();
        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFiles(folder, "*" + PlanSlug.Extension).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string full in found)
        {
            string path = PlanSlug.Folder + "/" + Path.GetFileName(full);
            if (ReadWhole(files, path) is not { } text)
            {
                listings.Add((new PlanListing(path, Path.GetFileNameWithoutExtension(full), null, 0, 0, null, Unreadable: true), WrittenAt(full)));
                continue;
            }

            var header = PlanDocument.TryParse(text);
            string body = PlanDocument.Body(text);
            var (done, total) = PlanDocument.Progress(body);
            string title = PlanDocument.FirstHeading(body) ?? Path.GetFileNameWithoutExtension(full);
            DateTimeOffset order = header?.Updated is { } updated && updated != DateTimeOffset.MinValue ? updated : WrittenAt(full);
            listings.Add((new PlanListing(path, title, header?.Status, done, total, header?.Updated, Review: PlanReview.Mark(header?.Review, body)), order));
        }

        return listings.OrderByDescending(l => l.Order).ThenBy(l => l.Listing.Path, StringComparer.Ordinal).Select(l => l.Listing).ToList();
    }

    /// <summary><see cref="PlanSlug.Folder"/>'s last segment, <c>plans</c>, as a name may still be typed with it.</summary>
    private const string BareFolder = "plans";

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
