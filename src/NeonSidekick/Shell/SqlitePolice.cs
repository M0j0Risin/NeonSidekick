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

    /// <summary>
    /// The other words that reach SQLite (2026-10-24, the database review's G15): Python's <c>apsw</c>, a SQLite wrapper whose name holds no
    /// <c>sqlite</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> OtherWords = ["apsw"];

    /// <summary>
    /// The script files a line may run that <see cref="ScriptFiles"/> reads: the interpreters' and shells' own. Since 2026-10-24 (the database
    /// review's Q2) the Windows Script Host's, more shells', and the languages a launcher runs from source too.
    /// </summary>
    public static readonly IReadOnlySet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyw", ".ps1", ".psm1", ".js", ".mjs", ".cjs", ".ts", ".sql", ".bat", ".cmd", ".sh", ".rb", ".pl", ".php", ".lua", ".cs", ".csx", ".r",
        ".vbs", ".vbe", ".wsf", ".jse", ".psd1", ".bash", ".zsh", ".ksh", ".fish", ".tcl", ".awk", ".java", ".go", ".swift", ".jl", ".kts", ".groovy",
        ".exs", ".mts", ".cts", ".tsx", ".jsx",
    };

    /// <summary>The extensions cmd tries for a command named without one, in its lookup's order (Q2: <c>run</c> ran <c>run.bat</c> unread).</summary>
    public static readonly IReadOnlyList<string> CommandExtensions = [".bat", ".cmd"];

    /// <summary>How many script files <see cref="ScriptFiles"/> reads for one line, the ones they name among them (Q2).</summary>
    public const int MaxScripts = 32;

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
        foreach (string other in OtherWords)
        {
            int found = text.IndexOf(other, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (at < 0 || found < at))
            {
                at = found;
            }
        }

        if (at >= 0)
        {
            int start = at, end = at + 1;
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

        var match = (script ? ScriptFileName : LineFileName).Match(text);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>
    /// A line as the database police judge it (2026-10-24, the database review's Q5): without its package-manager segments, its lookups
    /// (<see cref="AdminPolice.WithoutLookups"/>: a <c>grep sqlite</c> reaches nothing) and a <c>git commit</c>'s message, the rest as written.
    /// </summary>
    public static string Judged(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return CommitMessage().Replace(AdminPolice.WithoutLookups(PackageCommands.Strip(command)), "");
    }

    /// <summary>
    /// The script files <paramref name="command"/> names — a token ending one of <see cref="ScriptExtensions"/> that is a file,
    /// read from <paramref name="workdir"/>, inside <paramref name="root"/>, no bigger than <see cref="MaxScriptBytes"/> — with
    /// their text, each once; one that cannot be read is passed over.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> ScriptFiles(string command, string workdir, string root)
    {
        var found = new List<(string Name, string Text)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Full, long Length)>(ScriptPaths(command, workdir, root));
        while (queue.Count > 0 && found.Count < MaxScripts)
        {
            var (full, length) = queue.Dequeue();
            if (length > MaxScriptBytes || !seen.Add(full))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;   // a file that cannot be read: nothing to judge
            }

            found.Add((Path.GetFileName(full), text));

            // The scripts a script names, read too (2026-10-24, the database review's Q2: subprocess.run(["python", "inner.py"]) ran a file no
            // police read), from its own folder or the line's, inside the root, up to MaxScripts in all.
            foreach (Match named in NamedScript().Matches(text))
            {
                foreach (string from in new[] { Path.GetDirectoryName(full) ?? workdir, workdir })
                {
                    if (ScriptPath(named.Groups["name"].Value, from, root) is { } inner)
                    {
                        queue.Enqueue(inner);
                        break;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>The file <paramref name="word"/> names from <paramref name="from"/> when it is one inside <paramref name="root"/>, with its size; else null.</summary>
    private static (string Full, long Length)? ScriptPath(string word, string from, string root)
    {
        try
        {
            string full = Path.GetFullPath(Path.Combine(from, word));
            string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)
                ? (full, new FileInfo(full).Length)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;   // a token that is no path
        }
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
        // 2026-10-24 (review Q2): more shells and launchers that run a file named after them.
        "fish", "tclsh", "wish", "awk", "gawk", "julia", "ts-node", "tsx", "csi", "scriptcs", "osascript", "groovy", "kotlin", "elixir", "swift",
    };

    /// <summary>The interpreters' options whose next word is code or a module, never a script file (<c>-c</c>, <c>-e</c>, <c>-m</c>, <c>-Command</c>).</summary>
    private static readonly IReadOnlySet<string> InlineOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-c", "-e", "-p", "-m", "-r", "--eval", "--print", "-Command", "-EncodedCommand", "-ec", "-enc", "/c", "/k", "--command",
    };

    /// <summary>The interpreters' options that take a value before the script (2026-10-24, review Q2: <c>python -X utf8 job</c> ran <c>job</c> unread).</summary>
    private static readonly IReadOnlySet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-X", "-W", "--require", "--import", "-ExecutionPolicy", "-ep", "-WindowStyle", "-ConfigurationName", "--allow-read", "--loader",
    };

    /// <summary>
    /// The words of <paramref name="command"/> that name a script: by extension; the first word after an interpreter and its options (a value
    /// option's value stepped over, an inline-code option ending the search); and since 2026-10-24 (review Q2) a command named without an
    /// extension at a statement's start, which cmd looks up as a <c>.bat</c> or <c>.cmd</c> beside it.
    /// </summary>
    private static IEnumerable<string> ScriptWords(string command)
    {
        bool afterInterpreter = false;
        bool skipValue = false;
        bool atStart = true;
        foreach (Match token in Token().Matches(command))
        {
            string raw = token.Value;
            string word = raw.Trim('"', '\'', '`', '(', ')', ';', ',', '&', '|', '[', ']', '{', '}');
            bool breaks = raw is "&&" or "||" or ";" or "|" or "&" || raw.EndsWith(';') || raw.EndsWith('|') || raw.EndsWith('&');
            if (word.Length == 0)
            {
                atStart |= breaks;
                afterInterpreter &= !breaks;
                continue;
            }

            if (skipValue)
            {
                skipValue = false;
                if (ScriptExtensions.Contains(Path.GetExtension(word)))
                {
                    yield return word;   // a value that is itself a script (node --require ./x.js): read it (2026-10-24, the review of these fixes)
                }

                continue;
            }

            if (afterInterpreter && (word.StartsWith('-') || (word.StartsWith('/') && word.Length == 2)))
            {
                // An interpreter option: an inline command or a module ends the search (2026-10-22, the shell-review fixes: `bash -c echo hi`
                // read `echo` as a script); a value option's value is stepped over; any other flag leaves the script still to come.
                if (InlineOptions.Contains(word))
                {
                    afterInterpreter = false;
                }
                else if (ValueOptions.Contains(word))
                {
                    skipValue = true;
                }

                continue;
            }

            if (afterInterpreter)
            {
                afterInterpreter = false;
                atStart = breaks;
                yield return word;
                continue;
            }

            string program = Path.GetFileNameWithoutExtension(word.Replace('\\', '/').Split('/')[^1]);
            if (Interpreters.Contains(program) || Interpreters.Contains(word) || program.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                afterInterpreter = !program.Equals("cmd", StringComparison.OrdinalIgnoreCase);
                atStart = program.Equals("cmd", StringComparison.OrdinalIgnoreCase);   // cmd /c run: what follows is a command
                continue;
            }

            if (word.StartsWith('-') || (word.StartsWith('/') && word.Length == 2))
            {
                continue;   // a program's flag (cmd /c): what follows keeps its place
            }

            if (ScriptExtensions.Contains(Path.GetExtension(word)))
            {
                yield return word;
            }
            else if (atStart && Path.GetExtension(word).Length == 0 && !word.StartsWith('-') && !word.StartsWith('/'))
            {
                foreach (string extension in CommandExtensions)
                {
                    yield return word + extension;
                }

                if (word.StartsWith("./", StringComparison.Ordinal) || word.StartsWith(".\\", StringComparison.Ordinal))
                {
                    yield return word;   // an extensionless script run by its path (a shebang's)
                }
            }

            atStart = breaks;
            if (breaks)
            {
                afterInterpreter = false;
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
        // The line less its package-manager segments (2026-10-10), lookups and commit message (2026-10-24, Q5); its scripts are read whole, as before.
        if (Find(Judged(command), script: false, guard) is { } token)
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

    /// <summary>
    /// <see cref="DatabaseGuard.Extensions"/> as a regex alternation (2026-10-24, the database review's A9: the police's own list lacked
    /// <c>.s3db</c>, <c>.sl3</c>, <c>.sl2</c> and <c>.sqlitedb</c>).
    /// </summary>
    private static readonly string Extensions = string.Join("|", DatabaseGuard.Extensions.Select(e => Regex.Escape(e[1..])));

    /// <summary>
    /// The characters a file name may hold before its extension in a line: since 2026-10-24 the punctuation Windows allows (<c>a+b.db</c>,
    /// <c>x(1).db</c>, review Q1) and the wildcards a shell expands (<c>*.db</c>, <c>?</c>, G15).
    /// </summary>
    private const string LineNameChars = @"[\w.\-~+!@#$%,()\[\]{}*?]";

    /// <summary>What may not stand just before or after a line's file name: a name's own characters, not the brackets around a call (<c>open(x.db)</c>).</summary>
    private const string LineBoundary = @"[\w.\-~+!@#$%*?]";

    /// <summary>
    /// In a line: any token ending a database extension, a journal's suffix allowed — and after it the trailing dots and spaces Windows drops
    /// and an alternate data stream's <c>:name</c> (Q1: <c>app.db.</c> and <c>app.db::$DATA</c> open <c>app.db</c>).
    /// </summary>
    private static readonly Regex LineFileName = new(
        @"(?<!" + LineBoundary + @")(?<name>" + LineNameChars + @"+\.(?:" + Extensions + @")(?:-journal|-wal|-shm)?)\.*(?::[^\s""'`]*)?(?!" + LineBoundary + ")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>In a script: a database file name only where a file name stands — just inside a quote, or just after a slash.</summary>
    private static readonly Regex ScriptFileName = new(
        @"(?<=[""'`/\\])(?<name>[\w.\-~ +!@#$%,()\[\]{}*?]*?\.(?:" + Extensions + @")(?:-journal|-wal|-shm)?)[. ]*(?::[^""'`\s]*)?(?=[""'`]|$|\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>A script file a script names (Q2): a word ending one of the script extensions, quoted or standing alone.</summary>
    [GeneratedRegex(@"(?<![\w.\-/\\])(?<name>[\w.\-/\\]+\.(?:py|pyw|ps1|psm1|js|mjs|cjs|ts|sql|bat|cmd|sh|rb|pl|php|lua|vbs|wsf|bash|zsh|r))(?![\w.\-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedScript();

    /// <summary>
    /// A <c>git commit</c>'s message (Q5): a quoted <c>-m</c>/<c>--message</c> value, which the police leaves out — only one that can run nothing
    /// (2026-10-24, the review of these fixes: a double-quoted <c>"$(mysql …)"</c> is a command substitution, and cmd reads <c>&amp;</c> inside
    /// single quotes): no <c>$</c>, backtick, <c>%</c>, <c>^</c>, <c>&amp;</c>, <c>|</c>, <c>;</c>, <c>&lt;</c> or <c>&gt;</c> in it.
    /// </summary>
    [GeneratedRegex(@"(?<=\bgit\s+commit\b[^\r\n;&|]*?\s(?:-m|--message)(?:\s+|=))(?:""[^""$`%^&|;<>\r\n]*""|'[^'$`%^&|;<>\r\n]*')(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitMessage();

    /// <summary>A line's tokens: quoted runs whole, else runs without blanks.</summary>
    [GeneratedRegex(@"""[^""]*""|'[^']*'|\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
