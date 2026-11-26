using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Comfy;

/// <summary>
/// The sidecar's values as the wizard writes them (<see cref="ComfyWorkflowFile.Sidecar"/>); <see cref="ImageRoles"/> (later
/// still on 2026-09-24, the face swap) are the input pictures' roles in slot order, a blank one left out. <see cref="Reinforce"/> false
/// writes <c>reinforce: false</c>, and <see cref="Tips"/> go under the head (2026-11-25, the shortened wizard).
/// </summary>
public sealed record ComfySidecar(string Description, ComfyFamily Family, int? Width, int? Height, int? Steps, double? Cfg, double? Denoise, string Negative, IReadOnlyList<string>? ImageRoles = null, bool Reinforce = true, string Tips = "");

/// <summary>
/// Writing a new workflow into a <c>comfy</c> folder (later on 2026-09-24, the add-workflow wizard's save): the sidecar
/// <c>&lt;name&gt;.md</c> first, then <c>&lt;name&gt;.json</c> — the catalog lists a workflow by its <c>.json</c>, so it
/// never sees one half written. A name must be a plain file stem (<see cref="IsValidName"/>) and free in every folder
/// the catalog reads, so the new workflow shadows nothing and nothing shadows it. Since 2026-11-25 (the workflow manager) also an
/// installed workflow's save, rename, duplicate, move and delete, each returning null when done or the sentence that says why not.
/// </summary>
public static class ComfyWorkflowFile
{
    internal const string Category = "Comfy";

    /// <summary>The rule, in the words the wizard refuses a name with. Pinned.</summary>
    public const string NameRule = "1 to 64 lowercase letters, digits, hyphens and underscores, starting with a letter or digit";

    /// <summary>A name that is a plain, portable file stem: <see cref="NameRule"/>.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>
    /// A name as the wizard suggests it from a checkpoint or a file: lower-cased, anything else a hyphen, the runs folded, cut to 64, with no
    /// leading underscore (2026-11-25: it suggested a name it then refused).
    /// </summary>
    public static string Suggest(string from)
    {
        ArgumentNullException.ThrowIfNull(from);
        string stem = Path.GetFileNameWithoutExtension(from).ToLowerInvariant();
        var sb = new StringBuilder(stem.Length);
        foreach (char c in stem)
        {
            char k = char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' ? c : '-';
            if (!(k == '-' && (sb.Length == 0 || sb[^1] == '-')))
            {
                sb.Append(k);
            }
        }

        string name = sb.ToString().Trim('-').TrimStart('_', '-');
        return name.Length > 64 ? name[..64].TrimEnd('-') : name;
    }

