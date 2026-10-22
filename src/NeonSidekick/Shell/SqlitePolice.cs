using System.Text.RegularExpressions;
using NeonSidekick.Files;

namespace NeonSidekick.Shell;

/// <summary>
/// The shell police's SQLite rule (2026-10-05, the user's call after a model, under Shell command policy yolo, wrote a script
/// that inserted into a database SQLite mode kept read-only): while the SQLite tools are on and <c>Shell police</c> is, a
/// <c>run_command</c> line, an <c>execute_code</c> script or a <c>process</c> write that reaches SQLite is refused before the gate is
/// asked, whatever the policy, so a database is reached only through the sqlite_ tools, whose layers, mode, kinds and allow pane
/// then hold. Reaching SQLite is, case ignored: the word <c>sqlite</c> anywhere (the sqlite3 CLI, Python's sqlite3,
/// System.Data.SQLite, Microsoft.Data.Sqlite, better-sqlite3, node:sqlite, PSSQLite); a file <c>sqlite.json</c> names, by its full
/// path or its file name; and a database file name (<see cref="DatabaseGuard.HasDatabaseName"/>) — in a line any token, in a
/// script only quoted or after a slash, so <c>self.db</c> is no file. A line's own script files (<see cref="ScriptFiles"/>) are read
/// as scripts: <c>write_file insert.py</c> then <c>python insert.py</c> is caught at the second call. A tripwire like
/// <see cref="ForbiddenStrings"/>, not a sandbox: text built in pieces gets past it; only the <c>ask</c> policy shows the user every
/// command. A package-manager command that only installs, removes, restores or lists packages is not judged (2026-10-10, the
/// user's call, <see cref="PackageCommands"/>): <c>dotnet add package Microsoft.Data.Sqlite</c> declares a dependency, reaches no
/// database. Pure but for <see cref="ScriptFiles"/>' reads.
/// </summary>
public static partial class SqlitePolice
{
    /// <summary>The word every SQLite driver's, module's and CLI's name holds.</summary>
    public const string Word = "sqlite";

