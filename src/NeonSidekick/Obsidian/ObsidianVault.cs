using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Obsidian;

/// <summary>How <c>vault_write</c> puts its content in a note.</summary>
public enum VaultWriteMode
{
    Create,
    Overwrite,
    Append,
    Prepend,
}

/// <summary>What <c>vault_list</c> lists.</summary>
public enum VaultListing
{
    Notes,
    Tags,
    Properties,
}

/// <summary>
/// The user's Obsidian vault as the tools reach it (2026-09-22, the user's ask: "Obsidian integration"),
/// the <see cref="Git.GitAccess"/> counterpart — a folder of Markdown notes read and written straight on
/// disk, so it works whether Obsidian is running or not (Obsidian watches the folder and picks every
/// change up), needs no plugin, no network and starts no process.
///
/// <para>The root is the setting <c>Obsidian vault</c>, read on every call; it is a vault when it holds
/// a <c>.obsidian</c> folder (<see cref="IsVault"/>). Every path resolves inside it (<see cref="WorkingDirectory.IsInside"/>,
/// the sandbox's check on the path's spelling), and nothing under a dot-folder — <c>.obsidian</c> (the
/// app's config), <c>.trash</c>, <c>.git</c> — is ever written or listed: Obsidian's own. Notes are found
/// the way Obsidian resolves a link (<see cref="NoteIndex.Resolve"/>); the index is re-walked on each
/// call and re-reads only what changed. A write goes through <see cref="WorkingDirectory.WriteAtomically"/>
/// and keeps the note's byte-order mark and line endings; <see cref="Move"/> rewrites every link that
/// pointed at the note. Calls are serialised on one lock: the model's calls are sequential, but the
/// screen and a background check must never interleave a walk. Nothing throws for a model's mistake —
/// every refusal is an <see cref="ObsidianText"/> sentence starting <c>Error:</c>.</para>
/// </summary>
public sealed class ObsidianVault
{
    public const string ConfigFolderName = ".obsidian";
    public const string TrashFolderName = ".trash";
    public const string Category = "Obsidian";

    public const int DefaultSearchLimit = 50;
    public const int MaxSearchLimit = 200;
    public const int DefaultListLimit = 100;
    public const int MaxListLimit = 500;
    public const int MaxLinesShown = 200;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<string> _root;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly NoteIndex _index = new();

