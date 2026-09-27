using System.Globalization;
using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>
/// The words of <c>/skills add</c> (2026-09-26): usage, errors, the pickers' and the install
/// pane's rows, the preview and the notices — shared by the screen and headless, so both say the
/// same. What a downloaded skill says of itself is someone else's text headed for a terminal:
/// <see cref="SanitizeForTerminal"/> strips its control characters (an ESC could repaint the screen)
/// before anything prints it.
/// </summary>
public static class SkillInstallText
{
    /// <summary>The verb after <c>/skills</c>.</summary>
    public const string AddWord = "add";

    /// <summary>The completion note for <see cref="AddWord"/>.</summary>
    public const string AddNote = "install a skill from skills.sh or GitHub: search words, owner/repo[/skill] or a URL";

    public const string UsageError = "Usage: /skills add <search words | owner/repo[/skill] | github url | zip url> [--global] [--yes]";

    public static string BadRepoError(string text) => $"'{text}' is not a GitHub repository: owner/repo or owner/repo/skill";

    public static string UnsupportedUrlError(string text) => $"'{text}' is neither a github.com repository nor an https URL of a .zip";

    public const string NotZipError = "The download is not a zip archive";

    public static string UnreadableZipError(string detail) => "The archive could not be read: " + detail;

    public static string TooManyEntriesError(int count, int max) =>
        $"The archive holds {Count(count)} entries, more than the {Count(max)} read";

    public static string SearchUnreadableError(string detail) => "skills.sh's answer could not be read: " + detail;

    // ── refusals of one skill ─────────────────────────────────────────────

    public static string SkillMdTooBigRefusal(long bytes, long max) => $"its SKILL.md is {FileText.Size(bytes)}, over the {FileText.Size(max)} cap";

    public static string BadNameRefusal(string name) => $"its name '{name}' is not a skill name ({SkillFrontmatter.NameRule})";

    public static string TooManyFilesRefusal(int count, int max) => $"it holds {Count(count)} files, over the {Count(max)} cap";

    public static string TooBigRefusal(long bytes, long max) => $"its files come to {FileText.Size(bytes)}, over the {FileText.Size(max)} cap";

    public static string FileTooBigRefusal(string path, long bytes, long max) => $"{path} is {FileText.Size(bytes)}, over the {FileText.Size(max)} cap for one file";

    public static string UnsafePathRefusal(string path) => $"{path} is not a path a Windows folder can hold safely";

    public static string CaseCollisionRefusal(string path) => $"{path} differs from another file only by case";

    public static string CannotInstallError(string name, string refusal) => $"'{name}' cannot be installed: {refusal}";

    // ── the flow ─────────────────────────────────────────────────────────

    public static string SearchingLabel(string query) => $"Searching skills.sh for '{query}'";

    public static string DownloadingLabel(string label) => $"Downloading {label}";

    /// <summary>A listing's SKILL.md files being fetched.</summary>
    public static string ReadingSkillsLabel(string label) => $"Reading the skills of {label}";

    /// <summary>A listing's one skill being fetched, after the yes.</summary>
    public static string FetchingSkillLabel(string name) => $"Downloading {name}";

    /// <summary>A GitHub source neither the API nor codeload has: the repository gone or private, or — when one was named — the branch.</summary>
    public static string RepoNotFoundError(SkillSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Ref == SkillSource.DefaultRef
            ? $"{source.RepoName} is not on GitHub: the repository was deleted or made private"
            : $"{source.Label} is not on GitHub: the repository or the ref '{source.Ref}' is gone, or the repository is private";
    }

    /// <summary>Added to <see cref="RepoNotFoundError"/> when the source came from a search.</summary>
    public const string StaleSearchHitNote = " (skills.sh still lists it; pick another result)";

    public static string TooManySkillsError(int count, int max) =>
        $"The repository holds {Count(count)} skills, more than the {Count(max)} listed; name one (owner/repo/skill) or give its folder's URL";

    public static string NoResultsError(string query) => $"skills.sh found no skill for '{query}'";

    public static string NoSkillsError(string label) => $"{label} holds no SKILL.md";

    public static string NoSuchSkillError(string id, IEnumerable<string> names)
    {
        var list = names.Take(12).ToList();
        return $"{id} is not a skill there" + (list.Count == 0 ? "" : " (found: " + string.Join(", ", list) + ")");
    }

    public const string PickHitTitle = "Install which skill?";
    public const string PickSkillTitle = "The repository holds several skills — install which?";
    public const string PickKeys = "↑↓ = move · Enter = preview · ESC = cancel";

    /// <summary>The gap between two columns.</summary>
    public const string ColumnGap = "  ";

