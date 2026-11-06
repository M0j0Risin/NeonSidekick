using System.IO.Enumeration;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>What the tripwire found, each with a sentence of its own (2026-11-04, review P14-3): "use the app's tools" is false of the install, which no tool changes.</summary>
public enum TrustedFinding
{
    /// <summary><c>.git</c>, <c>.neon</c>, Claude Code's files, the project file or the app's home.</summary>
    Trusted,

    /// <summary>A package's install folder, or the update files inside it.</summary>
    Install,

    /// <summary>A delete or a move of a folder holding the install or the home.</summary>
    HoldsAppFolder,
}

/// <summary>
/// The trusted files' tripwire (2026-10-22, the fix plan's D8, after the reviews found the shell could do what the file tools refuse): a
/// <c>run_command</c> line, a script file it runs, an <c>execute_code</c> script or a <c>process</c> write that names <c>.git</c>,
/// <c>.neon</c> or <c>.claude</c> (a path segment, not <c>.gitignore</c> or <c>.claudeignore</c>), a <c>.mcp.json</c> (since 2026-11-02,
/// review P12-2: <c>/claude</c> loads both), the project file (<see cref="Llm.ProjectFile.FileNames"/>), the app's home or a package's install folder (2026-11-04, self-update) is
/// refused, the same inputs and callers as <see cref="SqlitePolice"/>, under <c>Shell police</c>.
///
/// <para>A <c>git</c> command is read for the install alone (review P9-1): git reaching its own folder is git's business, and <c>git add CLAUDE.md</c> must go on working, but <c>git -C</c> into the install isn't. Words come from <see cref="CommandPrefix"/>'s own reading of each shell, so a quoted run stays whole and a comment, which runs nothing, doesn't trip it (reviews P16-3, P19-4). Install checks ask <see cref="WriteGuard"/> itself, identity included, so a package in a home, an 8.3 name, a link, <c>/tmp</c> and NFC can't make the two disagree (reviews P8-4, P16-4, P19-3).</para>
///
/// <para>A tripwire like the others: lexical, it sees the text, not what runs (a name built at run time passes), and says so in TOOLS.md. It's for plain spellings with no false refusals (the user's call, 2026-11-04): the write guard is the real protection. Pure but for the facts and, for a word that could resolve into the install, a look at the disk, each distinct word and folder once, since a script can be 1 MB.</para>
/// </summary>
public static class TrustedFilesPolice
{
    /// <summary>What a judgement is held to: the app's home (null when none is known), the working directory, a package's install folder (null for none), and the folder the command runs in, for what a relative word means (2026-11-04, reviews P8-3, P16-1), the working directory when none is given.</summary>
    public sealed record Guard(string? Home, string Root, string? InstallFolder = null, string? Workdir = null)
    {
        internal string Base => Workdir ?? Root;
    }

    /// <summary>The guard over <paramref name="root"/> with the process's facts, or null while none are set (tests without an app).</summary>
    public static Guard? For(string root, string? workdir = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return WriteGuard.Facts is { } facts ? new Guard(facts.Home(), root, facts.InstallFolder?.Invoke(), workdir) : null;
    }

    /// <summary>The first word of <paramref name="text"/> that names a trusted file or folder, else null.</summary>
    public static string? Find(string text, Guard guard) => Find(text, guard, out _);