    /// <param name="root">The setting in force, read on every call: a save in <c>/tools</c> needs no rebind.</param>
    /// <param name="time">The clock "today" is read from for the daily note.</param>
    public ObsidianVault(Func<string> root, TimeProvider time)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Whether <paramref name="root"/> is a vault: an existing folder with a <c>.obsidian</c> folder in it.</summary>
    public static bool IsVault(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.Combine(root.Trim(), ConfigFolderName));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The root in force, full and without a trailing separator; empty when the setting is.</summary>
    public string Root
    {
        get
        {
            string configured = _root().Trim();
            if (configured.Length == 0)
            {
                return "";
            }

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return configured;
            }
        }
    }

    /// <summary>The root checked and the index current, or the refusal.</summary>
    private bool Open(out string root, out string error)
    {
        root = Root;
        if (root.Length == 0)
        {
            error = ObsidianText.NoVault;
            return false;
        }

        if (!IsVault(root))
        {
            error = ObsidianText.NotAVault(root);
            return false;
        }

        _index.Refresh(root);
        error = "";
        return true;
    }

    // ---- resolution ----

    /// <summary>A <c>note</c> argument as a link target: <c>[[Plan#Goals|the plan]]</c> as <c>Plan</c> (and its <c>#Goals</c>).</summary>
    public static string Reference(string note, out string subpath)
    {
        ArgumentNullException.ThrowIfNull(note);
        string text = note.Trim();
        if (text.StartsWith('!'))
        {
            text = text[1..];
        }

        if (text.StartsWith("[[", StringComparison.Ordinal) && text.EndsWith("]]", StringComparison.Ordinal))
        {
            text = text[2..^2];
        }

        int pipe = text.IndexOf('|', StringComparison.Ordinal);
        if (pipe >= 0)
        {
            text = text[..pipe];
        }

        NoteScanner.SplitSubpath(text, out string target, out subpath);
        return target;
    }

    /// <summary>The existing note <paramref name="note"/> names, or the refusal; <paramref name="also"/> the ambiguity line.</summary>
    private bool TryNote(string note, out NoteEntry entry, out string also, out string error)
    {
        entry = null!;
        also = "";
        string target = Reference(note, out _);
        if (target.Length == 0)
        {
            error = ObsidianText.Required("note");
            return false;
        }

        var found = _index.Resolve(target);
        if (found.Attachment is { } attachment)
        {
            error = ObsidianText.NotANote(attachment);
            return false;
        }

        if (found.Note is null)
        {
            string name = VaultPaths.NameOf(VaultPaths.Normalize(target));
            var near = _index.Notes.Where(n => n.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || n.Aliases.Any(a => a.Contains(name, StringComparison.OrdinalIgnoreCase)))
                .Take(5).Select(n => n.Relative).ToList();
            error = ObsidianText.NotFound(target, near);
            return false;
        }

        entry = found.Note;
        also = ObsidianText.AlsoNamed(entry.Name, found.Others);
        error = "";
        return true;
    }

    /// <summary>
    /// Where a new note named <paramref name="note"/> goes: a path as given (<c>.md</c> added); a bare name
    /// into the folder <c>app.json</c>'s "Default location for new notes" names (the root otherwise). Refused
    /// outside the vault, under a dot-folder, or with another extension.
    /// </summary>
    private bool TryNewPath(string root, string note, out string relative, out string full, out string error)
    {
        relative = full = "";
        string target = VaultPaths.Normalize(Reference(note, out _));
        if (target.Length == 0)
        {
            error = ObsidianText.Required("note");
            return false;
        }

        if (!target.Contains('/', StringComparison.Ordinal) && NewNoteFolder(root) is { Length: > 0 } folder)
        {
            target = folder + "/" + target;
        }

        return TryPath(root, target, out relative, out full, out error);
    }

    /// <summary>A vault-relative note path checked: inside, not hidden, <c>.md</c> (added when it has no extension).</summary>
    private static bool TryPath(string root, string target, out string relative, out string full, out string error)
    {
        relative = full = "";
        if (target.Split('/').FirstOrDefault(part => part is not ("." or "..") && !VaultPaths.IsValidName(part)) is { } bad)
        {
            error = ObsidianText.BadName(bad);
            return false;
        }

        if (!VaultPaths.IsNote(target))
        {
            if (VaultPaths.HasFileExtension(target))
            {
                error = ObsidianText.NotANote(target);
                return false;
            }

            target += VaultPaths.NoteExtension;
        }

        try
        {
            full = Path.GetFullPath(Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = ObsidianText.OutsideVault(target);
            return false;
        }

        if (!WorkingDirectory.IsInside(root, full) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            error = ObsidianText.OutsideVault(target);
            return false;
        }

        relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (VaultPaths.IsHidden(relative))
        {
            error = ObsidianText.HiddenPath(relative);
            return false;
        }

        error = "";
        return true;
    }

    private static string? NewNoteFolder(string root)
    {
        var config = ReadConfig(root, "app.json", ObsidianJsonContext.Default.ObsidianAppConfigFile);
        return config is { NewFileLocation: "folder", NewFileFolderPath: { } folder } ? VaultPaths.Normalize(folder) : null;
    }

    private static T? ReadConfig<T>(string root, string file, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        string path = Path.Combine(root, ConfigFolderName, file);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            DiagnosticLog.Debug(Category, $"Could not read {path}: {ex.Message}");
            return null;
        }
    }

    // ---- reading ----

    /// <summary>
    /// <c>vault_read</c>: the note's text, or one heading's section of it (the heading line to the next heading
    /// of the same or a higher level), or a window of lines — <paramref name="startLine"/> always a line of
    /// the whole note, so the header's <c>next: start_line</c> means the same with or without a heading.
    /// </summary>
    public string Read(string note, string heading, int? startLine, int? maxLines)
    {
        lock (_gate)
        {
            if (!Open(out _, out string error) || !TryNote(note, out var entry, out string also, out error))
            {
                return error;
            }

            if (NoteIndex.ReadLines(entry.Full) is not { } lines)
            {
                return ObsidianText.Failed("read", entry.Relative, "not a text file");
            }

            int from = 1;
            int to = lines.Count;
            if (heading.Trim().Length > 0)
            {
                if (Section(entry, heading, lines.Count) is not { } section)
                {
                    return ObsidianText.HeadingNotFound(entry.Relative, heading.Trim().TrimStart('#').Trim(), entry.Scan.Headings.Select(h => h.Text).ToList());
                }

                (from, to) = section;
            }

            int sectionEnd = to;
            if (startLine is { } s && s > from)
            {
                from = s;
            }

            if (maxLines is { } m && m > 0 && from + m - 1 < to)
            {
                to = from + m - 1;
            }

            var sb = new StringBuilder();
            bool truncated = false;
            int last = from - 1;
            for (int i = from; i <= to && i <= lines.Count; i++)
            {
                if (sb.Length + lines[i - 1].Length + 1 > WorkingDirectory.MaxReadChars)
                {
                    truncated = true;
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(lines[i - 1]);
                last = i;
            }

            var result = new ReadResult(FileOutcome.Ok, entry.Relative, sb.ToString(), lines.Count, from, last, truncated);
            string text = FileText.Read(result);
            if (heading.Trim().Length > 0 && last == sectionEnd && !truncated && last < lines.Count)
            {
                // The whole section read: the header's "next" would point past it, into the next heading's.
                int hint = text.IndexOf(FileText.NextHint(last + 1), StringComparison.Ordinal);
                text = hint < 0 ? text : text.Remove(hint, FileText.NextHint(last + 1).Length);
            }

            return ObsidianText.WithAlso(text, also);
        }
    }

    /// <summary>The 1-based line range of <paramref name="heading"/>'s section (<c>## Goals</c> or <c>Goals</c>, any case), or null.</summary>
    private static (int From, int To)? Section(NoteEntry entry, string heading, int lineCount)
    {
        string wanted = heading.Trim().TrimStart('#').Trim();
        var headings = entry.Scan.Headings;
        for (int i = 0; i < headings.Count; i++)
        {
            if (!string.Equals(headings[i].Text, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int end = lineCount;
            for (int j = i + 1; j < headings.Count; j++)
            {
                if (headings[j].Level <= headings[i].Level)
                {
                    end = headings[j].Line - 1;
                    break;
                }
            }

            return (headings[i].Line, end);
        }

        return null;
    }

    /// <summary>
    /// <c>vault_search</c>: the lines holding <paramref name="query"/> (any case), and the notes whose name or
    /// alias holds it, within <paramref name="folder"/> and <paramref name="tag"/> when given; frontmatter lines count.
    /// </summary>
    public string Search(string query, string tag, string folder, int? limit)
    {
        string q = query.Trim();
        if (q.Length == 0)
        {
            return ObsidianText.SearchNeedsQuery;
        }

        lock (_gate)
        {
            if (!Open(out _, out string error))
            {
                return error;
            }

            int cap = Math.Clamp(limit ?? DefaultSearchLimit, 1, MaxSearchLimit);
            var hits = new List<string>();
            var notes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var entry in Filter(folder, tag))
            {
                if (entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || entry.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    total++;
                    notes.Add(entry.Relative);
                    if (hits.Count < cap)
                    {
                        hits.Add(entry.Relative + ": (name)");
                    }
                }

                if (NoteIndex.ReadLines(entry.Full) is not { } lines)
                {
                    continue;
                }

                for (int i = 0; i < lines.Count; i++)
                {
                    if (!lines[i].Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    total++;
                    notes.Add(entry.Relative);
                    if (hits.Count < cap)
                    {
                        hits.Add(entry.Relative + ":" + (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + Clip(lines[i]));
                    }
                }
            }

            string scope = Scope(folder, tag);
            if (total == 0)
            {
                return $"no matches for \"{q}\"{scope}";
            }

            string head = $"{ObsidianText.Count(total, "match", "matches")} in {ObsidianText.Count(notes.Count, "note")} for \"{q}\"{scope}"
                + (total > hits.Count ? $" (first {hits.Count.ToString(CultureInfo.InvariantCulture)} shown; max_results raises it to {MaxSearchLimit.ToString(CultureInfo.InvariantCulture)})" : "") + ":";
            return head + "\n" + string.Join("\n", hits);
        }
    }

    private static string Scope(string folder, string tag)
    {
        var parts = new List<string>(2);
        if (VaultPaths.Normalize(folder) is { Length: > 0 } f)
        {
            parts.Add("in " + f + "/");
        }

        if (tag.Trim().TrimStart('#') is { Length: > 0 } t)
        {
            parts.Add("tagged #" + t);
        }

        return parts.Count == 0 ? "" : " " + string.Join(", ", parts);
    }

    private IEnumerable<NoteEntry> Filter(string folder, string tag)
    {
        string f = VaultPaths.Normalize(folder);
        string t = tag.Trim().TrimStart('#');
        return _index.Notes.Where(n =>
            (f.Length == 0 || n.Relative.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase))
            && (t.Length == 0 || n.HasTag(t)));
    }

    private static string Clip(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length <= WorkingDirectory.MaxLineChars ? trimmed : trimmed[..(WorkingDirectory.MaxLineChars - 1)] + "…";
    }

    /// <summary>
    /// <c>vault_list</c>: the notes (path order; with <paramref name="property"/> its value beside each), or every
    /// tag with how many notes carry it, or every property key with its count — within the filters given.
    /// </summary>
    public string List(VaultListing what, string folder, string tag, string property, string value, int? limit)
    {
        lock (_gate)
        {
            if (!Open(out _, out string error))
            {
                return error;
            }

            int cap = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);
            string key = property.Trim();
            string wanted = value.Trim();
            var notes = Filter(folder, tag).Where(n => key.Length == 0
                || (n.Frontmatter.Find(key) is { } p && (wanted.Length == 0 || NoteProperties.Matches(p, wanted)))).ToList();
            string scope = Scope(folder, tag) + (key.Length == 0 ? "" : wanted.Length == 0 ? $" with {key}" : $" with {key}: {wanted}");
            string cut = _index.Truncated ? $" (the vault is larger than {NoteIndex.MaxNotes.ToString("N0", CultureInfo.InvariantCulture)} notes; the rest are not indexed)" : "";
            switch (what)
            {
                case VaultListing.Tags:
                    var tags = notes.SelectMany(n => n.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                        .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                        .Select(g => (Tag: g.First(), Count: g.Count()))
                        .OrderByDescending(g => g.Count).ThenBy(g => g.Tag, StringComparer.OrdinalIgnoreCase).ToList();
                    return Listed(ObsidianText.Count(tags.Count, "tag") + scope + cut, tags.Select(t => $"#{t.Tag} ({t.Count.ToString(CultureInfo.InvariantCulture)})").ToList(), cap);
                case VaultListing.Properties:
                    var keys = notes.SelectMany(n => n.Frontmatter.Properties.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase))
                        .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
                        .Select(g => (Key: g.First(), Count: g.Count()))
                        .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
                    return Listed(ObsidianText.Count(keys.Count, "property", "properties") + scope + cut, keys.Select(k => $"{k.Key} ({k.Count.ToString(CultureInfo.InvariantCulture)})").ToList(), cap);
                default:
                    var rows = notes.Select(n => key.Length > 0 && n.Frontmatter.Find(key) is { } p ? n.Relative + " · " + NoteProperties.Describe(p).ReplaceLineEndings(" ") : n.Relative).ToList();
                    return Listed(ObsidianText.Count(notes.Count, "note") + scope + cut, rows, cap);
            }
        }
    }

    private static string Listed(string head, IReadOnlyList<string> rows, int cap)
    {
        if (rows.Count == 0)
        {
            return head;
        }

        string more = rows.Count > cap ? $"\n… {ObsidianText.Count(rows.Count - cap, "more")} (max_results raises the cap to {MaxListLimit.ToString(CultureInfo.InvariantCulture)})" : "";
        return head + ":\n" + string.Join("\n", rows.Take(cap)) + more;
    }

    /// <summary><c>vault_links</c>: the note's outgoing links (resolved or not), its embeds, and every link to it.</summary>
    public string Links(string note)
    {
        lock (_gate)
        {
            if (!Open(out _, out string error) || !TryNote(note, out var entry, out string also, out error))
            {
                return error;
            }

            var links = entry.Scan.Links.Where(l => !l.IsEmbed).ToList();
            var embeds = entry.Scan.Links.Where(l => l.IsEmbed).ToList();
            var back = _index.Backlinks(entry).Where(b => !ReferenceEquals(b.Source, entry)).ToList();
            int unresolved = links.Count(l => !_index.ResolveLink(entry, l).Found);
            var sb = new StringBuilder();
            sb.Append(entry.Relative).Append(": ").Append(ObsidianText.Count(links.Count, "link")).Append(" out");
            if (unresolved > 0)
            {
                sb.Append(" (").Append(unresolved.ToString(CultureInfo.InvariantCulture)).Append(" unresolved)");
            }

            sb.Append(", ").Append(ObsidianText.Count(embeds.Count, "embed")).Append(", ").Append(ObsidianText.Count(back.Count, "backlink"));
            Section("Links out", links);
            Section("Embeds", embeds);
            if (back.Count > 0)
            {
                sb.Append("\nBacklinks:");
                foreach (var (source, link) in back.Take(MaxLinesShown))
                {
                    sb.Append("\n  ").Append(source.Relative).Append(':').Append(link.Line.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(link.Raw);
                }

                if (back.Count > MaxLinesShown)
                {
                    sb.Append("\n  … ").Append(ObsidianText.Count(back.Count - MaxLinesShown, "more"));
                }
            }

            return ObsidianText.WithAlso(sb.ToString(), also);

            void Section(string title, List<NoteLink> list)
            {
                if (list.Count == 0)
                {
                    return;
                }

                sb.Append('\n').Append(title).Append(':');
                foreach (var link in list.Take(MaxLinesShown))
                {
                    var found = _index.ResolveLink(entry, link);
                    sb.Append("\n  ").Append(link.Raw).Append(" → ").Append(found.Relative ?? "unresolved")
                        .Append(" (line ").Append(link.Line.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                if (list.Count > MaxLinesShown)
                {
                    sb.Append("\n  … ").Append(ObsidianText.Count(list.Count - MaxLinesShown, "more"));
                }
            }
        }
    }

    // ---- writing ----

    /// <summary>A note's text as it sits on disk: decoded, with whether it had a byte-order mark and its line ending.</summary>
    private readonly record struct NoteText(string Text, bool Bom, string NewLine);

    private static bool TryReadText(string full, string relative, out NoteText text, out string error)
    {
        text = default;
        try
        {
            var info = new FileInfo(full);
            if (info.Length > WorkingDirectory.MaxTextFileBytes)
            {
                error = ObsidianText.TooLarge(relative);
                return false;
            }

            byte[] bytes = File.ReadAllBytes(full);
            if (WorkingDirectory.LooksBinary(bytes))
            {
                error = ObsidianText.Failed("edit", relative, "not a text file");
                return false;
            }

            string decoded = WorkingDirectory.Decode(bytes, out bool bom);
            text = new NoteText(decoded, bom, decoded.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ObsidianText.Failed("read", relative, ex.Message);
            return false;
        }
    }

    private static void WriteText(string full, string text, bool bom)
    {
        byte[] body = Utf8NoBom.GetBytes(text);
        if (bom)
        {
            body = [0xEF, 0xBB, 0xBF, .. body];
        }

        WorkingDirectory.WriteAtomically(full, body);
    }

    /// <summary>Lines joined with <paramref name="newLine"/>, ending with one; no lines, no text.</summary>
    private static string Join(IReadOnlyList<string> lines, string newLine) => lines.Count == 0 ? "" : string.Join(newLine, lines) + newLine;

    /// <summary>
    /// <c>vault_write</c>. <see cref="VaultWriteMode.Create"/> makes a new note and refuses an existing one;
    /// <see cref="VaultWriteMode.Overwrite"/> replaces the whole note, for good (until 2026-10-01 the setting <c>File safe edits</c>
    /// copied the previous version into the vault's <c>.trash</c> first; it went, and the user's call is that a write never
    /// trashes — only <see cref="Delete"/> uses the vault's <c>.trash</c>); <see cref="VaultWriteMode.Append"/> adds at the end, or at the end of
    /// <paramref name="heading"/>'s section; <see cref="VaultWriteMode.Prepend"/> adds right after the frontmatter, or right
    /// under the heading. The last three create a missing note (a heading then is refused). The content takes the note's line endings.
    /// </summary>
    public string Write(string note, string content, VaultWriteMode mode, string heading)
    {
        lock (_gate)
        {
            if (!Open(out string root, out string error))
            {
                return error;
            }

            NoteEntry? existing = null;
            string also = "";
            if (mode != VaultWriteMode.Create && Reference(note, out _).Length > 0)
            {
                var found = _index.Resolve(Reference(note, out _));
                existing = found.Note;
                also = existing is null ? "" : ObsidianText.AlsoNamed(existing.Name, found.Others);
                if (found.Attachment is { } attachment)
                {
                    return ObsidianText.NotANote(attachment);
                }
            }

            string relative;
            string full;
            if (existing is not null)
            {
                relative = existing.Relative;
                full = existing.Full;
            }
            else if (!TryNewPath(root, note, out relative, out full, out error))
            {
                return error;
            }
            else if (File.Exists(full) && mode == VaultWriteMode.Create)
            {
                return ObsidianText.Exists(relative);
            }

            string h = heading.Trim().TrimStart('#').Trim();
            bool created = !File.Exists(full);
            try
            {
                string result;
                if (created || mode is VaultWriteMode.Create or VaultWriteMode.Overwrite)
                {
                    if (created && h.Length > 0 && mode is VaultWriteMode.Append or VaultWriteMode.Prepend)
                    {
                        return ObsidianText.HeadingNotFound(relative, h, []);
                    }

                    bool bom = false;
                    string newLine = "\n";
                    if (!created)
                    {
                        if (!TryReadText(full, relative, out var old, out error))
                        {
                            return error;
                        }

                        (bom, newLine) = (old.Bom, old.NewLine);
                    }

                    string text = Join(WorkingDirectory.SplitLines(content), newLine);
                    WriteText(full, text, bom);
                    result = mode is VaultWriteMode.Append or VaultWriteMode.Prepend
                        ? ObsidianText.Inserted(relative, created: true, mode == VaultWriteMode.Append, "", text)
                        : ObsidianText.Wrote(relative, created, text);
                }
                else
                {
                    if (!TryReadText(full, relative, out var old, out error))
                    {
                        return error;
                    }

                    var lines = WorkingDirectory.SplitLines(old.Text);
                    var added = WorkingDirectory.SplitLines(content);
                    int at;
                    if (h.Length > 0)
                    {
                        var entry = existing ?? _index.Get(relative);
                        if (entry is null || Section(entry, h, lines.Count) is not { } section)
                        {
                            return ObsidianText.HeadingNotFound(relative, h, entry?.Scan.Headings.Select(x => x.Text).ToList() ?? []);
                        }

                        if (mode == VaultWriteMode.Prepend)
                        {
                            at = section.From;
                        }
                        else
                        {
                            at = section.To;
                            while (at > section.From && lines[at - 1].Trim().Length == 0)
                            {
                                at--;
                            }
                        }
                    }
                    else
                    {
                        at = mode == VaultWriteMode.Prepend ? NoteProperties.BodyStart(lines) : lines.Count;
                    }

                    lines.InsertRange(at, added);
                    string text = Join(lines, old.NewLine);
                    WriteText(full, text, old.Bom);
                    result = ObsidianText.Inserted(relative, created: false, mode == VaultWriteMode.Append, h, text);
                }

                _index.Update(relative, full);
                return ObsidianText.WithAlso(result, also);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ObsidianText.Failed("write", relative, ex.Message);
            }
        }
    }

    /// <summary>
    /// A free name in the vault's <c>.trash</c> for the file at <paramref name="full"/>, the folder made when missing:
    /// its own name, then <c>Plan 1.md</c>, <c>Plan 2.md</c>, … with <paramref name="extension"/> (2026-09-22; <see cref="Delete"/>
    /// keeps an attachment's own). Nothing there is ever overwritten.
    /// </summary>
    private static string TrashTarget(string root, string full, string extension)
    {
        string trash = Path.Combine(root, TrashFolderName);
        Directory.CreateDirectory(trash);
        string name = Path.GetFileNameWithoutExtension(full);
        string candidate = Path.Combine(trash, name + extension);
        for (int n = 1; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            candidate = Path.Combine(trash, name + " " + n.ToString(CultureInfo.InvariantCulture) + extension);
        }

        return candidate;
    }

    /// <summary>
    /// <c>vault_delete</c> (2026-09-22, the user's ask, behind the setting <c>Obsidian allow delete (.trash)</c>, on by default since 2026-09-23):
    /// one note — named as Obsidian names it — or one attachment by its path, moved into the vault's <c>.trash</c>
    /// under a free name (Obsidian's "Move to Obsidian trash"), never destroyed. A folder, a file under a dot-folder
    /// or a name nothing matches is refused. The links that still point at it are left alone (a deletion is no
    /// rename) and named in the result, so the model can tell the user what now reads as unresolved.
    /// </summary>
    public string Delete(string target)
    {
        lock (_gate)
        {
            if (!Open(out string root, out string error))
            {
                return error;
            }

            string reference = Reference(target, out _);
            string normalized = VaultPaths.Normalize(reference);
            if (normalized.Length == 0)
            {
                return ObsidianText.Required("note");
            }

            if (VaultPaths.IsHidden(normalized))
            {
                return ObsidianText.HiddenPath(normalized);
            }

            string asPath;
            try
            {
                asPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return ObsidianText.OutsideVault(normalized);
            }

            if (!WorkingDirectory.IsInside(root, asPath) || string.Equals(asPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return ObsidianText.OutsideVault(normalized);
            }

            if (Directory.Exists(asPath))
            {
                return ObsidianText.IsAFolder(Path.GetRelativePath(root, asPath).Replace('\\', '/'));
            }

            var found = _index.Resolve(reference);
            if (found.Relative is not { } relative)
            {
                string name = VaultPaths.NameOf(normalized);
                var near = _index.Notes.Where(n => n.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || n.Aliases.Any(a => a.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    .Take(5).Select(n => n.Relative).ToList();
                return ObsidianText.NotFound(reference, near);
            }

            string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            string also = found.Note is { } named ? ObsidianText.AlsoNamed(named.Name, found.Others) : "";

            // The notes whose links still reach it, read before the move: once it is gone they resolve to nothing.
            var linkedFrom = new List<string>();
            foreach (var source in _index.Notes)
            {
                if (ReferenceEquals(source, found.Note))
                {
                    continue;
                }

                bool links = source.Scan.Links.Any(link =>
                {
                    var hit = _index.ResolveLink(source, link);
                    return found.Note is not null
                        ? ReferenceEquals(hit.Note, found.Note)
                        : hit.Note is null && string.Equals(hit.Attachment, relative, StringComparison.OrdinalIgnoreCase);
                });
                if (links)
                {
                    linkedFrom.Add(source.Relative);
                }
            }

            linkedFrom.Sort(StringComparer.OrdinalIgnoreCase);
            try
            {
                string destination = TrashTarget(root, full, Path.GetExtension(full));
                File.Move(full, destination);
                DiagnosticLog.Info(Category, $"Deleted {relative} into {destination}");
                return ObsidianText.WithAlso(ObsidianText.Deleted(relative, TrashFolderName + "/" + Path.GetFileName(destination), linkedFrom, MaxLinesShown), also);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ObsidianText.Failed("delete", relative, ex.Message);
            }
        }
    }

    /// <summary>
    /// <c>vault_properties</c>: the note's properties; or, with <paramref name="set"/> (an object of keys and
    /// values) and/or <paramref name="remove"/> (keys), the note with those changed in one write — only the
    /// lines of the named keys touched (<see cref="NoteProperties"/>) — and the properties after.
    /// </summary>
    public string Properties(string note, JsonElement? set, IReadOnlyList<string> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        lock (_gate)
        {
            if (!Open(out _, out string error) || !TryNote(note, out var entry, out string also, out error))
            {
                return error;
            }

            if (set is null && remove.Count == 0)
            {
                return ObsidianText.WithAlso(DescribeProperties(entry), also);
            }

            if (!TryReadText(entry.Full, entry.Relative, out var old, out error))
            {
                return error;
            }

            var lines = WorkingDirectory.SplitLines(old.Text);
            var setKeys = new List<string>();
            if (set is { } changes)
            {
                foreach (var property in changes.EnumerateObject())
                {
                    string key = property.Name.Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!NoteProperties.TryRender(key, property.Value, out var rendered))
                    {
                        return ObsidianText.BadProperty(key);
                    }

                    lines = NoteProperties.Set(lines, key, rendered);
                    setKeys.Add(key);
                }
            }

            var removed = new List<string>();
            var missing = new List<string>();
            foreach (var key in remove.Select(k => k.Trim()).Where(k => k.Length > 0))
            {
                lines = NoteProperties.Remove(lines, key, out bool gone);
                (gone ? removed : missing).Add(key);
            }

            try
            {
                if (setKeys.Count > 0 || removed.Count > 0)
                {
                    WriteText(entry.Full, Join(lines, old.NewLine), old.Bom);
                    _index.Update(entry.Relative, entry.Full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ObsidianText.Failed("write", entry.Relative, ex.Message);
            }

            var after = _index.Get(entry.Relative) ?? entry;
            return ObsidianText.WithAlso(ObsidianText.PropertiesChanged(entry.Relative, setKeys, removed, missing) + "\n" + DescribeProperties(after), also);
        }
    }

    private static string DescribeProperties(NoteEntry entry)
    {
        var properties = entry.Frontmatter.Properties;
        var sb = new StringBuilder(ObsidianText.PropertiesHeader(entry.Relative, properties.Count));
        foreach (var property in properties)
        {
            sb.Append('\n').Append(NoteProperties.Describe(property));
        }

        var inline = entry.Scan.Tags.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (inline.Count > 0)
        {
            sb.Append("\n(inline tags: ").Append(string.Join(", ", inline.Select(t => "#" + t))).Append(')');
        }

        return sb.ToString();
    }

    /// <summary>The daily-notes settings in force: <c>.obsidian/daily-notes.json</c>, or the plugin's defaults.</summary>
    public DailyNoteSettings DailySettings(string root) =>
        DailyNotes.Settings(ReadConfig(root, "daily-notes.json", ObsidianJsonContext.Default.DailyNotesConfigFile));

    /// <summary>
    /// <c>vault_daily</c>: the daily note for <paramref name="date"/> (<see cref="DailyNotes.TryParseDay"/>, today by default),
    /// created from the plugin's template when missing — the way Obsidian's "Open today's daily note" does — with
    /// <paramref name="append"/> added at its end when given; the note's text after.
    /// </summary>
    public string Daily(string date, string append)
    {
        lock (_gate)
        {
            if (!Open(out string root, out string error))
            {
                return error;
            }

            var now = _time.GetLocalNow();
            if (!DailyNotes.TryParseDay(date, DateOnly.FromDateTime(now.DateTime), out var day))
            {
                return ObsidianText.BadDate(date.Trim());
            }

            var settings = DailySettings(root);
            if (!TryPath(root, DailyNotes.PathFor(settings, day), out string relative, out string full, out error))
            {
                return error;
            }

            var status = new List<string>(2);
            try
            {
                if (!File.Exists(full))
                {
                    string template = "";
                    string text = "";
                    if (settings.Template.Length > 0 && _index.Resolve(settings.Template).Note is { } source)
                    {
                        template = source.Relative;
                        var moment = day.ToDateTime(TimeOnly.FromDateTime(now.DateTime));
                        text = DailyNotes.ApplyTemplate(TryReadText(source.Full, source.Relative, out var t, out _) ? t.Text : "", VaultPaths.NameOf(relative), moment);
                    }

                    WriteText(full, text.Length == 0 ? "" : Join(WorkingDirectory.SplitLines(text), "\n"), bom: false);
                    status.Add(ObsidianText.CreatedFrom(template));
                }

                if (append.Trim().Length > 0)
                {
                    if (!TryReadText(full, relative, out var old, out error))
                    {
                        return error;
                    }

                    var lines = WorkingDirectory.SplitLines(old.Text);
                    lines.AddRange(WorkingDirectory.SplitLines(append));
                    WriteText(full, Join(lines, old.NewLine), old.Bom);
                    status.Add("appended");
                }

                _index.Update(relative, full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ObsidianText.Failed("write", relative, ex.Message);
            }

            if (!TryReadText(full, relative, out var current, out error))
            {
                return error;
            }

            var shown = WorkingDirectory.SplitLines(current.Text);
            string body = string.Join("\n", shown);
            if (body.Length > WorkingDirectory.MaxReadChars)
            {
                body = body[..WorkingDirectory.MaxReadChars] + "\n… (cut; vault_read reads the rest)";
            }

            string header = ObsidianText.DailyHeader(relative, string.Join(", ", status), shown.Count);
            return body.Length == 0 ? header.TrimEnd(':') : header + "\n" + body;
        }
    }

    // ---- moving ----

    /// <summary>One span of a note's line to replace.</summary>
    private readonly record struct LinkEdit(int Line, int Column, int Length, string Text);

    /// <summary>
    /// <c>vault_move</c>: the note renamed or moved to <paramref name="to"/> — a bare name renames it in its folder, a
    /// path ending <c>/</c> (or naming a folder) moves it there under its name, anything else is the new path — and every
    /// link that pointed at it rewritten, Obsidian's "Automatically update internal links": a wikilink by name keeps the
    /// name while it still finds the note alone, else takes the new name or, when that is shared, the path; a
    /// path-form or Markdown link takes the new path (a Markdown link relative to its note stays relative). The moved
    /// note's own relative Markdown links are re-aimed from its new folder. Every new text is computed before anything is
    /// written; the other notes are written first, then the note is moved, then its own rewrite lands.
    /// </summary>
    public string Move(string note, string to)
    {
        lock (_gate)
        {
            if (!Open(out string root, out string error) || !TryNote(note, out var entry, out _, out error))
            {
                return error;
            }

            string dest = VaultPaths.Normalize(to);
            if (dest.Length == 0)
            {
                return ObsidianText.Required("to");
            }

            bool intoFolder = to.Trim().EndsWith('/') || to.Trim().EndsWith('\\') || Directory.Exists(Path.Combine(root, dest.Replace('/', Path.DirectorySeparatorChar)));
            if (intoFolder)
            {
                dest = VaultPaths.Join(dest, Path.GetFileName(entry.Relative));
            }
            else if (!dest.Contains('/', StringComparison.Ordinal))
            {
                dest = VaultPaths.Join(entry.Folder, dest);
            }

            if (!TryPath(root, dest, out string newRelative, out string newFull, out error))
            {
                return error;
            }

            if (string.Equals(newRelative, entry.Relative, StringComparison.Ordinal))
            {
                return ObsidianText.NothingToMove;
            }

            if (File.Exists(newFull) && !string.Equals(newFull, entry.Full, StringComparison.OrdinalIgnoreCase))
            {
                return ObsidianText.Exists(newRelative);
            }

            string newName = VaultPaths.NameOf(newRelative);
            string newFolder = VaultPaths.FolderOf(newRelative);
            // Whether a bare name finds the moved note alone afterwards: no other note (or alias) goes by it.
            bool Unique(string name) => !_index.Notes.Any(n => !ReferenceEquals(n, entry) && (string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase) || n.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))));
            string newNoExt = newRelative[..^VaultPaths.NoteExtension.Length];

            // Every note's edits, computed before any write.
            var plans = new List<(NoteEntry Source, List<LinkEdit> Edits)>();
            foreach (var source in _index.Notes)
            {
                bool self = ReferenceEquals(source, entry);
                var edits = new List<LinkEdit>();
                foreach (var link in source.Scan.Links)
                {
                    if (link.Target.Length == 0)
                    {
                        continue;
                    }

                    var found = _index.ResolveLink(source, link);
                    string fromFolder = self ? newFolder : source.Folder;
                    if (ReferenceEquals(found.Note, entry))
                    {
                        if (Rewrite(link, found.By, fromFolder) is { } text && text != link.Raw)
                        {
                            edits.Add(new LinkEdit(link.Line, link.Column, link.Length, text));
                        }
                    }
                    else if (self && !link.IsWiki && found.By == ResolveBy.RelativePath && found.Relative is { } target
                        && !string.Equals(newFolder, entry.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        string url = NoteScanner.Encode(Markdownish(link, VaultPaths.RelativeFrom(newFolder, target))) + EncodeSubpath(link.Subpath);
                        edits.Add(new LinkEdit(link.Line, link.Column, link.Length, (link.IsEmbed ? "!" : "") + "[" + link.Alias + "](" + url + ")"));
                    }
                }

                if (edits.Count > 0)
                {
                    plans.Add((source, edits));
                }
            }

            var rewritten = new List<(NoteEntry Source, string Text, bool Bom, int Count)>();
            foreach (var (source, edits) in plans)
            {
                if (!TryReadText(source.Full, source.Relative, out var old, out error))
                {
                    return error;
                }

                rewritten.Add((source, Apply(old.Text, edits), old.Bom, edits.Count));
            }

            var done = new List<string>();
            try
            {
                foreach (var (source, text, bom, _) in rewritten.Where(r => !ReferenceEquals(r.Source, entry)))
                {
                    WriteText(source.Full, text, bom);
                    done.Add(source.Relative);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newFull)!);
                File.Move(entry.Full, newFull);
                if (rewritten.FirstOrDefault(r => ReferenceEquals(r.Source, entry)) is { Source: not null } own)
                {
                    WriteText(newFull, own.Text, own.Bom);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _index.Refresh(root);
                return ObsidianText.MoveFailedPartway(ex.Message, done);
            }

            _index.Refresh(root);
            int links = rewritten.Sum(r => r.Count);
            string result = ObsidianText.Moved(entry.Relative, newRelative, links, rewritten.Count);
            var list = rewritten.Select(r => (ReferenceEquals(r.Source, entry) ? newRelative : r.Source.Relative) + " (" + r.Count.ToString(CultureInfo.InvariantCulture) + ")").ToList();
            return list.Count == 0 ? result : result + ":\n" + string.Join("\n", list);

            string? Rewrite(NoteLink link, ResolveBy by, string fromFolder)
            {
                string bang = link.IsEmbed ? "!" : "";
                if (link.IsWiki)
                {
                    string target;
                    // A target with no folder in it is a name, whichever way it was found (a root note matches by path too).
                    bool byName = !link.Target.Contains('/', StringComparison.Ordinal);
                    if (byName && by == ResolveBy.Alias)
                    {
                        return null;   // an alias travels with the note's properties
                    }

                    if (byName && string.Equals(VaultPaths.NameOf(link.Target), newName, StringComparison.OrdinalIgnoreCase) && Unique(newName))
                    {
                        return null;
                    }

                    target = byName && Unique(newName) ? newName : newNoExt;
                    if (VaultPaths.IsNote(link.Target))
                    {
                        target += VaultPaths.NoteExtension;
                    }

                    string pipe = link.Raw.Contains("\\|", StringComparison.Ordinal) ? "\\|" : "|";   // a link in a table escapes its pipe
                    return bang + "[[" + target + link.Subpath + (link.Alias is null ? "" : pipe + link.Alias) + "]]";
                }

                if (by is ResolveBy.Name && !link.Target.Contains('/', StringComparison.Ordinal)
                    && string.Equals(VaultPaths.NameOf(link.Target), newName, StringComparison.OrdinalIgnoreCase) && Unique(newName))
                {
                    return null;
                }

                string path = by == ResolveBy.RelativePath ? VaultPaths.RelativeFrom(fromFolder, newRelative) : newRelative;
                string url = NoteScanner.Encode(Markdownish(link, path)) + EncodeSubpath(link.Subpath);
                return bang + "[" + link.Alias + "](" + url + ")";
            }
        }
    }

    /// <summary>A path the way the link wrote its own: with <c>.md</c> when it had one, without when it did not.</summary>
    private static string Markdownish(NoteLink link, string path) =>
        VaultPaths.IsNote(link.Target) || !VaultPaths.IsNote(path) ? path : path[..^VaultPaths.NoteExtension.Length];

    private static string EncodeSubpath(string subpath) =>
        subpath.Length == 0 ? "" : "#" + NoteScanner.Encode(subpath[1..]);

    /// <summary>The text with the edits applied, each span of its line replaced — from the last to the first, so the columns hold; the line endings as they were.</summary>
    private static string Apply(string text, List<LinkEdit> edits)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        var sb = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Line).ThenByDescending(e => e.Column))
        {
            int at = starts[edit.Line - 1] + edit.Column;
            sb.Remove(at, edit.Length).Insert(at, edit.Text);
        }

        return sb.ToString();
    }
}