    /// <summary>
    /// The search hits as rows in columns (2026-09-26, the user's ask: the <c>·</c>-joined rows wandered with the
    /// name's length): the name padded to the longest, the repository to the longest, the install count
    /// right-aligned to the widest. Widths in terminal cells (<see cref="UI.TextCells.Width"/>). Pure.
    /// </summary>
    public static IReadOnlyList<string> HitRows(IReadOnlyList<SkillsShSkill> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var names = hits.Select(h => Clean(h.Name)).ToList();
        var sources = hits.Select(h => Clean(h.Source)).ToList();
        var counts = hits.Select(h => Count(h.Installs)).ToList();
        int nameWidth = Widest(names);
        int sourceWidth = Widest(sources);
        int countWidth = Widest(counts);
        return hits.Select((h, i) => PadCells(names[i], nameWidth) + ColumnGap + PadCells(sources[i], sourceWidth) + ColumnGap
            + counts[i].PadLeft(countWidth) + " " + Installs(h.Installs)).ToList();
    }

    /// <summary>The skills of an archive as rows in columns: the name, the folder (<c>(root)</c> for the archive's root), and why one cannot be installed. Pure.</summary>
    public static IReadOnlyList<string> CandidateRows(IReadOnlyList<SkillCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var names = candidates.Select(c => Clean(c.Name)).ToList();
        var folders = candidates.Select(c => Clean(c.Folder.Length == 0 ? "(root)" : c.Folder)).ToList();
        int nameWidth = Widest(names);
        int folderWidth = Widest(folders);
        return candidates.Select((c, i) => c.Refusal is null
            ? (PadCells(names[i], nameWidth) + ColumnGap + folders[i]).TrimEnd()
            : PadCells(names[i], nameWidth) + ColumnGap + PadCells(folders[i], folderWidth) + ColumnGap + "✗ " + Clean(c.Refusal)).ToList();
    }

    /// <summary>Headless, several hits: the ids to type back, the install counts lined up after them. Pure.</summary>
    public static IReadOnlyList<string> HeadlessHitLines(IReadOnlyList<SkillsShSkill> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var ids = hits.Select(h => Clean(h.Id)).ToList();
        var counts = hits.Select(h => Count(h.Installs)).ToList();
        int idWidth = Widest(ids);
        int countWidth = Widest(counts);
        return hits.Select((h, i) => "  " + PadCells(ids[i], idWidth) + ColumnGap + counts[i].PadLeft(countWidth) + " " + Installs(h.Installs)).ToList();
    }

