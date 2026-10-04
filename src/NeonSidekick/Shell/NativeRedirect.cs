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
        Map(Llm.Tools.WebFetchTool.ToolName, "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm");
        Map(Llm.Tools.SqlQueryTool.ToolName, "sqlcmd", "invoke-sqlcmd");
        Map(Llm.Tools.OracleQueryTool.ToolName, "sqlplus");
        Map(Llm.Tools.MySqlQueryTool.ToolName, "mysql", "mariadb");
        Map(Llm.Tools.SqliteQueryTool.ToolName, "sqlite3");   // 2026-10-04
        Map(Llm.Tools.PostgresQueryTool.ToolName, "psql");   // 2026-10-04   // 2026-09-30; SQLcl's bare "sql" is too common a word to take
        Map(Llm.Tools.UncSharesTool.ToolName, "net use", "net share", "net view", "get-smbshare", "get-smbmapping");   // 2026-10-03: "my UNC shares" went to net use
        return table;
    }

    /// <summary>
    /// The native tool <paramref name="command"/> stands in for, with its prefix, or null: a line of one segment whose
    /// prefix is in <see cref="Table"/> and whose tool is among <paramref name="offered"/> (the names the turn offers —
    /// a tool switched off, or a group not offered, leaves the shell its line).
    /// </summary>
    public static (string Prefix, string Tool)? For(string command, IReadOnlySet<string> offered)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(offered);
        var segments = CommandPrefix.Segments(command);
        if (segments.Count != 1)
        {
            return null;
        }

        string prefix = CommandPrefix.Of(segments[0]);
        return Table.TryGetValue(prefix, out string? tool) && offered.Contains(tool) ? (prefix, tool) : null;
    }
}