    /// <summary>
    /// <see cref="Find(string, Guard)"/>, saying what it found. <paramref name="kind"/> is the shell's name, as a request carries it; one that names no shell (<c>execute_code</c>'s languages) and a script read by PowerShell's rules. A script is cut at its line breaks first (review P16-3): a stray apostrophe in a JavaScript comment would otherwise join the lines after it into one string and hide a program.
    /// </summary>
    public static string? Find(string text, Guard guard, out TrustedFinding finding, string? kind = null, bool isScript = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        finding = TrustedFinding.Trusted;
        var shell = ShellKind.PowerShell;
        if (!isScript)
        {
            ShellKinds.TryParse(kind, out shell);
        }

        var judging = new Judging(guard);
        if (judging.BaseInUpdateFolder())
        {
            finding = TrustedFinding.Install;
            return guard.Base;
        }

        foreach (string line in isScript ? text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : [text])
        {
            foreach (string segment in CommandPrefix.Segments(line, shell))
            {
                var words = Words(segment, shell);
                bool git = IsGit(words);
                foreach (string raw in words)
                {
                    string word = raw.Trim(WordEdges);
                    if (word.Length == 0 || IsSwitch(word, shell))
                    {
                        continue;
                    }

                    foreach (string spelled in Spellings(word, isScript))
                    {
                        foreach (string value in ValuesOf(spelled))
                        {
                            if (!git && NamesTrusted(value, guard))
                            {
                                return word;
                            }

                            if (judging.NamesInstall(value))
                            {
                                finding = TrustedFinding.Install;
                                return word;
                            }
                        }
                    }
                }

                if (!git && judging.DropsAppFolder(segment, words, shell, isScript) is { } dropped)
                {
                    finding = TrustedFinding.HoldsAppFolder;
                    return dropped;
                }
            }
        }

        return null;
    }

    private static readonly char[] WordEdges = ['"', '\'', '`', '(', ')', ';', ',', '&', '|', '{', '}', '[', ']'];

