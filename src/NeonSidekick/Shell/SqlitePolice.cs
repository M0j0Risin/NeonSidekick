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
/// command. Pure but for <see cref="ScriptFiles"/>' reads.
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

    /// <summary>The largest script file <see cref="ScriptFiles"/> reads; a bigger one is passed over.</summary>
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
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workdir);
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<(string Name, string Text)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match token in Token().Matches(command))
        {
            string word = token.Value.Trim('"', '\'', '`', '(', ')', ';', ',', '&', '|');
            if (word.Length == 0 || !ScriptExtensions.Contains(Path.GetExtension(word)))
            {
                continue;
            }

            try
            {
                string full = Path.GetFullPath(Path.Combine(workdir, word));
                string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                bool inside = full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!inside || !seen.Add(full) || !File.Exists(full) || new FileInfo(full).Length > MaxScriptBytes)
                {
                    continue;
                }

                found.Add((Path.GetFileName(full), File.ReadAllText(full)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A token that is no path, or a file that cannot be read: nothing to judge.
            }
        }

        return found;
    }

    /// <summary>
    /// The verdict on a <c>run_command</c> line: what in the line reaches SQLite, else what in one of its script files does
    /// (<c>File</c> naming it); null when neither does.
    /// </summary>
    public static (string Token, string? File)? Judge(string command, string workdir, string root, DatabaseGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        if (Find(command, script: false, guard) is { } token)
        {
            return (token, null);
        }

        foreach (var (name, text) in ScriptFiles(command, workdir, root))
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
