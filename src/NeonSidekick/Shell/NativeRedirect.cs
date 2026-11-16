namespace NeonSidekick.Shell;

/// <summary>
/// Which native tool a <c>run_command</c> line stands in for (2026-09-26, the user's ask: the model kept running
/// <c>cat</c>, <c>dir</c>, <c>git status</c> or <c>curl</c> through the shell when a tool of its own did the job,
/// costing the user an approval and the tool its guarantees). The unit is <see cref="CommandPrefix"/>'s: the program,
/// with the verb for <c>git</c>, so only the git verbs a tool covers are here — <c>git push</c>, <c>git pull</c>,
/// <c>git clone</c> have none and pass. A line of more than one segment (a pipe, <c>&amp;&amp;</c>, <c>;</c>) is shell
/// work in its own right and never redirected: <c>Get-Content x | Select-Object -First 5</c> is not a <c>read_file</c>.
/// Pure; the table is pinned by <c>NativeRedirectTests</c>. The setting <c>Shell prefer native tools</c> and the
/// once-a-turn rule live in <see cref="Llm.Tools.RunCommandTool"/>.
/// </summary>
public static class NativeRedirect
{
    /// <summary>Prefix (as <see cref="CommandPrefix.Of"/> gives it: lower case, extension off) → the tool that does its job.</summary>
    public static readonly IReadOnlyDictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        void Map(string tool, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                table.Add(prefix, tool);
            }
        }

        Map(Llm.Tools.ReadFileTool.ToolName, "cat", "type", "get-content", "gc", "more", "head", "tail");
        Map(Llm.Tools.SearchFilesTool.ToolName, "dir", "ls", "get-childitem", "gci", "tree", "grep", "findstr", "select-string", "sls", "rg");
        Map(Llm.Tools.CopyTool.ToolName, "copy", "cp", "copy-item", "cpi");
        Map(Llm.Tools.MoveTool.ToolName, "move", "mv", "move-item");
        Map(Llm.Tools.DeleteTool.ToolName, "del", "erase", "rm", "rmdir", "rd", "remove-item");
        Map(Llm.Tools.CreateDirectoryTool.ToolName, "mkdir", "md");
        Map(Llm.Tools.ZipTool.ToolName, "compress-archive");
        Map(Llm.Tools.UnzipTool.ToolName, "expand-archive");
        Map(Llm.Tools.GitStatusTool.ToolName, "git status");
        Map(Llm.Tools.GitLogTool.ToolName, "git log");
        Map(Llm.Tools.GitDiffTool.ToolName, "git diff");
        Map(Llm.Tools.GitShowTool.ToolName, "git show");
        Map(Llm.Tools.GitBlameTool.ToolName, "git blame");
        Map(Llm.Tools.GitStageTool.ToolName, "git add");
        Map(Llm.Tools.GitCommitTool.ToolName, "git commit");
        Map(Llm.Tools.GitBranchTool.ToolName, "git branch");
        Map(Llm.Tools.GitStashTool.ToolName, "git stash");
        Map(Llm.Tools.GitRevertTool.ToolName, "git revert");
        Map(Llm.Tools.GitTagTool.ToolName, "git tag");
        Map(Llm.Tools.WebFetchTool.ToolName, "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm");
        Map(Llm.Tools.SqlQueryTool.ToolName, "sqlcmd", "invoke-sqlcmd");
        Map(Llm.Tools.OracleQueryTool.ToolName, "sqlplus");
        Map(Llm.Tools.MySqlQueryTool.ToolName, "mysql", "mariadb");
        Map(Llm.Tools.SqliteQueryTool.ToolName, "sqlite3");   // 2026-10-04
        Map(Llm.Tools.DuckDbQueryTool.ToolName, "duckdb");   // 2026-10-25
        Map(Llm.Tools.PostgresQueryTool.ToolName, "psql");   // 2026-10-04   // 2026-09-30; SQLcl's bare "sql" is too common a word to take
        Map(Llm.Tools.UncSharesTool.ToolName, "net use", "net share", "net view", "get-smbshare", "get-smbmapping", "mount_smbfs", "smbutil");   // 2026-10-03: "my UNC shares" went to net use; a Mac's two since 2026-10-11
        return table;
    }

    /// <summary>
    /// The native tool <paramref name="command"/> stands in for, with its prefix, or null: a line of one segment whose
    /// prefix is in <see cref="Table"/> and whose tool is among <paramref name="offered"/> (the names the turn offers —
    /// a tool switched off, or a group not offered, leaves the shell its line).
    /// </summary>
    public static (string Prefix, string Tool)? For(string command, IReadOnlySet<string> offered) => For(command, offered, ShellKind.PowerShell, macAware: false);

    private static (string Prefix, string Tool)? For(string command, IReadOnlySet<string> offered, ShellKind kind, bool macAware)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(offered);
        var segments = CommandPrefix.Segments(command, kind);
        if (segments.Count != 1)
        {
            return null;
        }

        // The verb straight after the program alone (2026-10-12): git -C sub status runs in sub, where git_status never looks.
        string prefix = CommandPrefix.Of(segments[0], skipOptions: false, kind);
        if (PastTheVerb(prefix, segments[0], kind) is { } form)
        {
            // A form the verb's tool can't do (2026-11-15, the tool-arguments review's #32): to gitlib_delete when the turn offers it, else
            // the shell keeps the line; a form with no tool at all (Delete is null) stays the shell's.
            return form.Delete && offered.Contains(Llm.Tools.GitDeleteTool.ToolName) ? (form.Prefix, Llm.Tools.GitDeleteTool.ToolName) : null;
        }

        return Table.TryGetValue(prefix, out string? tool) && offered.Contains(tool)
            && !(macAware && OperatingSystem.IsMacOS() && DescribesCommand(prefix, kind))
            ? (prefix, tool)
            : null;
    }

    /// <summary>
    /// <see cref="For(string, IReadOnlySet{string})"/> for a line run in <paramref name="kind"/> (2026-10-06, the macOS build): on a Mac
    /// <c>type</c> goes to <c>read_file</c> only in PowerShell (or cmd), where it prints a file; in zsh and bash it describes a
    /// command (<c>type python3</c>), which no native tool does, so the shell keeps it. Windows is unchanged — every shell's
    /// <c>type</c> is sent back there, Git Bash's too, as before.
    /// </summary>
    public static (string Prefix, string Tool)? For(string command, IReadOnlySet<string> offered, ShellKind kind) =>
        For(command, offered, kind, macAware: true);

    /// <summary>
    /// The forms of a git verb its own tool can't do (2026-11-15, the tool-arguments review's #32: <see cref="CommandPrefix.Of"/> stops at
    /// <c>git tag</c>, so <c>git tag -d v1</c> went to <c>gitlib_tag</c>, which made the model loop). Read past the verb: a delete flag
    /// anywhere after it (<c>git branch -v -d x</c>), or the first word after <c>stash</c> that isn't an option. <c>git tag -d</c>,
    /// <c>git branch -d/-D/--delete</c> and <c>git stash drop</c> are deletes (<c>gitlib_delete</c>; the branch and stash forms are a widening
    /// found in pass 39); <c>git revert --abort/--continue/--quit/--skip</c> and <c>git stash clear</c> have no tool. Null for any other line.
    /// </summary>
    private static (string Prefix, bool Delete)? PastTheVerb(string prefix, string segment, ShellKind kind)
    {
        if (prefix is not ("git tag" or "git branch" or "git stash" or "git revert"))
        {
            return null;
        }

        string verb = prefix[4..];
        var tokens = CommandPrefix.Tokens(segment, kind, out _);
        int at = tokens.FindIndex(t => t.Equals(verb, StringComparison.OrdinalIgnoreCase));
        var rest = tokens.Skip(at + 1).ToList();
        bool Has(params string[] flags) => rest.Any(t => flags.Contains(t, StringComparer.Ordinal));
        switch (prefix)
        {
            case "git tag" when Has("-d", "--delete"):
                return ("git tag -d", true);
            case "git branch" when Has("-d", "-D", "--delete"):
                return ("git branch -d", true);
            case "git revert" when Has("--abort", "--continue", "--quit", "--skip"):
                return ("git revert --abort", false);
            case "git stash":
                string? sub = rest.FirstOrDefault(t => !t.StartsWith('-'))?.ToLowerInvariant();
                return sub switch
                {
                    "drop" => ("git stash drop", true),
                    "clear" => ("git stash clear", false),
                    _ => null,
                };
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="prefix"/> in <paramref name="kind"/> is the Unix shells' <c>type</c>, a command's description. Pure.</summary>
    public static bool DescribesCommand(string prefix, ShellKind kind) =>
        string.Equals(prefix, "type", StringComparison.Ordinal) && kind is not (ShellKind.PowerShell or ShellKind.Cmd);
}