    // The programs a delete or a move of a folder takes, with their aliases, in lower case as CommandPrefix.Of answers (2026-11-04, review P19-5, the user's call).
    private static readonly IReadOnlySet<string> DropPrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "del", "erase", "rd", "ri", "remove-item",
        "mv", "move", "mi", "move-item",
        "ren", "rename", "rni", "rename-item",
    };

    private static readonly IReadOnlySet<string> MovePrograms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mv", "move", "mi", "move-item", "ren", "rename", "rni", "rename-item",
    };

    private static readonly string[] DestinationOptions = ["-destination", "-newname", "--target-directory"];

    /// <summary>
    /// A segment's words as its shell reads them. A word read to the end from an unbalanced apostrophe is read again split at blanks (review P16-3): in <c>echo don't touch .git/x</c> the apostrophe is literal to a reader, and it mustn't hide the rest.
    /// </summary>
    private static List<string> Words(string segment, ShellKind shell)
    {
        var tokens = CommandPrefix.Tokens(segment, shell, out bool lastIsOpen);
        if (lastIsOpen && tokens.Count > 0)
        {
            string open = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
            tokens.AddRange(open.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        return tokens;
    }

    /// <summary>Whether a segment is a git command: <c>git</c> (or <c>git.exe</c>) as its program, after any variable assignments.</summary>
    private static bool IsGit(List<string> words)
    {
        foreach (string raw in words)
        {
            string word = raw.Trim('"', '\'');
            if (word.Contains('=', StringComparison.Ordinal) && !word.StartsWith('-'))
            {
                continue;   // FOO=1 git …
            }

            string name = Path.GetFileName(word.Replace('\\', '/'));
            return name.Equals("git", StringComparison.OrdinalIgnoreCase) || name.Equals("git.exe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // On Windows a lone /s is a switch, not a folder at the drive's root (review P16-5: rmdir /s read as naming D:\s). On a Mac it stays a path. As PathPolice reads one.
    private static bool IsSwitch(string word, ShellKind shell) =>
        OperatingSystem.IsWindows() && shell != ShellKind.Bash && word.Length == 2 && word[0] == '/' && char.IsAsciiLetter(word[1]);

    // Off Windows a command line's backslash escapes (review P19-4), so My\ Apps reads as the shell does, and as spelled too, so .git\config still trips on both systems. A script's isn't the shell's escape, as in PathPolice.
    private static IEnumerable<string> Spellings(string word, bool isScript)
    {
        yield return word;
        if (!isScript && !OperatingSystem.IsWindows() && word.Contains('\\', StringComparison.Ordinal))
        {
            yield return PathPolice.Unescape(word);
        }
    }

    /// <summary>
    /// What a word may name: with its quotes gone and split at a redirect joined to it (<c>evil.dll&gt;D:/x.dll</c>, <c>1&gt;D:/x.dll</c>), a rooted path whole, even past a later <c>=</c>, and a relative word on both sides of an <c>=</c> or a switch's colon, so <c>--git-dir=.git</c> is read and <c>.git/hooks/pre=commit</c> keeps its <c>.git</c>. A drive needs a Windows host and a slash (2026-11-04, reviews P7-3, P9-4, P14-2, P16-2).
    /// </summary>
    internal static List<string> ValuesOf(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        string flat = word.Replace('\\', '/').Replace("\"", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        var values = new List<string>();
        foreach (string piece in flat.Split(['<', '>'], StringSplitOptions.RemoveEmptyEntries))
        {
            AddValues(piece, values);
        }

        return values;
    }

    private static void AddValues(string piece, List<string> values)
    {
        int cut = IsRooted(piece) ? -1 : piece.IndexOfAny(['=', ':']);
        if (cut < 0)
        {
            values.Add(piece);
            return;
        }

        if (cut > 0)
        {
            values.Add(piece[..cut]);
        }

        if (cut + 1 < piece.Length)
        {
            AddValues(piece[(cut + 1)..], values);
        }
    }

    private static bool IsRooted(string value) =>
        value.StartsWith('/') || value.StartsWith('~') || (OperatingSystem.IsWindows() && value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '/');

    private static string[] PartsOf(string value) => value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether <paramref name="word"/> (a word <see cref="Find(string, Guard)"/> returned) names Claude Code's files: a <c>.claude</c> segment or a
    /// <c>.mcp.json</c> (2026-11-02, review P12-2). Those get their own refusal: no tool of the model's changes them, only the user.
    /// </summary>
    public static bool IsClaudeFile(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return ValuesOf(word).Any(IsClaudeValue);
    }

    private static bool IsClaudeValue(string value)
    {
        var parts = PartsOf(value);
        return parts.Any(part => part.Equals(WriteGuard.ClaudeFolderName, StringComparison.OrdinalIgnoreCase))
            || (parts.Length > 0 && parts[^1].Equals(WriteGuard.ClaudeMcpFileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="value"/> names <c>.git</c>, <c>.neon</c>, Claude Code's files, the project file, the app's folder name, or a path under the home. It can't tell a read from a write, so a full path there is refused either way; a relative one from a working folder inside the home passes.</summary>
    private static bool NamesTrusted(string value, Guard guard)
    {
        if (IsClaudeValue(value))
        {
            return true;
        }

        var parts = PartsOf(value);
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

        return IsRooted(value) && guard.Home is { Length: > 0 } home && Full(value, guard) is { } full && WorkingDirectory.IsInside(Path.GetFullPath(home), Path.TrimEndingDirectorySeparator(full));
    }

    // The word on disk, from the folder the command runs in. Null for one no path can be made of.
    private static string? Full(string value, Guard guard)
    {
        try
        {
            string spelled = value.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.TrimStart('~', '/')) : value;
            return Path.GetFullPath(spelled, Path.GetFullPath(guard.Base));   // the base's own drive for a rooted /x, not the current one's
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // The ? of a \\?\ prefix is no wildcard (2026-11-05, found writing review P10-12's test): the write guard's own spelling read as a pattern that matched nothing.
    private static bool HasWildcard(string path) =>
        (path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.AsSpan(4) : path.AsSpan()).IndexOfAny('*', '?') >= 0;

    private static string[] Steps(string path) => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    // A wildcard path against a target, step by step, under the target's case rule: true when every step they share could match. The caller decides what a shorter or longer pattern means.
    private static bool StepsMatch(string[] pattern, string[] target, string caseFolder)
    {
        bool ignoreCase = CaseRule.Comparison(caseFolder) == StringComparison.OrdinalIgnoreCase;
        for (int i = 0; i < Math.Min(pattern.Length, target.Length); i++)
        {
            if (!FileSystemName.MatchesSimpleExpression(pattern[i], target[i], ignoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One <see cref="Find(string, Guard, out TrustedFinding, string?, bool)"/>'s answers, each distinct word and folder judged once (review P8-3's cost): relative words now reach the disk, identity opens every ancestor, and a script can be 1 MB.</summary>
    private sealed class Judging(Guard guard)
    {
        private readonly string? _install = guard.InstallFolder is { Length: > 0 } install ? Path.GetFullPath(install) : null;
        private readonly Dictionary<string, bool> _words = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _folders = new(StringComparer.Ordinal);
        private bool? _baseInInstall;

        public bool BaseInUpdateFolder() =>
            _install is { } install && WorkingDirectory.IsInside(Path.Combine(install, Updates.UpdateFolder.Name), Path.TrimEndingDirectorySeparator(Path.GetFullPath(guard.Base)));

        public bool NamesInstall(string value)
        {
            if (_install is null)
            {
                return false;
            }

            if (!_words.TryGetValue(value, out bool named))
            {
                named = Judge(value, _install);
                _words[value] = named;
            }

            return named;
        }

        // With an install known (review P2-6): a rooted word into it always, since the police can't tell a read from a write. A relative word resolves from where the command runs (reviews P8-3, P16-1), and counts for .update and the manifest by name, only there (review P14-1: CSS in D:\site named .update too), a wildcard, what exists, or a new library's name (review P8-5). Nothing else, or every switch and new name would be refused, and not inside a home kept there, or a portable install's sandbox would refuse every command.
        private bool Judge(string value, string install)
        {
            bool rooted = IsRooted(value);

            // A word with no separator takes its folder's answer unless the folder is in the install: no disk for the common case. The install's own name still counts.
            if (!rooted && !value.Contains('/', StringComparison.Ordinal) && value is not ("." or "..") && !BaseInInstall(install) && !value.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(install)), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (Full(value, guard) is not { } full || !Into(full, install))
            {
                return false;
            }

            if (rooted)
            {
                return true;
            }

            var parts = PartsOf(value);
            return parts.Length > 0
                && (parts.Any(part => part.Equals(Updates.UpdateFolder.Name, StringComparison.OrdinalIgnoreCase))
                    || parts[^1].Equals(Updates.UpdateManifest.FileName, StringComparison.OrdinalIgnoreCase)
                    || HasWildcard(value)
                    || File.Exists(full)
                    || Directory.Exists(full)
                    || Path.GetExtension(parts[^1]) is { } extension && (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dylib", StringComparison.OrdinalIgnoreCase) || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)));
        }

        private bool BaseInInstall(string install)
        {
            _baseInInstall ??= WriteGuard.InInstall(install, guard.Home, Path.TrimEndingDirectorySeparator(Path.GetFullPath(guard.Base)));
            return _baseInInstall.Value;
        }

        // What the write guard says, a wildcard read step by step, and a folder's answer once: only a folder that exists can be the install itself, so what isn't in a folder outside it never opens its ancestors again.
        private bool Into(string full, string install)
        {
            if (HasWildcard(full))
            {
                string[] pattern = Steps(full);
                string[] target = Steps(install);
                if (pattern.Length >= target.Length && StepsMatch(pattern, target, install))
                {
                    return true;
                }

                int wild = Array.FindIndex(pattern, step => HasWildcard(step));
                string fixedPart = string.Join(Path.DirectorySeparatorChar, pattern[..wild]);
                return wild > 0 && FolderInInstall(full.StartsWith('/') ? Path.DirectorySeparatorChar + fixedPart : fixedPart, install);
            }

            string plain = Path.TrimEndingDirectorySeparator(full);
            if (Path.GetDirectoryName(plain) is { } folder && FolderInInstall(folder, install))
            {
                return true;
            }

            return Directory.Exists(plain) && WriteGuard.InInstall(install, guard.Home, plain);
        }

        private bool FolderInInstall(string folder, string install)
        {
            if (!_folders.TryGetValue(folder, out bool inside))
            {
                inside = WriteGuard.InInstall(install, guard.Home, folder);
                _folders[folder] = inside;
            }

            return inside;
        }

        /// <summary>
        /// The word of a delete or move that takes a folder holding the install or the home, or null (2026-11-04, review P19-5, the user's call): <c>rm -rf /Users/me/*</c> took the app with it, as the file tools refuse. Read with <see cref="CommandPrefix.Of"/>, so <c>env</c> and <c>nohup</c> are read through; for what it can't read (<c>sudo</c>, <c>xargs</c>, <c>eval</c>) any word on the list counts. A move's destination doesn't count, so <c>mv report.txt ~</c> passes, and <c>ls ~</c> does, since only this list counts.
        /// </summary>
        public string? DropsAppFolder(string segment, List<string> words, ShellKind shell, bool isScript)
        {
            if (_install is null && guard.Home is not { Length: > 0 })
            {
                return null;
            }

            string prefix = CommandPrefix.Of(segment, shell: shell);
            int program = words.FindIndex(word => DropPrograms.Contains(CommandPrefix.Program(word.Trim(WordEdges))));
            if (program < 0 || (prefix != CommandPrefix.Unreadable && !DropPrograms.Contains(CommandPrefix.Program(prefix.Split(' ')[0]))))
            {
                return null;
            }

            int destination = DestinationAt(words, program);
            for (int i = program + 1; i < words.Count; i++)
            {
                string word = words[i].Trim(WordEdges);
                if (i == destination || word.Length == 0 || IsSwitch(word, shell))
                {
                    continue;
                }

                foreach (string spelled in Spellings(word, isScript))
                {
                    foreach (string value in ValuesOf(spelled))
                    {
                        if (!value.StartsWith('-') && Full(value, guard) is { } full && (Holds(full, _install) || Holds(full, guard.Home)))
                        {
                            return word;
                        }
                    }
                }
            }

            return null;
        }

        // The destination's index, or -1: the value of -Destination or -t if it has one, else the last plain word when there are two or more.
        private static int DestinationAt(List<string> words, int program)
        {
            string name = CommandPrefix.Program(words[program].Trim(WordEdges));
            if (!MovePrograms.Contains(name))
            {
                return -1;
            }

            int lastPlain = -1;
            int plain = 0;
            for (int i = program + 1; i < words.Count; i++)
            {
                string word = words[i].Trim(WordEdges);
                string head = word.Split(['=', ':'], 2)[0];
                if (head.Equals("-t", StringComparison.Ordinal) || (head.Length > 2 && DestinationOptions.Any(option => option.StartsWith(head, StringComparison.OrdinalIgnoreCase))))
                {
                    return word.Length > head.Length ? i : i + 1;   // joined to its option, or the next word
                }

                if (!word.StartsWith('-'))
                {
                    plain++;
                    lastPlain = i;
                }
            }

            return plain >= 2 ? lastPlain : -1;
        }

        private static bool Holds(string full, string? target)
        {
            if (target is not { Length: > 0 })
            {
                return false;
            }

            string goal = Path.GetFullPath(target);
            if (HasWildcard(full))
            {
                string[] pattern = Steps(full);
                string[] steps = Steps(goal);
                return pattern.Length <= steps.Length && StepsMatch(pattern, steps, goal);
            }

            return WriteGuard.HoldsFolder(Path.TrimEndingDirectorySeparator(full), goal);
        }
    }

    /// <summary>The app's own folder name under a user's profile (the default home).</summary>
    public const string HomeFolderName = ".neonsidekick";
}