    /// <summary>The sidecar text: the fences around the values set, then the tips under them when given. Pure; pinned.</summary>
    public static string Sidecar(ComfySidecar values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder("---\n");
        if (values.Description.Length > 0)
        {
            sb.Append("description: ").Append(Skills.SkillFrontmatter.Scalar(ComfySidecarEdit.Fold(values.Description))).Append('\n');
        }

        sb.Append("family: ").Append(ComfyFamilies.Name(values.Family)).Append('\n');
        if (values.Width is { } w) sb.Append("width: ").Append(w.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Height is { } h) sb.Append("height: ").Append(h.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Steps is { } s) sb.Append("steps: ").Append(s.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Cfg is { } c) sb.Append("cfg: ").Append(c.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (values.Denoise is { } d) sb.Append("denoise: ").Append(d.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("negative: ").Append(values.Negative.Length == 0 ? "\"\"" : Skills.SkillFrontmatter.Scalar(ComfySidecarEdit.Fold(values.Negative))).Append('\n');
        for (int i = 0; values.ImageRoles is { } roles && i < roles.Count && i < ComfyWorkflow.ImageKeys.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(roles[i]))
            {
                sb.Append(ComfyWorkflow.ImageKeys[i]).Append(": ").Append(Skills.SkillFrontmatter.Scalar(ComfySidecarEdit.Fold(roles[i].Trim()))).Append('\n');
            }
        }

        if (!values.Reinforce)
        {
            sb.Append("reinforce: false\n");
        }

        sb.Append("---\n");
        if (values.Tips.Trim().Length > 0)
        {
            sb.Append(values.Tips.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes <paramref name="name"/> into <paramref name="folder"/> (made when missing): null when done, else the sentence — a bad name, a
    /// name already in one of <paramref name="catalogRoots"/>, a leftover <c>.md</c> of that name (2026-11-25: it was overwritten silently),
    /// or the IO failure. The <c>.md</c> first; when the <c>.json</c> then fails the <c>.md</c> is taken back, so a retry isn't blocked by it.
    /// </summary>
    public static string? Add(string folder, IReadOnlyList<string> catalogRoots, string name, string graphJson, string sidecar)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(catalogRoots);
        ArgumentNullException.ThrowIfNull(graphJson);
        ArgumentNullException.ThrowIfNull(sidecar);
        if (NameProblem(name, catalogRoots.Append(folder), folder, own: null) is { } problem)
        {
            return problem;
        }

        string md = Path.Combine(folder, name + ".md");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(md, sidecar);
            try
            {
                File.WriteAllText(Path.Combine(folder, name + ".json"), graphJson);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Quietly(() => File.Delete(md));
                throw;
            }

            DiagnosticLog.Info(Category, ComfyText.LogAdded(name, folder));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>The folder among <paramref name="roots"/> already holding <c>&lt;name&gt;.json</c>, or null.</summary>
    public static string? Taken(IEnumerable<string> roots, string name)
    {
        ArgumentNullException.ThrowIfNull(roots);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(root, name + ".json")))
            {
                return root;
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> can't be a workflow's name in <paramref name="folder"/>, or null: <see cref="NameRule"/>, a Windows device
    /// name (<c>con</c>, <c>nul</c> …), a <c>.json</c> of that name in any of <paramref name="roots"/> other than <paramref name="own"/> (a
    /// rename that only changes case is free), or a leftover <c>.md</c> of that name in <paramref name="folder"/>.
    /// </summary>
    public static string? NameProblem(string? name, IEnumerable<string> roots, string folder, string? own)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(folder);
        if (!IsValidName(name))
        {
            return "the name must be " + NameRule;
        }

        if (CustomCommands.CustomCommandName.IsDeviceName(name!))
        {
            return ComfyText.DeviceName(name!);
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string json = Path.Combine(root, name + ".json");
            if (File.Exists(json) && !SamePath(json, own))
            {
                return ComfyText.NameTaken(name!, root);
            }
        }

        string md = Path.Combine(folder, name + ".md");
        string? ownMd = own is null ? null : Path.ChangeExtension(own, ".md");
        return File.Exists(md) && !SamePath(md, ownMd) ? ComfyText.LeftoverSidecar(md) : null;
    }

    private static bool SamePath(string a, string? b) =>
        b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name free everywhere: <paramref name="name"/> itself, else <c>&lt;name&gt;-2</c>, <c>-3</c> … within 64 characters. With
    /// <paramref name="suffixes"/> (2026-11-26, the review's #3: Kind both saves a <c>_txt2img</c> and an <c>_img2img</c>), each suffixed name must
    /// be free, and the stem is capped so the longest suffixed one still fits.
    /// </summary>
    public static string FreeName(string name, IReadOnlyList<string> roots, string folder, IReadOnlyList<string>? suffixes = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(roots);
        IReadOnlyList<string> tails = suffixes is { Count: > 0 } ? suffixes : [""];
        int room = 64 - tails.Max(t => t.Length);
        string stem = name.Length == 0 ? "workflow" : name;
        if (stem.Length > room)
        {
            stem = stem[..room].TrimEnd('-', '_', '.');
        }

        bool Free(string candidate) => tails.All(t => NameProblem(candidate + t, roots, folder, null) is null);
        if (Free(stem))
        {
            return stem;
        }

        for (int n = 2; n < 1000; n++)
        {
            string tail = "-" + n.ToString(CultureInfo.InvariantCulture);
            string candidate = (stem.Length + tail.Length > room ? stem[..(room - tail.Length)].TrimEnd('-') : stem) + tail;
            if (Free(candidate))
            {
                return candidate;
            }
        }

        return stem;
    }

    // ── An installed workflow's files (2026-11-25, the workflow manager) ──────

    /// <summary>
    /// Writes an edited workflow: <paramref name="newJson"/> and <paramref name="newMd"/> (null: unchanged) over the files as
    /// <paramref name="json"/> and <paramref name="md"/> read them. Refused when either changed on disk since (another instance, a hand
    /// edit), when the result is no workflow <see cref="ComfyWorkflow.TryParse"/> takes, or when a file's encoding can't hold the new
    /// text. Each file keeps its encoding and mark; the JSON takes its own line endings. The <c>.md</c> first; a failed <c>.json</c> puts
    /// the <c>.md</c> back. Null when done.
    /// </summary>
    public static string? Save(string name, ComfyFileText json, string? newJson, ComfyFileText md, string? newMd)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(md);
        if (newJson is null && newMd is null)
        {
            return null;
        }

        try
        {
            foreach (var file in (ComfyFileText[])[json, md])
            {
                if (!file.Unchanged())
                {
                    return ComfyText.ChangedOnDisk(file.Path);
                }
            }

            string jsonText = newJson is null ? json.Text : InEndings(newJson, json.Text);
            string? mdText = newMd ?? (md.Exists ? md.Text : null);
            if (!ComfyWorkflow.TryParse(name, json.Path, jsonText, mdText, out _, out string? problem))
            {
                return ComfyText.SaveRefused(problem!);
            }

            byte[]? mdBytes = newMd is null ? null : md.Encode(newMd);
            byte[]? jsonBytes = newJson is null ? null : json.Encode(jsonText);
            if (newMd is not null && mdBytes is null)
            {
                return ComfyText.CannotEncode(md.Path);
            }

            if (newJson is not null && jsonBytes is null)
            {
                return ComfyText.CannotEncode(json.Path);
            }

            if (mdBytes is not null)
            {
                WorkingDirectory.WriteAtomically(md.Path, mdBytes);
            }

            if (jsonBytes is not null)
            {
                try
                {
                    WorkingDirectory.WriteAtomically(json.Path, jsonBytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (mdBytes is not null)
                    {
                        Quietly(md.PutBack);
                    }

                    throw;
                }
            }

            DiagnosticLog.Info(Category, ComfyText.LogSaved(name, Path.GetDirectoryName(json.Path) ?? ""));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary><paramref name="text"/> in <paramref name="like"/>'s line endings (CRLF when it had any, else LF).</summary>
    public static string InEndings(string text, string like)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(like);
        string lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return like.Contains("\r\n", StringComparison.Ordinal) ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    /// <summary>
    /// <paramref name="oldName"/> renamed in <paramref name="folder"/>: the <c>.md</c> first, then the <c>.json</c>, rolled back on a failure,
    /// never overwriting (<see cref="NameProblem"/>; a hand-made name that breaks the rule can be renamed). The output name follows when it is
    /// <c>neon/&lt;old name&gt;</c>, and a family guessed from the old name is written into the <c>.md</c> (made when there is none). Null when done.
    /// </summary>
    public static string? Rename(string folder, IReadOnlyList<string> roots, string oldName, string newName) =>
        Relocate(folder, folder, roots, oldName, newName, copy: false, follow: true, ComfyText.LogRenamed(oldName, newName));

    /// <summary><paramref name="oldName"/> copied as <paramref name="newName"/> in <paramref name="folder"/>, followed as a rename is; the original untouched. Null when done.</summary>
    public static string? Duplicate(string folder, IReadOnlyList<string> roots, string oldName, string newName) =>
        Relocate(folder, folder, roots, oldName, newName, copy: true, follow: true, ComfyText.LogDuplicated(oldName, newName));

    /// <summary><paramref name="name"/> moved from <paramref name="from"/> to <paramref name="to"/> (made when missing), never over a file there. Null when done.</summary>
    public static string? Move(string from, string to, IReadOnlyList<string> roots, string name) =>
        Relocate(from, to, roots, name, name, copy: false, follow: false, ComfyText.LogMoved(name, to));

    private static string? Relocate(string from, string to, IReadOnlyList<string> roots, string oldName, string newName, bool copy, bool follow, string log)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(roots);
        string oldJson = Path.Combine(from, oldName + ".json");
        string oldMd = Path.Combine(from, oldName + ".md");
        string newJson = Path.Combine(to, newName + ".json");
        string newMd = Path.Combine(to, newName + ".md");
        if (!File.Exists(oldJson))
        {
            return ComfyText.WorkflowGone(oldName);
        }

        // A move's old folder holds the workflow itself: its name is checked against the others.
        bool sameFolder = SamePath(from, to);
        var others = sameFolder ? roots.Append(to) : roots.Where(r => !SamePath(r, from)).Append(to);
        if (NameProblem(newName, others, to, own: copy || !sameFolder ? null : oldJson) is { } problem)
        {
            return problem;
        }

        var undo = new List<Action>();
        try
        {
            var jsonFile = ComfyFileText.Read(oldJson);
            var mdFile = ComfyFileText.Read(oldMd);
            var (jsonAfter, mdAfter) = follow ? Followed(jsonFile, mdFile, oldName, newName) : (null, null);
            Directory.CreateDirectory(to);
            if (mdFile.Exists)
            {
                Step(oldMd, newMd, copy, undo);
            }

            Step(oldJson, newJson, copy, undo);
            if (mdAfter is not null)
            {
                byte[] bytes = mdFile.Encode(mdAfter) ?? throw new IOException(ComfyText.CannotEncode(newMd));
                WorkingDirectory.WriteAtomically(newMd, bytes);
                undo.Add(mdFile.Exists ? () => WorkingDirectory.WriteAtomically(newMd, mdFile.Bytes!) : () => File.Delete(newMd));
            }

            if (jsonAfter is not null)
            {
                byte[] bytes = jsonFile.Encode(jsonAfter) ?? throw new IOException(ComfyText.CannotEncode(newJson));
                WorkingDirectory.WriteAtomically(newJson, bytes);
            }

            DiagnosticLog.Info(Category, log);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int i = undo.Count - 1; i >= 0; i--)
            {
                Quietly(undo[i]);
            }

            return ex.Message;
        }
    }

    /// <summary>One file moved or copied, never over another, and its undo noted.</summary>
    private static void Step(string from, string to, bool copy, List<Action> undo)
    {
        if (copy)
        {
            File.Copy(from, to, overwrite: false);
            undo.Add(() => File.Delete(to));
        }
        else
        {
            File.Move(from, to, overwrite: false);
            undo.Add(() => File.Move(to, from, overwrite: false));
        }
    }

    /// <summary>
    /// What a rename or a duplicate changes inside the files: the output name when it is <c>neon/&lt;old name&gt;</c> (a graph with comments
    /// is left alone), and the family, written when the <c>.md</c> names none and the new name would guess another. Null where nothing changes.
    /// </summary>
    private static (string? Json, string? Md) Followed(ComfyFileText json, ComfyFileText md, string oldName, string newName)
    {
        string? jsonAfter = null;
        if (ComfyGraphEdit.Load(json.Text) is { Graph: { } graph }
            && ComfyGraphEdit.Read(graph).OutputName is { Editable: true, Value: { } prefix } part
            && string.Equals(prefix, ComfyGraphs.PrefixFolder + oldName, StringComparison.Ordinal)
            && ComfyGraphEdit.SetText(graph, part, ComfyGraphs.PrefixFolder + newName) is null)
        {
            // The one "neon/<old>" string replaced in the file's own text (2026-11-26, the review's #80), so its layout and characters stay; taken only
            // when it reads back as the same graph with the new name (an escaped prefix elsewhere would put the match in another node's text).
            string token = "\"" + ComfyGraphs.PrefixFolder + oldName + "\"";
            int at = json.Text.IndexOf(token, StringComparison.Ordinal);
            if (at >= 0 && json.Text.IndexOf(token, at + token.Length, StringComparison.Ordinal) < 0)
            {
                string raw = string.Concat(json.Text.AsSpan(0, at), "\"" + ComfyGraphs.PrefixFolder + newName + "\"", json.Text.AsSpan(at + token.Length));
                if (ComfyGraphEdit.Load(raw) is { Graph: { } again } && System.Text.Json.Nodes.JsonNode.DeepEquals(again, graph))
                {
                    jsonAfter = raw;
                }
            }

            jsonAfter ??= InEndings(graph.ToJsonString(ComfyGraphs.Indented), json.Text);
        }

        string? mdAfter = null;
        string mdText = md.Exists ? md.Text : "";
        bool named = ComfySidecarEdit.Get(mdText, "family") is { } word && ComfyFamilies.TryParse(word, out _);
        var guessed = ComfyFamilies.Guess(oldName);
        if (!named && guessed != ComfyFamilies.Guess(newName))
        {
            mdAfter = ComfySidecarEdit.SetWord(mdText, "family", ComfyFamilies.Name(guessed));
        }

        return (jsonAfter, mdAfter);
    }

    /// <summary>
    /// <paramref name="name"/> deleted from <paramref name="folder"/>: the <c>.json</c> first (the catalog then no longer lists it), then the
    /// <c>.md</c>, each through <see cref="FileDeletes.Remove"/> (to <paramref name="bin"/> when given). The <c>.json</c>'s way, or null with
    /// the error; a refused <c>.md</c> after a done <c>.json</c> gives both, the error saying the <c>.md</c> stays.
    /// </summary>
    public static (DeleteWay? Way, string? Error) Delete(string folder, string name, IRecycleBin? bin) =>
        DeleteFile(Path.Combine(folder, name + ".json"), name, bin);

    /// <summary><see cref="Delete"/> by the <c>.json</c>'s path: a skipped file has no name the catalog knows.</summary>
    public static (DeleteWay? Way, string? Error) DeleteFile(string jsonPath, string name, IRecycleBin? bin)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);
        ArgumentNullException.ThrowIfNull(name);
        string md = Path.ChangeExtension(jsonPath, ".md");
        if (!File.Exists(jsonPath))
        {
            return (null, ComfyText.WorkflowGone(name));
        }

        DeleteWay way;
        try
        {
            way = FileDeletes.Remove(jsonPath, isDirectory: false, isLink: false, bin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }

        DiagnosticLog.Info(Category, ComfyText.LogDeleted(name, Path.GetDirectoryName(jsonPath) ?? ""));
        try
        {
            if (File.Exists(md))
            {
                FileDeletes.Remove(md, isDirectory: false, isLink: false, bin);
            }

            return (way, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (way, ComfyText.SidecarStays(md, ex.Message));
        }
    }

    /// <summary>
    /// The workflow's <c>.md</c>, made when missing with its family (<c>/comfy edit</c>'s, and Open the Markdown's, 2026-11-25): its path and
    /// whether it was made, or the error.
    /// </summary>
    public static (string? Path, bool Created, string? Error) EnsureSidecar(string jsonPath, ComfyFamily family)
    {
        ArgumentNullException.ThrowIfNull(jsonPath);
        string md = Path.ChangeExtension(jsonPath, ".md");
        try
        {
            if (File.Exists(md))
            {
                return (md, false, null);
            }

            using (var stream = new FileStream(md, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(Encoding.UTF8.GetBytes("---\nfamily: " + ComfyFamilies.Name(family) + "\n---\n"));
            }

            return (md, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, false, ex.Message);
        }
    }

    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, ex.Message);
        }
    }
}

/// <summary>
/// A workflow file as read for an edit (2026-11-25, the workflow manager): its bytes (null when there is no file), its text and how it was
/// encoded, so a save can tell a change on disk since and write back in the file's own encoding and mark.
/// </summary>
public sealed class ComfyFileText
{
    private readonly TextCodec? _codec;

