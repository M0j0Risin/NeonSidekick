namespace NeonSidekick.Llm;

/// <summary>The project notes as the prompt carries them: which file they came from and the text.</summary>
public sealed record ProjectNotes(string FileName, string Text);

/// <summary>
/// The working directory's own notes for the model: <c>NEON.md</c> in its root, else
/// <c>AGENTS.md</c> (the cross-client convention), else <c>CLAUDE.md</c> (Claude Code's; 2026-10-18, the user's ask) — the first
/// that exists wins. Read into
/// the system prompt after the operating rules on every turn while <c>Agent skills</c> and (later
/// on 2026-09-19) <c>Project file</c> — the toggle on <c>/skills</c>' Options tab (its Project tab until 2026-10-01) — are on,
/// so a <c>/cwd</c> swaps the notes with the folder; absent or blank, nothing is added. The
/// mechanics are <see cref="PromptFile"/>'s over a live path: the folder is asked for on every
/// read. No default text and no seeding — the file is the project's, not the profile's. Only the folder's own <c>CLAUDE.md</c> is
/// read (never <c>.claude/CLAUDE.md</c> or <c>CLAUDE.local.md</c>), its <c>@path</c> imports as plain text, not followed.
/// </summary>
public sealed class ProjectFile : PromptFile
{
    /// <summary>The three names, in precedence order (two until 2026-10-18).</summary>
    public static readonly string[] FileNames = { "NEON.md", "AGENTS.md", "CLAUDE.md" };

    public const string PrimaryFileName = "NEON.md";
    public const string SecondaryFileName = "AGENTS.md";

    /// <summary>Claude Code's file, read when neither of the others is there (2026-10-18, the user's ask).</summary>
    public const string TertiaryFileName = "CLAUDE.md";

    /// <summary>Characters kept: a project file runs longer than a persona, and the prompt goes out on every request.</summary>
    public const int MaxLength = 16_000;

    public const string Category = "Project";

    /// <param name="directory">The working directory's root, asked for on every read.</param>
    public ProjectFile(Func<string> directory)
        : base(() => Locate(directory()), "", Category, "Project notes", MaxLength)
    {
        ArgumentNullException.ThrowIfNull(directory);
    }

    /// <summary>
    /// The file in play under <paramref name="directory"/>: the first of <see cref="FileNames"/> that exists, else <c>AGENTS.md</c>
    /// (present or not), as before <c>CLAUDE.md</c> joined on 2026-10-18 (the user's ask), so a folder with none still names it.
    /// </summary>
    public static string Locate(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        foreach (string name in FileNames)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, SecondaryFileName);
    }

    /// <summary>The notes for this turn, or null when neither file is there or the one there is blank.</summary>
    public ProjectNotes? ReadNotes()
    {
        string? text = Read();
        return text is null ? null : new ProjectNotes(CurrentFileName, text);
    }

    /// <summary>The notes as the prompt carries them: CRLF folded to LF, trimmed, cut at <see cref="MaxLength"/> with an ellipsis. Pure; pinned.</summary>
    public static string Normalize(string raw) => Normalize(raw, out _);

    public static string Normalize(string raw, out bool truncated) => Normalize(raw, MaxLength, out truncated);

    public static string TruncatedWarning(string fileName, int length) => TruncatedWarning(fileName, MaxLength, length);

    public static string UnreadableWarning(string fileName, string detail) => UnreadableWarning(fileName, "project notes", detail);
}
