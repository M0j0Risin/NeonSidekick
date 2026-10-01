using NeonSidekick.Llm.Tools;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary><see cref="NativeRedirect"/> (2026-09-26): which native tool a <c>run_command</c> line stands in for — one segment, a prefix in the table, the tool offered.</summary>
public sealed class NativeRedirectTests
{
    private static readonly IReadOnlySet<string> Everything = new HashSet<string>(NativeRedirect.Table.Values, StringComparer.Ordinal);

    [Theory]
    [InlineData("cat README.md", "cat", "read_file")]
    [InlineData("type notes.txt", "type", "read_file")]
    [InlineData("Get-Content -Path src\\a.cs -TotalCount 20", "get-content", "read_file")]
    [InlineData("CAT.EXE x", "cat", "read_file")]
    [InlineData("dir /s /b *.cs", "dir", "search_files")]
    [InlineData("ls -la", "ls", "search_files")]
    [InlineData("Get-ChildItem -Recurse", "get-childitem", "search_files")]
    [InlineData("grep -rn TODO src", "grep", "search_files")]
    [InlineData("Select-String -Pattern x -Path *.cs", "select-string", "search_files")]
    [InlineData("copy a.txt b.txt", "copy", "copy")]
    [InlineData("mv a b", "mv", "move")]
    [InlineData("Remove-Item old.log", "remove-item", "delete")]
    [InlineData("mkdir out", "mkdir", "create_directory")]
    [InlineData("Compress-Archive -Path x -DestinationPath x.zip", "compress-archive", "zip")]
    [InlineData("Expand-Archive x.zip", "expand-archive", "unzip")]
    [InlineData("git status --short", "git status", "git_status")]
    [InlineData("git log -5 --oneline", "git log", "git_log")]
    [InlineData("git diff HEAD~1", "git diff", "git_diff")]
    [InlineData("git add .", "git add", "git_stage")]
    [InlineData("git commit -m \"fix\"", "git commit", "git_commit")]
    [InlineData("curl https://example.com", "curl", "web_fetch")]
    [InlineData("Invoke-WebRequest https://example.com", "invoke-webrequest", "web_fetch")]
    [InlineData("sqlcmd -S . -Q \"select 1\"", "sqlcmd", "sql_query")]
    [InlineData("sqlplus -s hr/x@db @q.sql", "sqlplus", "oracle_query")]
    [InlineData("mysql -u shop_reader -p shop", "mysql", "mysql_query")]
    [InlineData("& git status", "git status", "git_status")]   // PowerShell's call operator leaves one segment
    public void AMappedLine_NamesItsTool(string command, string prefix, string tool)
    {
        Assert.Equal((prefix, tool), NativeRedirect.For(command, Everything));
    }

    [Theory]
    [InlineData("git push")]              // no tool for the verb
    [InlineData("git -C sub status")]     // an option before the verb reads as bare git
    [InlineData("dotnet build")]
    [InlineData("python script.py")]
    [InlineData("cat a.txt | sort")]      // a pipe is shell work
    [InlineData("mkdir out && cd out")]
    [InlineData("type a.txt; type b.txt")]
    [InlineData("")]
    public void AnythingElse_IsTheShells(string command)
    {
        Assert.Null(NativeRedirect.For(command, Everything));
    }

    [Fact]
    public void ATool_NotOffered_LeavesTheShellItsLine()
    {
        var files = new HashSet<string>(StringComparer.Ordinal) { ReadFileTool.ToolName };
        Assert.Equal(("cat", ReadFileTool.ToolName), NativeRedirect.For("cat x", files));
        Assert.Null(NativeRedirect.For("git status", files));
        Assert.Null(NativeRedirect.For("rm x", files));   // delete switched off, as in a fresh profile
        Assert.Null(NativeRedirect.For("cat x", new HashSet<string>()));
    }

    [Fact]
    public void EveryToolInTheTable_IsARealToolName()
    {
        string[] known =
        [
            ReadFileTool.ToolName, SearchFilesTool.ToolName, CopyTool.ToolName, MoveTool.ToolName, DeleteTool.ToolName, CreateDirectoryTool.ToolName,
            ZipTool.ToolName, UnzipTool.ToolName, GitStatusTool.ToolName, GitLogTool.ToolName, GitDiffTool.ToolName, GitShowTool.ToolName, GitBlameTool.ToolName,
            GitStageTool.ToolName, GitCommitTool.ToolName, GitBranchTool.ToolName, GitStashTool.ToolName, WebFetchTool.ToolName, SqlQueryTool.ToolName, OracleQueryTool.ToolName, MySqlQueryTool.ToolName,
        ];
        Assert.All(NativeRedirect.Table.Values, tool => Assert.Contains(tool, known));
        // Every key reads as CommandPrefix would give it, so a lookup can hit.
        Assert.All(NativeRedirect.Table.Keys, prefix => Assert.Equal(prefix, CommandPrefix.Of(prefix)));
    }
}
