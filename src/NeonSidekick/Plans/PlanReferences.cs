using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Plans;

/// <summary>
/// A picture from the conversation a plan may keep (2026-10-18, <see cref="PlanReferences"/>): its bytes, their type and where it came from (null for
/// none known). <paramref name="Hash"/> is its SHA-256 when known already (2026-10-20, <see cref="PlanReferences.Hashed"/>: a picture only named needs
/// no bytes).
/// </summary>
public sealed record PlanPicture(byte[] Bytes, string MediaType, string? Source, string? Hash = null);

/// <summary>A reference picture saved beside a plan: its name (<c>ref-1</c>) and its path relative to the working directory.</summary>
public sealed record PlanReference(string Name, string Path);

/// <summary>
/// Plan reference pictures (2026-10-18, the user's ask: what a plan was made from — a screenshot, a sketch, a photo of the board — goes with it
/// to the runs that start with nothing of the planning: a fresh-context approve and every Ralph pass). The pictures of the planning conversation
/// are saved beside the plan in its folder (<see cref="FolderOf"/>) as <c>ref-N.&lt;ext&gt;</c>, each once (<see cref="Sync"/>, by SHA-256, numbers
/// never reused), and listed under the plan's <c>## References</c> (<see cref="ReconcileSection"/>: the model's lines and descriptions kept, a saved
/// picture it left out appended); a run reloads the entries a step names, else all, within the picture budget (<see cref="ForRun"/>). What the
/// conversation's pictures are: the user's own (pasted, dropped, <c>/camera</c>'s and <c>/screen</c>'s only under their keep-in-sessions settings),
/// never a <c>view_image</c> carrier's, a PDF or a PDF page's picture, nor a reference picture the app attached itself; a picture a compaction
/// took out is read again from its path (<see cref="FromHistory"/>).
/// </summary>
public static class PlanReferences
{
    /// <summary>The plan's section that lists them.</summary>
    public const string Heading = "References";

    /// <summary>A reference picture's name before its number.</summary>
    public const string Prefix = "ref-";

    /// <summary>The most bytes a reloaded picture may have (the attachment cap).</summary>
    public const long MaxPictureBytes = 20_000_000;