    /// <summary>The script files a line may run that <see cref="ScriptFiles"/> reads: the interpreters' and shells' own.</summary>
    public static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".ps1", ".psm1", ".js", ".mjs", ".cjs", ".ts", ".sql", ".bat", ".cmd", ".sh", ".rb", ".pl", ".php", ".lua", ".cs", ".csx", ".r",
    };

    /// <summary>The largest script file <see cref="ScriptFiles"/> reads; a bigger one is passed over by it and refused by <see cref="ShellPoliceChecks"/> (<see cref="OversizedScript"/>).</summary>
    public const long MaxScriptBytes = 1_000_000;

    /// <summary>
    /// What in <paramref name="text"/> reaches SQLite — the word as written with the name around it (<c>sqlite3</c>,
    /// <c>Microsoft.Data.Sqlite</c>), a named database's file name, or a database file name — else null.
    /// <paramref name="script"/> reads it as a script (database file names only quoted or after a slash).
    /// </summary>
    public static string? Find(string text, bool script, DatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(guard);
        int at = text.IndexOf(Word, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            int start = at, end = at + Word.Length;
            while (start > 0 && IsNameChar(text[start - 1]))
            {
                start--;
            }

            while (end < text.Length && IsNameChar(text[end]))
            {
                end++;
            }

            return text[start..end];
        }

        foreach (string named in guard.NamedFiles)
        {
            if (Contains(text, named) || Contains(text, named.Replace('\\', '/')))
            {
                return Path.GetFileName(named);
            }
        }

        foreach (string name in guard.NamedFileNames)
        {
            if (ContainsName(text, name))
            {
                return name;
            }
        }

        var match = (script ? ScriptFileName() : LineFileName()).Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// The script files <paramref name="command"/> names — a token ending one of <see cref="ScriptExtensions"/> that is a file,
    /// read from <paramref name="workdir"/>, inside <paramref name="root"/>, no bigger than <see cref="MaxScriptBytes"/> — with
    /// their text, each once; one that cannot be read is passed over.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> ScriptFiles(string command, string workdir, string root)
    {
        var found = new List<(string Name, string Text)>();
        foreach (var (full, length) in ScriptPaths(command, workdir, root))
        {
            if (length > MaxScriptBytes)
            {
                continue;
            }

            try
            {
                found.Add((Path.GetFileName(full), File.ReadAllText(full)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be read: nothing to judge.
            }
        }

        return found;
    }

    /// <summary>
    /// The name of the first script file <paramref name="command"/> names (as <see cref="ScriptFiles"/> finds them) that is over
    /// <see cref="MaxScriptBytes"/>, else null (later on 2026-10-19, the third shell review: <see cref="ScriptFiles"/> passed one over unread,
    /// and every police read that as clean, so a script padded past the limit reached a database or changed a service).
    /// </summary>
    public static string? OversizedScript(string command, string workdir, string root) =>
        ScriptPaths(command, workdir, root).Where(file => file.Length > MaxScriptBytes).Select(file => Path.GetFileName(file.Full)).FirstOrDefault();

    /// <summary>
    /// The programs that run the file named after them (2026-10-22, review L4: a script was found by its extension alone, so
    /// <c>bash run</c> or <c>python job</c> ran a file no police read). Matched without case, a <c>.exe</c> off.
    /// </summary>
    public static readonly IReadOnlySet<string> Interpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "python", "python3", "py", "pythonw", "node", "deno", "bun", "bash", "sh", "zsh", "dash", "ksh", "ruby", "perl", "php", "lua",
        "pwsh", "powershell", "Rscript", "dotnet-script", "cscript", "wscript", "call", "source", ".",
    };

    /// <summary>The words of <paramref name="command"/> that name a script: by extension, or the first non-option word after an interpreter.</summary>
    private static IEnumerable<string> ScriptWords(string command)
    {
        bool afterInterpreter = false;
        foreach (Match token in Token().Matches(command))
        {
            string word = token.Value.Trim('"', '\'', '`', '(', ')', ';', ',', '&', '|');
            if (word.Length == 0)
            {
                continue;
            }

            if (afterInterpreter && !word.StartsWith('-') && !(word.StartsWith('/') && word.Length == 2))
            {
                afterInterpreter = false;
                yield return word;
                continue;
            }

            string program = Path.GetFileNameWithoutExtension(word.Replace('\\', '/').Split('/')[^1]);
            if (Interpreters.Contains(program) || Interpreters.Contains(word))
            {
                afterInterpreter = true;
                continue;
            }

            if (word is "&&" or "||" or ";" or "|")
            {
                afterInterpreter = false;
            }

            if (ScriptExtensions.Contains(Path.GetExtension(word)))
            {
                yield return word;
            }
        }
    }

    /// <summary>
    /// The scripts <paramref name="command"/> runs (after an interpreter, or as <c>./x</c>, <c>.\x</c>) that are not there to read (review L4:
    /// a script the same line writes, <c>echo … &gt; a.sh &amp;&amp; bash a.sh</c>, was judged before it existed). Inside the root only.
    /// </summary>
    public static IReadOnlyList<string> MissingScripts(string command, string workdir, string root)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workdir);
        ArgumentNullException.ThrowIfNull(root);
        var missing = new List<string>();
        bool afterInterpreter = false;
        foreach (Match token in Token().Matches(command))
        {
            string word = token.Value.Trim('"', '\'', '`', '(', ')', ';', ',', '&', '|');
            if (word.Length == 0)
            {
                continue;
            }

            bool runs = (afterInterpreter && !word.StartsWith('-') && !(word.StartsWith('/') && word.Length == 2))
                || word.StartsWith("./", StringComparison.Ordinal) || word.StartsWith(".\\", StringComparison.Ordinal);
            string program = Path.GetFileNameWithoutExtension(word.Replace('\\', '/').Split('/')[^1]);
            afterInterpreter = !runs && (Interpreters.Contains(program) || Interpreters.Contains(word));
            if (!runs || word.Contains('$') || word.Contains('%'))
            {
                continue;
            }

            try
            {
                string full = Path.GetFullPath(Path.Combine(workdir, word));
                if (WorkingDirectory.IsInside(Path.GetFullPath(root), full) && !File.Exists(full) && !Directory.Exists(full))
                {
                    missing.Add(Path.GetFileName(full));
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // No path: nothing to say.
            }
        }

        return missing;
    }

    /// <summary>The script files <paramref name="command"/> names that exist inside <paramref name="root"/>, each once, with their sizes.</summary>
    private static IEnumerable<(string Full, long Length)> ScriptPaths(string command, string workdir, string root)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workdir);
        ArgumentNullException.ThrowIfNull(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string word in ScriptWords(command))
        {

            (string Full, long Length)? file = null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(workdir, word));
                string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                bool inside = full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (inside && seen.Add(full) && File.Exists(full))
                {
                    file = (full, new FileInfo(full).Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A token that is no path: nothing to judge.
            }

            if (file is { } found)
            {
                yield return found;
            }
        }
    }

    /// <summary>
    /// The verdict on a <c>run_command</c> line: what in the line reaches SQLite, else what in one of its script files does
    /// (<c>File</c> naming it); null when neither does.
    /// </summary>
    public static (string Token, string? File)? Judge(string command, string workdir, string root, DatabaseGuard guard) =>
        Judge(command, new Lazy<IReadOnlyList<(string Name, string Text)>>(() => ScriptFiles(command, workdir, root)), guard);

    /// <summary>
    /// <see cref="Judge(string, string, string, DatabaseGuard)"/> over script files read at most once and only when the line itself
    /// passes (2026-10-12, the shell review: this police and <see cref="ServerDatabasePolice"/> each read them from disk, up to
    /// <see cref="MaxScriptBytes"/> a file, for one command; <see cref="ShellPoliceChecks"/> now hands both the same <paramref name="scripts"/>).
    /// </summary>
    public static (string Token, string? File)? Judge(string command, Lazy<IReadOnlyList<(string Name, string Text)>> scripts, DatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(guard);
        // The line less its package-manager segments (2026-10-10); its script files are read whole, as before.
        if (Find(PackageCommands.Strip(command), script: false, guard) is { } token)
        {
            return (token, null);
        }

        foreach (var (name, text) in scripts.Value)
        {
            if (Find(text, script: true, guard) is { } inScript)
            {
                return (inScript, name);
            }
        }

        return null;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or ':';

    private static bool Contains(string text, string needle) =>
        needle.Length > 0 && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="name"/> stands in <paramref name="text"/> as a file name: not inside a longer name.</summary>
    private static bool ContainsName(string text, string name)
    {
        int from = 0;
        while (name.Length > 0 && (from = text.IndexOf(name, from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int end = from + name.Length;
            bool before = from == 0 || !IsFileChar(text[from - 1]);
            bool after = end == text.Length || !IsFileChar(text[end]);
            if (before && after)
            {
                return true;
            }

            from = end;
        }

        return false;
    }

    private static bool IsFileChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-';

    /// <summary>In a line: any token ending a database extension (a journal's suffix allowed).</summary>
    [GeneratedRegex(@"(?<![\w.\-])(?<name>[\w.\-~]+\.(?:db|db3|sqlite|sqlite3)(?:-journal|-wal|-shm)?)(?![\w.\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineFileName();

    /// <summary>In a script: a database file name only where a file name stands — just inside a quote, or just after a slash.</summary>
    [GeneratedRegex(@"(?<=[""'`/\\])(?<name>[\w.\-~ ]*?\.(?:db|db3|sqlite|sqlite3)(?:-journal|-wal|-shm)?)(?=[""'`]|$|\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ScriptFileName();

    /// <summary>A line's tokens: quoted runs whole, else runs without blanks.</summary>
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
