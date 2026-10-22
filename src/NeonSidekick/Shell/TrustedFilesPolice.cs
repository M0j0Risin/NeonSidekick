using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>
/// The trusted files' tripwire (2026-10-22, the fix plan's D8, after the reviews found the shell could do what the file tools refuse): a
/// <c>run_command</c> line, a script file it runs, an <c>execute_code</c> script or a <c>process</c> write that names <c>.git</c> or
/// <c>.neon</c> (a path segment, not <c>.gitignore</c>), the project file (<see cref="Llm.ProjectFile.FileNames"/>) or the app's home is
/// refused — the same inputs and callers as <see cref="SqlitePolice"/>, under <c>Shell police</c>. A bare <c>git</c> command is never read:
/// git reaching its own folder is git's business. Fed by <see cref="WriteGuard"/>'s facts, so the two never disagree. A tripwire like the
/// others: lexical, it sees the text, not what runs (a name built at run time passes), and says so in TOOLS.md. Pure but for the facts.
/// </summary>
public static partial class TrustedFilesPolice
{
    /// <summary>What a judgement is held to: the app's home (null when none is known) and the working directory.</summary>
    public sealed record Guard(string? Home, string Root);

    /// <summary>The guard over <paramref name="root"/> with the process's facts, or null while none are set (tests without an app).</summary>
    public static Guard? For(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return WriteGuard.Facts is { } facts ? new Guard(facts.Home(), root) : null;
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

    /// <summary>Whether <paramref name="word"/> names <c>.git</c>, <c>.neon</c>, the project file, the app's folder name, or a path under the home.</summary>
    private static bool Names(string word, Guard guard)
    {
        string spelled = word.Replace('\\', '/');
        // Past a key=value or a switch's colon, the value: --git-dir=.git, -o:.neon/x.
        int cut = spelled.LastIndexOfAny(['=', ':']);
        string value = cut >= 0 && !(cut == 1 && char.IsAsciiLetter(spelled[0])) ? spelled[(cut + 1)..] : spelled;
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

        if (guard.Home is { Length: > 0 } home && (Path.IsPathRooted(value) || value.StartsWith('~')))
        {
            try
            {
                string full = Path.GetFullPath(value.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.TrimStart('~', '/')) : value);
                return WorkingDirectory.IsInside(Path.GetFullPath(home), full) || string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>The app's own folder name under a user's profile (the default home).</summary>
    public const string HomeFolderName = ".neonsidekick";

    /// <summary>A line's segments: split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>&amp;</c> and line breaks.</summary>
    [GeneratedRegex(@"&&|\|\||[;|&\r\n]", RegexOptions.CultureInvariant)]
    private static partial Regex Segments();

    /// <summary>A segment's words: quoted runs whole, else runs without blanks.</summary>
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