    private static readonly Regex s_refFile = new(@"^ref-(\d+)\.(png|jpe?g|gif|webp|bmp)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex s_refName = new(@"\bref-(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The folder beside <paramref name="planPath"/> its pictures and Ralph log live in: <c>.neon/plans/&lt;slug&gt;</c>.</summary>
    public static string FolderOf(string planPath)
    {
        ArgumentNullException.ThrowIfNull(planPath);
        return planPath.EndsWith(PlanSlug.Extension, StringComparison.OrdinalIgnoreCase) ? planPath[..^PlanSlug.Extension.Length] : planPath + ".d";
    }

    /// <summary>
    /// The conversation's pictures a plan may keep, in order, from <paramref name="messages"/>: the user messages' image parts (never a carrier's, a
    /// PDF's, a PDF page's or a reference's), a camera picture only with <paramref name="keepCamera"/>, a screenshot only with <paramref name="keepScreen"/>,
    /// and a picture a compaction took out read again through <paramref name="reload"/> (its path; null skips them) — never one whose stand-in
    /// carries a reference's mark, nor a camera photo's or a screenshot's the keep settings leave out (2026-10-19, the review: they were read
    /// from disk again and saved beside the plan; <see cref="PictureBudget.LeftOutPart"/> keeps the marks).
    /// </summary>
    public static IReadOnlyList<PlanPicture> FromHistory(IReadOnlyList<ChatMessage> messages, bool keepCamera, bool keepScreen, Func<string, PlanPicture?>? reload = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var found = new List<PlanPicture>();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.User || ConversationHistory.IsImageCarrier(message))
            {
                continue;
            }

            foreach (var content in message.Contents)
            {
                if (content is DataContent data && data.HasTopLevelMediaType("image"))
                {
                    if (ConversationHistory.PdfPage(data) is not null || ConversationHistory.ReferencePath(data) is not null
                        || (!keepCamera && ConversationHistory.CameraPath(data) is not null) || (!keepScreen && ConversationHistory.ScreenPath(data) is not null))
                    {
                        continue;
                    }

                    // The history's own array when the content holds one whole (2026-10-20, a review: copied each planning turn, every picture's bytes
                    // were hashed again; the same array is the hash cache's key from turn to turn). Never written to.
                    byte[] bytes = MemoryMarshal.TryGetArray(data.Data, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
                        ? array
                        : data.Data.ToArray();
                    found.Add(new PlanPicture(bytes, data.MediaType, ConversationHistory.PicturePath(data)));
                }
                else if (content is TextContent text && reload is not null && LeftOutPath(text.Text) is { } path
                    && !Marked(text, ConversationHistory.ReferenceKey) && (keepCamera || !Marked(text, ConversationHistory.CameraKey)) && (keepScreen || !Marked(text, ConversationHistory.ScreenKey))
                    && reload(path) is { } again)
                {
                    found.Add(again);
                }
            }
        }

        return found;
    }

    private static bool Marked(AIContent content, string key) => content.AdditionalProperties?.ContainsKey(key) == true;

    /// <summary>The path a <see cref="PictureBudget.LeftOut"/> stand-in names, or null.</summary>
    private static string? LeftOutPath(string text)
    {
        const string Head = "(picture ";
        const string Tail = " taken out to keep the request small)";
        string trimmed = text.Trim();
        return trimmed.StartsWith(Head, StringComparison.Ordinal) && trimmed.EndsWith(Tail, StringComparison.Ordinal) && trimmed.Length > Head.Length + Tail.Length
            ? trimmed[Head.Length..^Tail.Length]
            : null;
    }

    /// <summary>The pictures saved beside <paramref name="planPath"/>, by number.</summary>
    public static IReadOnlyList<PlanReference> Saved(WorkingDirectory files, string planPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(planPath);
        string folder = FolderOf(planPath);
        if (files.Resolve(folder, forWrite: false, out string full) != FileOutcome.Ok || !Directory.Exists(full))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(full)
                .Select(Path.GetFileName)
                .Select(name => (Name: name!, Match: s_refFile.Match(name!)))
                .Where(f => f.Match.Success && int.TryParse(f.Match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderBy(f => int.Parse(f.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(f => new PlanReference(Prefix + f.Match.Groups[1].Value, folder + "/" + f.Name))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The names <paramref name="pictures"/> are (or will be) saved under beside <paramref name="planPath"/> (null before the first presentation):
    /// the saved ones that match, then the new ones numbered after the highest saved, without writing. What the planning directive names.
    /// </summary>
    public static IReadOnlyList<string> Names(WorkingDirectory files, string? planPath, IReadOnlyList<PlanPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pictures);
        var saved = planPath is null ? [] : Saved(files, planPath);
        // Two saved files of the same bytes (one written again while the first was locked, or copied by hand) keep the first's name (2026-10-20,
        // a review: ToDictionary threw on the second, and every planning turn after it).
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in saved)
        {
            hashes.TryAdd(HashOf(files, reference.Path) ?? reference.Path, reference.Name);
        }

        int next = saved.Count == 0 ? 1 : saved.Max(r => Number(r.Name)) + 1;
        var names = new List<string>();
        foreach (var picture in pictures)
        {
            string hash = HashOf(picture);
            if (!hashes.TryGetValue(hash, out string? name))
            {
                name = Prefix + N(next++);
                hashes[hash] = name;
            }

            if (!names.Contains(name, StringComparer.Ordinal))
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// <paramref name="pictures"/> saved beside <paramref name="planPath"/> (2026-10-18): one already there (the same bytes) is left, a new one written as
    /// the next <c>ref-N</c> (<see cref="WorkingDirectory.WriteBytes"/>, sandboxed and atomic); every saved picture after, by number. A write refused is skipped.
    /// </summary>
    public static IReadOnlyList<PlanReference> Sync(WorkingDirectory files, string planPath, IReadOnlyList<PlanPicture> pictures)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(planPath);
        ArgumentNullException.ThrowIfNull(pictures);
        var saved = Saved(files, planPath).ToList();
        if (pictures.Count == 0)
        {
            return saved;
        }

        var hashes = new HashSet<string>(saved.Select(r => HashOf(files, r.Path)).OfType<string>(), StringComparer.Ordinal);
        int next = saved.Count == 0 ? 1 : saved.Max(r => Number(r.Name)) + 1;
        string folder = FolderOf(planPath);
        foreach (var picture in pictures)
        {
            string hash = HashOf(picture);
            if (!hashes.Add(hash))
            {
                continue;
            }

            string name = Prefix + N(next);
            string path = folder + "/" + name + ExtensionFor(picture);
            if (files.WriteBytes(path, picture.Bytes, overwrite: false).Outcome == FileOutcome.Ok)
            {
                saved.Add(new PlanReference(name, path));
                next++;
            }
        }

        return saved;
    }

    /// <summary>
    /// <paramref name="body"/> with its <c>## References</c> listing every one of <paramref name="saved"/>: the section's own lines kept (the model's
    /// descriptions, and project pictures it listed by path), a saved picture it does not name appended as <c>- ref-N (`path`): a picture from the
    /// conversation</c>; a body with none gains the section at its end. Nothing saved, the body as it was. Pure.
    /// </summary>
    public static string ReconcileSection(string body, IReadOnlyList<PlanReference> saved)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(saved);
        if (saved.Count == 0)
        {
            return body;
        }

        string text = body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        var lines = text.Split('\n').ToList();
        int start = lines.FindIndex(IsReferencesHeading);
        int end = start < 0 ? lines.Count : NextSection(lines, start);
        string section = start < 0 ? "" : string.Join('\n', lines.Skip(start + 1).Take(end - start - 1));
        var named = new HashSet<string>(s_refName.Matches(section).Select(m => Prefix + m.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
        var missing = saved.Where(r => !named.Contains(r.Name) && !section.Contains(r.Path, StringComparison.Ordinal)).Select(r => PlanText.ReferenceLine(r.Name, r.Path)).ToList();
        if (missing.Count == 0)
        {
            return body;
        }

        if (start < 0)
        {
            return text + "\n\n## " + Heading + "\n" + string.Join('\n', missing);
        }

        int at = end;
        while (at > start + 1 && lines[at - 1].Trim().Length == 0)
        {
            at--;
        }

        lines.InsertRange(at, missing);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The entries of <paramref name="body"/>'s <c>## References</c>: each line's <c>ref-N</c> name, or else the path it names (a project picture).
    /// </summary>
    public static IReadOnlyList<(string? Ref, string? Path)> Entries(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        int start = lines.FindIndex(IsReferencesHeading);
        if (start < 0)
        {
            return [];
        }

        var entries = new List<(string?, string?)>();
        foreach (string line in lines.Skip(start + 1).Take(NextSection(lines, start) - start - 1))
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (s_refName.Match(line) is { Success: true } named)
            {
                entries.Add((Prefix + named.Groups[1].Value, null));
            }
            else if (PlanSteps.PathsIn(line).FirstOrDefault(ImageFile.IsImagePath) is { } path)
            {
                entries.Add((null, path));
            }
        }

        return entries;
    }

    /// <summary>
    /// The pictures a run of <paramref name="planPath"/> is sent with (2026-10-18): the <c>## References</c> entries <paramref name="step"/> names (by
    /// <c>ref-N</c> or by path), else every entry; each loaded as an attachment marked <see cref="ImageAttachment.Reference"/> (its path the plan's
    /// own, relative), at most <paramref name="keep"/> pictures and <paramref name="megabytes"/> MB (0 for no cap), in order. None, with
    /// <paramref name="notice"/> said, when <paramref name="canSee"/> is false (a server that cannot see pictures, no image codecs).
    /// </summary>
    public static IReadOnlyList<ImageAttachment> ForRun(WorkingDirectory files, string planPath, string body, PlanStep? step, int keep, int megabytes, bool canSee, out string? notice)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(planPath);
        ArgumentNullException.ThrowIfNull(body);
        notice = null;
        var entries = Entries(body);
        if (entries.Count == 0)
        {
            return [];
        }

        if (!canSee)
        {
            notice = PlanText.ReferencesUnseenNotice(entries.Count);
            return [];
        }

        var saved = Saved(files, planPath);
        IEnumerable<(string? Ref, string? Path)> wanted = entries;
        if (step is not null && (step.NamedRefs.Count > 0 || step.NamedPaths.Any(ImageFile.IsImagePath)))
        {
            wanted = entries.Where(e => (e.Ref is { } r && step.NamedRefs.Contains(r, StringComparer.OrdinalIgnoreCase))
                || (e.Path is { } p && step.NamedPaths.Contains(p, StringComparer.Ordinal)));
            var named = step.NamedPaths.Where(ImageFile.IsImagePath).Where(p => !entries.Any(e => e.Path == p)).Select(p => ((string?)null, (string?)p));
            wanted = wanted.Concat(named);
        }

        var images = new List<ImageAttachment>();
        long bytes = 0;
        long cap = megabytes <= 0 ? long.MaxValue : megabytes * 1_000_000L;
        foreach (var (name, path) in wanted)
        {
            string? relative = name is { } r ? saved.FirstOrDefault(s => string.Equals(s.Name, r, StringComparison.OrdinalIgnoreCase))?.Path : path;
            if (relative is null || (keep > 0 && images.Count >= keep) || files.Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok)
            {
                continue;
            }

            if (ImageFile.Load(full, out _) is not { } image || bytes + image.Bytes.Length > cap)
            {
                continue;
            }

            bytes += image.Bytes.Length;
            images.Add(image with { Path = relative, Reference = true });
        }

        return images;
    }

    /// <summary>A picture file read again as a <see cref="PlanPicture"/> (a pruned one's path): null when it is not there, too large or not an image.</summary>
    public static PlanPicture? Reload(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxPictureBytes || !ImageFile.IsImagePath(fullPath))
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(fullPath);
            return new PlanPicture(data, MediaTypeOf(data), fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsReferencesHeading(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith("## ", StringComparison.Ordinal) && string.Equals(trimmed[3..].Trim().TrimEnd(':'), Heading, StringComparison.OrdinalIgnoreCase);
    }

    private static int NextSection(List<string> lines, int start)
    {
        for (int i = start + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return lines.Count;
    }

    private static int Number(string name) => int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;

    private static string? HashOf(WorkingDirectory files, string path) =>
        files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok ? FileHash(full) : null;

    /// <summary>
    /// A picture file as <see cref="Names"/> needs it, its hash alone (no bytes): what the planning directive's names read for a pruned picture every
    /// turn (2026-10-20, a review: up to 20 MB each was read and hashed again per turn); null as <see cref="Reload"/> is.
    /// </summary>
    public static PlanPicture? Hashed(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return !info.Exists || info.Length > MaxPictureBytes || !ImageFile.IsImagePath(fullPath) || FileHash(fullPath) is not { } hash
                ? null
                : new PlanPicture([], "", fullPath, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // The hashes known (2026-10-20, a review: every planning turn hashed every picture of the conversation and every saved ref-N file again): a
    // picture's by its bytes' array (the history's own, the turn's attachment's), a file's by its path while its size and write time hold.
    private static readonly ConditionalWeakTable<byte[], string> s_hashes = new();
    private static readonly Lock s_fileGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Written, string Hash)> s_fileHashes = new(StringComparer.Ordinal);
    private const int MaxFileHashes = 256;

    /// <summary><paramref name="picture"/>'s SHA-256: the one it carries, else its bytes', once per array.</summary>
    private static string HashOf(PlanPicture picture) => picture.Hash ?? s_hashes.GetValue(picture.Bytes, static bytes => Hash(bytes));

    /// <summary>A file's SHA-256, read again only when its size or write time changed; null when it cannot be read.</summary>
    private static string? FileHash(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return null;
            }

            lock (s_fileGate)
            {
                if (s_fileHashes.TryGetValue(full, out var kept) && kept.Length == info.Length && kept.Written == info.LastWriteTimeUtc)
                {
                    return kept.Hash;
                }
            }

            string hash = Hash(File.ReadAllBytes(full));
            lock (s_fileGate)
            {
                if (s_fileHashes.Count >= MaxFileHashes)
                {
                    s_fileHashes.Clear();
                }

                s_fileHashes[full] = (info.Length, info.LastWriteTimeUtc, hash);
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The extension a picture is saved with: its media type's, else what its bytes say.</summary>
    private static string ExtensionFor(PlanPicture picture) => picture.MediaType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ImageFile.ExtensionOf(picture.Bytes),
    };

    private static string MediaTypeOf(byte[] data) => ImageFile.ExtensionOf(data) switch
    {
        ".jpg" => "image/jpeg",
        ".bmp" => "image/bmp",
        _ => data.AsSpan().StartsWith("GIF8"u8) ? "image/gif" : data.AsSpan().StartsWith("RIFF"u8) ? "image/webp" : "image/png",
    };

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