    private ComfyFileText(string path, byte[]? bytes, string text, TextCodec? codec)
    {
        Path = path;
        Bytes = bytes;
        Text = text;
        _codec = codec;
    }

    public string Path { get; }

    /// <summary>The bytes as read; null when the file wasn't there.</summary>
    public byte[]? Bytes { get; }

    /// <summary>The text ("" when the file wasn't there).</summary>
    public string Text { get; }

    public bool Exists => Bytes is not null;

    /// <summary>The file at <paramref name="path"/>, read whole; a missing one is <see cref="Exists"/> false.</summary>
    public static ComfyFileText Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return new ComfyFileText(path, null, "", null);
        }

        byte[] bytes = File.ReadAllBytes(path);
        string text = TextFile.Decode(bytes, out var codec);
        return new ComfyFileText(path, bytes, text, codec);
    }

    /// <summary>Whether the file on disk is still what was read (both missing counts).</summary>
    public bool Unchanged()
    {
        bool there = File.Exists(Path);
        return there == Exists && (!there || File.ReadAllBytes(Path).AsSpan().SequenceEqual(Bytes));
    }

    /// <summary><paramref name="text"/> in the file's own encoding and mark (UTF-8 for a new file); null when that encoding can't hold it.</summary>
    public byte[]? Encode(string text) => (_codec ?? TextCodec.Utf8).Encode(text);

    /// <summary>The bytes as read written back (a failed save's undo); a file that wasn't there is removed.</summary>
    public void PutBack()
    {
        if (Bytes is null)
        {
            File.Delete(Path);
        }
        else
        {
            WorkingDirectory.WriteAtomically(Path, Bytes);
        }
    }
}
