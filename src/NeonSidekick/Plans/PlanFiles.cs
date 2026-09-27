using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Plans;

/// <summary>One plan file as <c>/plan open</c> lists it: its path, title, status, ticked steps and last update.</summary>
public sealed record PlanListing(string Path, string Title, PlanStatus? Status, int Done, int Total, DateTimeOffset? Updated)
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
        var header = new PlanHeader(PlanStatus.Draft, revision, session.Created ?? now, now, session.Requirement);
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
            return FileText.Missing(path);
        }

        var written = files.WriteText(path, PlanDocument.WithStatus(text, status, now, requirement, progress), overwrite: true);
        return written.Outcome == FileOutcome.Ok ? null : FileText.Error(written.Outcome, path, "write", written.Detail);
    }

    /// <summary>The plan file's whole text, or null when it cannot be read whole.</summary>
    public static string? ReadWhole(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        var read = files.ReadText(path, null, null);
        return read.Outcome == FileOutcome.Ok && !read.Truncated ? read.Text : null;
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

    /// <summary>Every <c>.neon/plans/*.md</c>, the most recently updated first (the file's time when its header has none).</summary>
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
                continue;
            }

            var header = PlanDocument.TryParse(text);
            string body = PlanDocument.Body(text);
            var (done, total) = PlanDocument.Progress(body);
            string title = PlanDocument.FirstHeading(body) ?? Path.GetFileNameWithoutExtension(full);
            DateTimeOffset order = header?.Updated is { } updated && updated != DateTimeOffset.MinValue ? updated : File.GetLastWriteTime(full);
            listings.Add((new PlanListing(path, title, header?.Status, done, total, header?.Updated), order));
        }

        return listings.OrderByDescending(l => l.Order).ThenBy(l => l.Listing.Path, StringComparer.Ordinal).Select(l => l.Listing).ToList();
    }

    /// <summary><see cref="PlanSlug.Folder"/>'s last segment, <c>plans</c>, as a name may still be typed with it.</summary>
    private const string BareFolder = "plans";

    private static bool Taken(WorkingDirectory files, string path) =>
        files.Resolve(path, forWrite: false, out string full) != FileOutcome.Ok || File.Exists(full) || Directory.Exists(full);
}