    /// <summary>Headless, several skills in one archive: the ids to type back, a refusal lined up after its id. Pure.</summary>
    public static IReadOnlyList<string> HeadlessCandidateLines(SkillSource source, IReadOnlyList<SkillCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);
        var ids = candidates.Select(c => Clean(source.Kind == SkillSourceKind.GitHub ? source.RepoName + "/" + c.Name : c.Name)).ToList();
        int idWidth = Widest(ids);
        return candidates.Select((c, i) => c.Refusal is null
            ? "  " + ids[i]
            : "  " + PadCells(ids[i], idWidth) + ColumnGap + "(" + Clean(c.Refusal) + ")").ToList();
    }

    private static string Installs(long count) => count == 1 ? "install" : "installs";

    private static int Widest(IEnumerable<string> cells) => cells.Select(UI.TextCells.Width).DefaultIfEmpty(0).Max();

    /// <summary><paramref name="text"/> padded with spaces to <paramref name="width"/> terminal cells.</summary>
    private static string PadCells(string text, int width) => text + new string(' ', Math.Max(0, width - UI.TextCells.Width(text)));

    public const string HeadlessPickHint = "Several skills match; /skills add <id> installs one.";

    public static string HeadlessNeedsYes(string name) => $"'{name}' was not installed: add --yes to install it ({SkillSource.GlobalFlag} for the global skills).";

    // ── the install pane ─────────────────────────────────────────────────

    public static string InstallTitle(string name) => $"Install {name}?";

    public const string ProfileRow = "Install to this profile's skills";
    public const string GlobalRow = "Install to the global skills (every profile)";
    public static string UpdateRow(SkillScope scope) => $"Replace the one in the {SkillScopes.Name(scope)} skills (update from the same source)";
    public const string CancelRow = "Cancel";
    public const string InstallKeys = "p / g / c = pick · Enter = choose · ESC = cancel";
    public const string UpdateKeys = "u / c = pick · Enter = choose · ESC = cancel";

    public static string InstallCaption(SkillCandidate candidate, SkillSource source)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(source);
        return $"{Clean(candidate.Name)} from {Clean(source.Label)}: {Count(candidate.Files.Count)} {(candidate.Files.Count == 1 ? "file" : "files")}, {FileText.Size(candidate.Bytes)}. Its scripts run only through run_command's approval.";
    }

    /// <summary>The typed-answer path's question, where no pane opens.</summary>
    public static string TypedQuestion(string name, SkillScope scope) => $"Install {name} to the {SkillScopes.Name(scope)} skills?";

    // ── outcomes ─────────────────────────────────────────────────────────

    public static string TakenError(string name, SkillScope scope) =>
        $"A skill named '{name}' is already in the {SkillScopes.Name(scope)} skills and did not come from this source; rename or delete it on /skills first";

    public static string ExternalReadOnlyError(string name) =>
        $"A skill named '{name}' is in the external folder, which the app never writes; switch Use external skills off or remove it there first";

    public static string FailedError(string name, string detail) => $"Could not install '{name}': {detail}";

    public static string InstalledNotice(string name, SkillScope scope, string directory) =>
        $"({ChatScreenGlyph} installed {name} to the {SkillScopes.Name(scope)} skills: {directory})";

    public static string UpdatedNotice(string name, SkillScope scope, string directory) =>
        $"({ChatScreenGlyph} updated {name} in the {SkillScopes.Name(scope)} skills: {directory})";

    public static string KeptNotice(string name) => $"({ChatScreenGlyph} {name} not installed)";

    public const string CancelledNotice = "(nothing installed)";

    public const string SkillsOffWarning = "Agent skills is off: the model is offered no skill until it is switched on (/skills, Options)";

    private const string ChatScreenGlyph = "🎓";

    // ── the preview ──────────────────────────────────────────────────────

    /// <summary>Files listed in the preview at most.</summary>
    public const int PreviewFiles = 30;

    /// <summary>The body's excerpt: lines and characters at most.</summary>
    public const int PreviewLines = 40;
    public const int PreviewChars = 3_000;

    /// <summary>File endings the preview calls scripts.</summary>
    public static readonly string[] ScriptExtensions = [".ps1", ".psm1", ".sh", ".bash", ".py", ".js", ".mjs", ".cjs", ".ts", ".bat", ".cmd", ".exe", ".dll", ".vbs", ".rb", ".pl"];

    /// <summary>
    /// What is about to be installed, as Markdown: the name and description, the source (ref,
    /// short commit, path), the frontmatter's other keys (<c>allowed-tools</c> and the rest), the
    /// files with their sizes, the scripts called out, and the first lines of the instructions in a
    /// fence. Every piece from the archive is sanitised. Pure.
    /// </summary>
    public static string PreviewMarkdown(SkillCandidate candidate, SkillSource source, string? commit)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(source);
        var sb = new StringBuilder();
        sb.Append("**").Append(Clean(candidate.Name)).Append("**");
        if (candidate.Frontmatter is { } front)
        {
            sb.Append(" — ").Append(Clean(front.Description));
        }

        sb.Append("\n\n");
        sb.Append("- Source: ").Append(Clean(source.Label));
        if (commit is not null)
        {
            sb.Append(" @ ").Append(commit[..Math.Min(7, commit.Length)]);
        }

        sb.Append('\n');
        sb.Append("- Folder: ").Append(candidate.Folder.Length == 0 ? "(the archive's root)" : Clean(candidate.Folder)).Append('\n');
        if (candidate.Frontmatter is { OtherLines.Count: > 0 } other)
        {
            foreach (string line in other.OtherLines.Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0])).Take(8))
            {
                sb.Append("- ").Append(Clean(line.Trim())).Append('\n');
            }
        }

        sb.Append("- Files: ").Append(Count(candidate.Files.Count)).Append(", ").Append(FileText.Size(candidate.Bytes)).Append('\n');
        foreach (var file in candidate.Files.Take(PreviewFiles))
        {
            sb.Append("  - ").Append(Clean(file.Path)).Append(" (").Append(FileText.Size(file.Length)).Append(")\n");
        }

        if (candidate.Files.Count > PreviewFiles)
        {
            sb.Append("  - …and ").Append(Count(candidate.Files.Count - PreviewFiles)).Append(" more\n");
        }

        foreach (string skipped in candidate.Skipped)
        {
            sb.Append("  - ").Append(Clean(skipped)).Append(" (a symbolic link, left out)\n");
        }

        var scripts = candidate.Files.Where(f => ScriptExtensions.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase)).Select(f => f.Path).ToList();
        if (scripts.Count > 0)
        {
            sb.Append("- Contains scripts: ").Append(Clean(string.Join(", ", scripts.Take(10)))).Append(scripts.Count > 10 ? ", …" : "")
                .Append(" — they run only through run_command's approval\n");
        }

        if (candidate.Body.Length > 0)
        {
            string excerpt = Excerpt(Clean(candidate.Body), out bool cut);
            string fence = Fence(excerpt);
            sb.Append('\n').Append(fence).Append("markdown\n").Append(excerpt).Append('\n').Append(fence).Append('\n');
            if (cut)
            {
                sb.Append("…\n");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Excerpt(string body, out bool cut)
    {
        string[] lines = body.Split('\n');
        string text = string.Join('\n', lines.Take(PreviewLines));
        cut = lines.Length > PreviewLines;
        if (text.Length > PreviewChars)
        {
            text = text[..PreviewChars];
            cut = true;
        }

        return text.TrimEnd();
    }

    /// <summary>A backtick fence longer than any run inside <paramref name="text"/>.</summary>
    private static string Fence(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return new string('`', Math.Max(3, longest + 1));
    }

    /// <summary>
    /// Someone else's text made safe for the terminal: every C0 and C1 control character (ESC
    /// first of all) and the bidirectional overrides removed, a tab and a newline kept, CRLF
    /// folded. Pure.
    /// </summary>
    public static string SanitizeForTerminal(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (c is '\n' or '\t' || !(char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩'))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string Clean(string text) => SanitizeForTerminal(text);

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
