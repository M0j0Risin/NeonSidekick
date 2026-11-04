using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>
/// The trusted files' tripwire (2026-10-22, the fix plan's D8, after the reviews found the shell could do what the file tools refuse): a
/// <c>run_command</c> line, a script file it runs, an <c>execute_code</c> script or a <c>process</c> write that names <c>.git</c>,
/// <c>.neon</c> or <c>.claude</c> (a path segment, not <c>.gitignore</c> or <c>.claudeignore</c>), a <c>.mcp.json</c> (since 2026-11-02,
/// review P12-2: <c>/claude</c> loads both), the project file (<see cref="Llm.ProjectFile.FileNames"/>), the app's home or a package's install folder (2026-11-04, self-update) is
/// refused — the same inputs and callers as <see cref="SqlitePolice"/>, under <c>Shell police</c>. A bare <c>git</c> command is never read:
/// git reaching its own folder is git's business. Fed by <see cref="WriteGuard"/>'s facts, so the two never disagree. A tripwire like the
/// others: lexical, it sees the text, not what runs (a name built at run time passes), and says so in TOOLS.md. Pure but for the facts.
/// </summary>
public static partial class TrustedFilesPolice
{
    /// <summary>What a judgement is held to: the app's home (null when none is known), the working directory, and a package's install folder (null for none).</summary>
    public sealed record Guard(string? Home, string Root, string? InstallFolder = null);

    /// <summary>The guard over <paramref name="root"/> with the process's facts, or null while none are set (tests without an app).</summary>
    public static Guard? For(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return WriteGuard.Facts is { } facts ? new Guard(facts.Home(), root, facts.InstallFolder?.Invoke()) : null;
    }

    /// <summary>The first word of <paramref name="text"/> that names a trusted file or folder, else null.</summary>
    public static string? Find(string text, Guard guard)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        foreach (string segment in Segments().Split(text))
        {
            if (IsGit(segment))
            {
                continue;
            }

            foreach (Match token in Token().Matches(segment))
            {
                string word = token.Value.Trim('"', '\'', '`', '(', ')', ';', ',', '&', '|', '<', '>', '{', '}', '[', ']');
                if (word.Length > 0 && Names(word, guard))
                {
                    return word;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a segment is a git command: <c>git</c> (or <c>git.exe</c>) as its program, after any variable assignments.</summary>
    private static bool IsGit(string segment)
    {
        foreach (Match token in Token().Matches(segment))
        {
            string word = token.Value.Trim('"', '\'');
            if (word.Contains('=', StringComparison.Ordinal) && !word.StartsWith('-'))
            {
                continue;   // FOO=1 git …
            }

            string name = Path.GetFileName(word.Replace('\\', '/'));
            return name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.Equals("git.exe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="word"/> (a word <see cref="Find"/> returned) names Claude Code's files: a <c>.claude</c> segment or a
    /// <c>.mcp.json</c> (2026-11-02, review P12-2). Those get their own refusal: no tool of the model's changes them, only the user.
    /// </summary>
    public static bool IsClaudeFile(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        var parts = ValueOf(word.Replace('\\', '/')).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Equals(WriteGuard.ClaudeFolderName, StringComparison.OrdinalIgnoreCase))
            || (parts.Length > 0 && parts[^1].Equals(WriteGuard.ClaudeMcpFileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Past a key=value or a switch's colon, the value: --git-dir=.git, -o:.neon/x (a drive's colon kept).</summary>
    private static string ValueOf(string spelled)
    {
        int cut = spelled.LastIndexOfAny(['=', ':']);
        return cut >= 0 && !(cut == 1 && char.IsAsciiLetter(spelled[0])) ? spelled[(cut + 1)..] : spelled;
    }

    /// <summary>Whether <paramref name="word"/> names <c>.git</c>, <c>.neon</c>, Claude Code's files, the project file, the app's folder name, or a path under the home or the install folder. It can't tell a read from a write, so a full path there is refused either way; a relative one from a working folder inside passes, as for the home.</summary>
    private static bool Names(string word, Guard guard)
    {
        string value = ValueOf(word.Replace('\\', '/'));
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (IsClaudeFile(word))
        {
            return true;
        }

        foreach (string part in parts)
        {
            if (part.Equals(WorkingDirectory.GitFolderName, StringComparison.OrdinalIgnoreCase)
                || part.Equals(Plans.PlanSlug.AppFolder, StringComparison.OrdinalIgnoreCase)
                || part.Equals(HomeFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (parts.Length > 0 && Llm.ProjectFile.FileNames.Any(n => n.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (Path.IsPathRooted(value) || value.StartsWith('~'))
        {
            try
            {
                string full = Path.GetFullPath(value.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.TrimStart('~', '/')) : value);
                return Covers(guard.Home, full) || Covers(guard.InstallFolder, full);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool Covers(string? folder, string full) =>
        folder is { Length: > 0 } && (WorkingDirectory.IsInside(Path.GetFullPath(folder), full) || string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), StringComparison.OrdinalIgnoreCase));

    /// <summary>The app's own folder name under a user's profile (the default home).</summary>
    public const string HomeFolderName = ".neonsidekick";

    /// <summary>A line's segments: split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>&amp;</c> and line breaks.</summary>
    [GeneratedRegex(@"&&|\|\||[;|&\r\n]", RegexOptions.CultureInvariant)]
    private static partial Regex Segments();

    /// <summary>A segment's words: quoted runs whole, else runs without blanks.</summary>
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
