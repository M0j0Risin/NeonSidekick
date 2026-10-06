using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The eleven git tools over a temp sandbox holding a real repository: the pinned names, schemas and descriptions, the argument reading, every action's sentence and every refusal.</summary>
public sealed class GitToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly Files.WorkingDirectory _files;
    private readonly GitAccess _git;
    private readonly IReadOnlyList<AIFunction> _tools;

    public GitToolsTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new Files.WorkingDirectory(() => _root, _time);
        _git = new GitAccess(_files, _time);
        _tools = App.ChatScreen.GitTools(_git, () => _settings);
    }

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private T Tool<T>() where T : AIFunction => _tools.OfType<T>().Single();

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> Invoke(AIFunction tool, params (string Name, object? Value)[] pairs) => (string)(await tool.InvokeAsync(Args(pairs)))!;

    private string Repo() => GitAccessTests.Init(_root);

    private string Commit(string relative, string content, string message) => GitAccessTests.CommitFile(_root, relative, content, message);

    private void Write(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ---- the shape ----

    [Fact]
    public void Names_Schemas_AndDescriptions_ArePinned()
    {
        Assert.Equal(GitToolNames.All, _tools.Select(t => t.Name));
        Assert.Equal(["gitlib_status", "gitlib_log", "gitlib_show", "gitlib_diff", "gitlib_blame", "gitlib_branch", "gitlib_stage", "gitlib_commit", "gitlib_stash", "gitlib_discard", "gitlib_delete"], GitToolNames.All);
        Assert.All(_tools, t => Assert.Equal("object", t.JsonSchema.GetProperty("type").GetString()));
        Assert.All(_tools, t => Assert.True(t.JsonSchema.GetProperty("properties").TryGetProperty("path", out _), t.Name + " takes path"));

        static string[] Props(AIFunction t) => t.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        static string[] Required(AIFunction t) => t.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        static string[] Enum(AIFunction t, string property) => t.JsonSchema.GetProperty("properties").GetProperty(property).GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(["path"], Props(Tool<GitStatusTool>()));
        Assert.Empty(Required(Tool<GitStatusTool>()));
        Assert.Equal(["path", "ref", "max_commits"], Props(Tool<GitLogTool>()));
        Assert.Equal(["ref", "path"], Props(Tool<GitShowTool>()));
        Assert.Equal(["ref"], Required(Tool<GitShowTool>()));
        Assert.Equal(["path", "ref", "from", "to", "staged", "max_lines"], Props(Tool<GitDiffTool>()));
        Assert.Equal(["path", "from_line", "to_line", "ref"], Props(Tool<GitBlameTool>()));
        Assert.Equal(["path"], Required(Tool<GitBlameTool>()));
        Assert.Equal(["action", "name", "new_name", "start_point", "switch_to", "path"], Props(Tool<GitBranchTool>()));
        Assert.Equal(["list", "create", "switch", "rename"], Enum(Tool<GitBranchTool>(), "action"));
        Assert.Equal(["action", "paths", "path"], Props(Tool<GitStageTool>()));
        Assert.Equal(["action", "paths"], Required(Tool<GitStageTool>()));
        Assert.Equal(["stage", "unstage"], Enum(Tool<GitStageTool>(), "action"));
        Assert.Equal(["message", "amend", "allow_empty", "path"], Props(Tool<GitCommitTool>()));
        Assert.Equal(["message"], Required(Tool<GitCommitTool>()));
        Assert.Equal(["action", "message", "index", "include_untracked", "path"], Props(Tool<GitStashTool>()));
        Assert.Equal(["push", "pop", "apply", "list"], Enum(Tool<GitStashTool>(), "action"));
        Assert.Equal(["paths", "ref", "path"], Props(Tool<GitDiscardTool>()));
        Assert.Empty(Required(Tool<GitDiscardTool>()));
        Assert.Equal(["kind", "name", "index", "path"], Props(Tool<GitDeleteTool>()));
        Assert.Equal(["branch", "tag", "stash"], Enum(Tool<GitDeleteTool>(), "kind"));
        Assert.Equal("integer", Tool<GitLogTool>().JsonSchema.GetProperty("properties").GetProperty("max_commits").GetProperty("type").GetString());
        Assert.Equal("array", Tool<GitStageTool>().JsonSchema.GetProperty("properties").GetProperty("paths").GetProperty("type").GetString());
        Assert.Contains("at most 200 lines in one call", Tool<GitBlameTool>().JsonSchema.GetProperty("properties").GetProperty("to_line").GetProperty("description").GetString());
        Assert.Contains("at most 100 paths", Tool<GitStageTool>().JsonSchema.GetProperty("properties").GetProperty("paths").GetProperty("description").GetString());

        Assert.Equal(
            "Shows the state of the git repository under the working directory: the branch, how far ahead or behind its upstream it is, " +
            "and every staged, modified, untracked or conflicted path (narrowed to path when given). Call it before staging or committing.",
            Tool<GitStatusTool>().Description);
        Assert.Equal(
            "Lists the git history: the commits reachable from ref (HEAD by default), newest first, one line each with the short sha, the date, the author and the subject. " +
            "With a file or folder as path, only the commits that changed it.",
            Tool<GitLogTool>().Description);
        Assert.Equal(
            "Shows one commit: who made it, when, its message and the files it changed with their line counts. " +
            "With a file as path, the file's text as it is at that commit (read_file shows the working copy); with a folder, its entries there.",
            Tool<GitShowTool>().Description);
        Assert.Equal(
            "Shows changes as a unified diff with the files and their line counts first: with nothing but path, the unstaged changes in the working tree; " +
            "staged: true, what is staged; ref, one commit against its parent; from and to, everything between two commits. " +
            "Untracked files are not in it (gitlib_status lists them); a long patch is cut at max_lines — narrow it with path.",
            Tool<GitDiffTool>().Description);
        Assert.Equal(
            "Shows who last changed each line of a file and in which commit: one row per line with the short sha, the date, the author, the line number and the text; " +
            "a window of lines at a time (from_line, to_line), at HEAD unless ref says otherwise.",
            Tool<GitBlameTool>().Description);
        Assert.Equal(
            "Lists, creates, switches to or renames git branches. A switch never overwrites local changes (commit or stash them first); " +
            "deleting a branch is gitlib_delete's job.",
            Tool<GitBranchTool>().Description);
        Assert.Equal(
            "Stages or unstages changes for the next commit: the paths named, or \".\" for everything changed under path. " +
            "Stage only what the user asked to commit; gitlib_status shows what is staged.",
            Tool<GitStageTool>().Description);
        Assert.Equal(
            "Commits what is staged with the message given, signed with the user's git identity (user.name / user.email from git config). " +
            "Stage with gitlib_stage first; commit only what the user asked for, with their message or a short imperative one.",
            Tool<GitCommitTool>().Description);
        Assert.Equal(
            "Puts the working tree's changes aside and brings them back: push saves them as a stash and cleans the tree, pop or apply restores stash@{index}, list shows them. " +
            "Dropping a stash is gitlib_delete's job.",
            Tool<GitStashTool>().Description);
        Assert.Equal(
            "Throws uncommitted changes away for good: the paths named go back to how they are at ref (HEAD by default), index and working tree alike; " +
            "with no paths the whole tree is reset hard to ref (untracked files are left alone). Nothing brings the changes back — do it only when the user asked for exactly that.",
            Tool<GitDiscardTool>().Description);
        Assert.Equal(
            "Removes a local branch (never the one checked out), a tag, or a stash by its index. " +
            "A branch's unmerged commits and a dropped stash are gone from every listing — do it only when the user asked for exactly that.",
            Tool<GitDeleteTool>().Description);
        // gitlib_delete is the fresh profile's opt-in (gitlib_discard on out of the box since 2026-09-23); the rule names neither.
        Assert.Equal(["gitlib_delete", "unzip", "zip", "unc_delete", "docker_remove", "docker_prune"], new AppSettingsData().ToolsDisabled);   // delete on out of the box since later on 2026-09-21; unc_delete off since 2026-09-30; docker_remove and docker_prune since 2026-10-02
        Assert.All(_tools, t => Assert.Contains(t.Name, App.ChatScreen.QuietTools));
    }

    // ---- status / log / show ----

    [WindowsFact]
    public async Task Status_SaysNoRepository_ThenTheState_AsOneHeaderAndSections()
    {
        Assert.Equal($"Error: '{_root}' is not inside a git repository; /cwd into one, or ask the user to git init it", await Invoke(Tool<GitStatusTool>()));

        Repo();
        Write("a.txt", "one\n");
        Assert.Equal("On branch main (no commits yet): 1 untracked\nuntracked:\n?  a.txt\n" + GitText.Legend, await Invoke(Tool<GitStatusTool>()));
        Commit("a.txt", "one\n", "first");
        Assert.Equal("On branch main: clean", await Invoke(Tool<GitStatusTool>()));
        Write("a.txt", "two\n");
        Write("b.txt", "b\n");
        Assert.Equal("On branch main: 1 modified, 1 untracked\nunstaged:\nM  a.txt\nuntracked:\n?  b.txt\n" + GitText.Legend, await Invoke(Tool<GitStatusTool>()));
        Assert.Equal("Error: '..\\out' is outside the working directory", await Invoke(Tool<GitStatusTool>(), ("path", "..\\out")));
        Assert.True(GitText.Note(await Invoke(Tool<GitStatusTool>())).Length <= 200);
    }

    /// <summary>The Unix twin of <see cref="Status_SaysNoRepository_ThenTheState_AsOneHeaderAndSections"/> (2026-10-06, the macOS build): its paths with <c>/</c>.</summary>
    [UnixFact]
    public async Task Status_SaysNoRepository_ThenTheState_AsOneHeaderAndSections_Unix()
    {
        Assert.Equal($"Error: '{_root}' is not inside a git repository; /cwd into one, or ask the user to git init it", await Invoke(Tool<GitStatusTool>()));

        Repo();
        Write("a.txt", "one\n");
        Assert.Equal("On branch main (no commits yet): 1 untracked\nuntracked:\n?  a.txt\n" + GitText.Legend, await Invoke(Tool<GitStatusTool>()));
        Commit("a.txt", "one\n", "first");
        Assert.Equal("On branch main: clean", await Invoke(Tool<GitStatusTool>()));
        Write("a.txt", "two\n");
        Write("b.txt", "b\n");
        Assert.Equal("On branch main: 1 modified, 1 untracked\nunstaged:\nM  a.txt\nuntracked:\n?  b.txt\n" + GitText.Legend, await Invoke(Tool<GitStatusTool>()));
        Assert.Equal("Error: '../out' is outside the working directory", await Invoke(Tool<GitStatusTool>(), ("path", "../out")));
        Assert.True(GitText.Note(await Invoke(Tool<GitStatusTool>())).Length <= 200);
    }

    [Fact]
    public async Task Log_ListsRows_ReadsTheCap_AndRefusesABadCount()
    {
        Repo();
        string first = Commit("a.txt", "one\n", "first");
        string second = Commit("a.txt", "two\n", "second");

        string log = await Invoke(Tool<GitLogTool>());
        Assert.Matches($@"^2 commits on main, newest first:\n{second[..7]} 2026-09-01 12:\d\d Test User: second\n{first[..7]} 2026-09-01 12:\d\d Test User: first$", log);
        _settings.GitLibLogMaxCommits = 1;
        Assert.StartsWith("1 commit on main, newest first (more before them):\n", await Invoke(Tool<GitLogTool>()));
        Assert.StartsWith("2 commits on main", await Invoke(Tool<GitLogTool>(), ("max_commits", 5)));
        Assert.Equal("Error: max_commits must be 1 to 200", await Invoke(Tool<GitLogTool>(), ("max_commits", 0)));
        Assert.Equal("Error: 'lots' is not a whole number for 'max_commits'", await Invoke(Tool<GitLogTool>(), ("max_commits", "lots")));
        Assert.Equal("Error: 'nope' names no commit, branch or tag", await Invoke(Tool<GitLogTool>(), ("ref", "nope")));
        Assert.Equal("No commits touch b.txt on main", await Invoke(Tool<GitLogTool>(), ("path", "b.txt"), ("max_commits", 5)));
        Assert.Equal(1, GitLogTool.DefaultCount(new AppSettingsData { GitLibLogMaxCommits = -4 }));
        Assert.Equal(200, GitLogTool.DefaultCount(new AppSettingsData { GitLibLogMaxCommits = 9999 }));
    }

    [WindowsFact]
    public async Task Show_ACommit_AFile_AFolder_AndTheRefusals()
    {
        Repo();
        Commit("a.txt", "one\n", "first");
        string second = Commit(Path.Combine("src", "b.txt"), "b1\nb2\n", "second\n\nBody.\n");

        string show = await Invoke(Tool<GitShowTool>(), ("ref", second[..7]));
        Assert.StartsWith($"Commit {second[..7]} by Test User at 2026-09-01 ", show);
        Assert.EndsWith(": second\nsecond\n\nBody.\nFiles changed (1, +2 −0):\nA src\\b.txt (+2 −0)", show);
        Assert.Equal($"a.txt at {second[..7]} (1 line):\none\n", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "a.txt")));
        Assert.Equal($"src\\ at {second[..7]} (1 entry):\nb.txt", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "src")));
        Assert.Equal("Error: give the commit to show (a sha, a branch, a tag, HEAD~1)", await Invoke(Tool<GitShowTool>()));
        Assert.Equal("Error: 'zzz.txt' is not there", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "zzz.txt")));
        Assert.Equal("Error: 'nope' names no commit, branch or tag", await Invoke(Tool<GitShowTool>(), ("ref", "nope")));
    }

    /// <summary>The Unix twin of <see cref="Show_ACommit_AFile_AFolder_AndTheRefusals"/> (2026-10-06, the macOS build): its paths with <c>/</c>.</summary>
    [UnixFact]
    public async Task Show_ACommit_AFile_AFolder_AndTheRefusals_Unix()
    {
        Repo();
        Commit("a.txt", "one\n", "first");
        string second = Commit(Path.Combine("src", "b.txt"), "b1\nb2\n", "second\n\nBody.\n");

        string show = await Invoke(Tool<GitShowTool>(), ("ref", second[..7]));
        Assert.StartsWith($"Commit {second[..7]} by Test User at 2026-09-01 ", show);
        Assert.EndsWith(": second\nsecond\n\nBody.\nFiles changed (1, +2 −0):\nA src/b.txt (+2 −0)", show);
        Assert.Equal($"a.txt at {second[..7]} (1 line):\none\n", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "a.txt")));
        Assert.Equal($"src/ at {second[..7]} (1 entry):\nb.txt", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "src")));
        Assert.Equal("Error: give the commit to show (a sha, a branch, a tag, HEAD~1)", await Invoke(Tool<GitShowTool>()));
        Assert.Equal("Error: 'zzz.txt' is not there", await Invoke(Tool<GitShowTool>(), ("ref", "HEAD"), ("path", "zzz.txt")));
        Assert.Equal("Error: 'nope' names no commit, branch or tag", await Invoke(Tool<GitShowTool>(), ("ref", "nope")));
    }

    // ---- diff / blame ----

    [Fact]
    public async Task Diff_TheFourForms_TheCut_AndABadMix()
    {
        Repo();
        string first = Commit("a.txt", "one\ntwo\n", "first");
        Write("a.txt", "one\n2\n");

        Assert.Equal("Unstaged changes (1 file, +1 −1):\nM a.txt (+1 −1)\n\n--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n one\n-two\n+2", await Invoke(Tool<GitDiffTool>()));
        Assert.Equal("No staged changes", await Invoke(Tool<GitDiffTool>(), ("staged", true)));
        Assert.Equal("No unstaged changes under src", await Invoke(Tool<GitDiffTool>(), ("path", "src/")));
        await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "a.txt" }));
        Assert.StartsWith("Staged changes (1 file, +1 −1):", await Invoke(Tool<GitDiffTool>(), ("staged", true)));
        string committed = await Invoke(Tool<GitCommitTool>(), ("message", "second"));
        Assert.Matches(@"^Committed [0-9a-f]{7} on main: second \(1 file, \+1 −1\)$", committed);
        string sha = committed[10..17];
        Assert.StartsWith($"Changes in {sha} (second) (1 file, +1 −1):", await Invoke(Tool<GitDiffTool>(), ("ref", "HEAD")));
        Assert.StartsWith($"Changes from {first[..7]} to {sha} (1 file, +1 −1):", await Invoke(Tool<GitDiffTool>(), ("from", first[..7]), ("to", "HEAD")));
        Assert.Equal("Error: 'zzz' is not there", await Invoke(Tool<GitDiffTool>(), ("ref", "HEAD"), ("path", "zzz")));

        _settings.GitLibDiffMaxLines = 20;
        string cut = await Invoke(Tool<GitDiffTool>(), ("ref", "HEAD"), ("max_lines", 20));
        Assert.DoesNotContain("[… cut", cut);   // seven lines fit
        Assert.Equal("Error: max_lines must be 20 to 5000", await Invoke(Tool<GitDiffTool>(), ("max_lines", 5)));
        Assert.Equal("Error: give \"ref\" alone, \"from\" with \"to\", or \"staged\" — not a mix", await Invoke(Tool<GitDiffTool>(), ("ref", "HEAD"), ("staged", true)));
        Assert.Equal("Error: give \"ref\" alone, \"from\" with \"to\", or \"staged\" — not a mix", await Invoke(Tool<GitDiffTool>(), ("from", "HEAD")));
        Assert.Equal("Error: 'maybe' is not true or false for 'staged'", await Invoke(Tool<GitDiffTool>(), ("staged", "maybe")));
        Assert.Equal(20, GitDiffTool.DefaultLines(new AppSettingsData { GitLibDiffMaxLines = 1 }));
        Assert.Null(GitDiffTool.Request("", "HEAD", "a", "b", false));
        Assert.Equal(new GitDiffRequest(GitDiffKind.Range, "src", From: "a", To: "b"), GitDiffTool.Request("src", "", " a ", "b ", false));
        Assert.Equal(new GitDiffRequest(GitDiffKind.Unstaged, ""), GitDiffTool.Request("", "", "", "", false));
    }

    [Fact]
    public async Task Diff_ALongPatch_IsCut_WithTheHint()
    {
        Repo();
        Commit("a.txt", string.Concat(Enumerable.Range(1, 40).Select(i => i + "\n")), "first");
        Write("a.txt", string.Concat(Enumerable.Range(1, 40).Select(i => (i % 5 == 0 ? "x" : "") + i + "\n")));

        string cut = await Invoke(Tool<GitDiffTool>(), ("max_lines", 20));
        Assert.StartsWith("Unstaged changes (1 file, +8 −8):\nM a.txt (+8 −8)\n\n--- a/a.txt\n+++ b/a.txt\n", cut);
        Assert.EndsWith("\n[… cut: 50 lines in all; raise max_lines or narrow with path]", cut);   // two labels, one hunk header, 39 rows of the old text and 8 added
        Assert.Equal(20, cut.Split("\n\n")[1].Split('\n').Length - 1);
    }

    [Fact]
    public async Task Blame_RowsAndWindow_AndTheRefusals()
    {
        Repo();
        string first = Commit("a.txt", "one\ntwo\n", "first");
        string second = Commit("a.txt", "one\n2\n", "second");

        string blame = await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"));
        Assert.Equal($"Blame of a.txt lines 1–2 of 2 at main (2 commits):\n{first[..7]} 2026-09-01 Test User 1│one\n{second[..7]} 2026-09-01 Test User 2│2", blame);
        Assert.Equal($"Blame of a.txt lines 2–2 of 2 at main (1 commit):\n{second[..7]} 2026-09-01 Test User 2│2", await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"), ("from_line", 2)));
        Assert.Equal("Error: give the file to blame", await Invoke(Tool<GitBlameTool>()));
        Assert.Equal("Error: from_line must be 1 or more", await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"), ("from_line", 0)));
        Assert.Equal("Error: 'x' is not a whole number for 'to_line'", await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"), ("to_line", "x")));
        Assert.Equal("Error: 'nope.txt' is not there", await Invoke(Tool<GitBlameTool>(), ("path", "nope.txt")));
    }

    [Fact]
    public async Task Blame_ALongFile_IsWindowed_WithAContinueHint()
    {
        Repo();
        Commit("a.txt", string.Concat(Enumerable.Range(1, 250).Select(i => "line " + i + "\n")), "first");

        string blame = await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"));
        Assert.StartsWith("Blame of a.txt lines 1–200 of 250 at main (1 commit):\n", blame);
        Assert.EndsWith("\n[… 50 more lines; continue with from_line 201]", blame);
        Assert.StartsWith("Blame of a.txt lines 201–250 of 250 at main (1 commit):\n", await Invoke(Tool<GitBlameTool>(), ("path", "a.txt"), ("from_line", 201)));
    }

    // ---- branch / stage / commit / stash ----

    [Fact]
    public async Task Branch_List_Create_Switch_Rename_AndTheRefusals()
    {
        Repo();
        string first = Commit("a.txt", "one\n", "first");

        Assert.Equal($"1 branch (current: main):\n* main {first[..7]} first", await Invoke(Tool<GitBranchTool>(), ("action", "list")));
        Assert.Equal($"Created branch feature at {first[..7]}", await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "feature")));
        Assert.Equal("Error: 'feature' exists already", await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "feature")));
        Assert.Equal($"Created branch topic at {first[..7]} and switched to it", await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "topic"), ("switch_to", true)));
        Assert.Equal($"Switched to branch feature ({first[..7]})", await Invoke(Tool<GitBranchTool>(), ("action", "switch"), ("name", "feature")));
        Assert.Equal("Renamed branch topic to other", await Invoke(Tool<GitBranchTool>(), ("action", "rename"), ("name", "topic"), ("new_name", "other")));
        Assert.Equal($"3 branches (current: feature):\n* feature {first[..7]} first\n  main {first[..7]} first\n  other {first[..7]} first", await Invoke(Tool<GitBranchTool>(), ("action", "list")));
        Assert.Equal("Error: give the branch's name", await Invoke(Tool<GitBranchTool>(), ("action", "switch")));
        Assert.Equal("Error: give the branch's new name", await Invoke(Tool<GitBranchTool>(), ("action", "rename"), ("name", "main")));
        Assert.Equal("Error: 'nope' names no commit, branch or tag", await Invoke(Tool<GitBranchTool>(), ("action", "switch"), ("name", "nope")));
        Assert.Equal("Error: 'delete' is not an action here", await Invoke(Tool<GitBranchTool>(), ("action", "delete"), ("name", "main")));
        Assert.Equal("Error: 'yes' is not true or false for 'switch_to'", await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "x"), ("switch_to", "yes")));

        Commit("a.txt", "feature\n", "on feature");   // the branches differ in a.txt now
        Write("a.txt", "dirty\n");
        Assert.Equal("Error: that would overwrite local changes; commit or stash them first", await Invoke(Tool<GitBranchTool>(), ("action", "switch"), ("name", "main")));
        Assert.Equal($"Created branch third at {first[..7]}, but the switch failed: Error: that would overwrite local changes; commit or stash them first", await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "third"), ("start_point", "main"), ("switch_to", true)));
    }

    [WindowsFact]
    public async Task Stage_Commit_AndTheirRefusals()
    {
        Repo();
        Assert.Equal("Error: give the paths to act on (\".\" for everything changed under \"path\")", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", Array.Empty<string>())));
        Assert.Equal("Error: '42' is not a list of paths for 'paths'", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", 42)));
        Assert.Equal("Error: 'add' is not an action here", await Invoke(Tool<GitStageTool>(), ("action", "add"), ("paths", new[] { "a.txt" })));
        Assert.Equal("Error: give the commit message", await Invoke(Tool<GitCommitTool>(), ("message", " ")));
        Assert.Equal("Error: nothing is staged; stage the changes with gitlib_stage first", await Invoke(Tool<GitCommitTool>(), ("message", "x")));

        Write("a.txt", "one\n");
        Write(Path.Combine("src", "b.txt"), "b\n");
        Assert.Equal("Staged 2 paths: a.txt, src\\b.txt", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." })));
        Assert.Equal("Unstaged 1 path: src\\b.txt", await Invoke(Tool<GitStageTool>(), ("action", "unstage"), ("paths", new[] { "src/b.txt" })));
        Assert.Equal("Staged 1 path: src\\b.txt", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." }), ("path", "src")));
        Assert.Equal("Nothing to stage under src\\", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." }), ("path", "src")));
        Assert.Equal("Error: 'nope.txt' is not there", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "nope.txt" })));
        Assert.Matches(@"^Committed [0-9a-f]{7} on main: first \(2 files, \+2 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "first")));
        Assert.Matches(@"^Amended [0-9a-f]{7} on main: first, again \(2 files, \+2 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "first, again"), ("amend", true)));
        Assert.Equal("Error: 'yes' is not true or false for 'amend'", await Invoke(Tool<GitCommitTool>(), ("message", "x"), ("amend", "yes")));
        Assert.Matches(@"^Committed [0-9a-f]{7} on main: empty \(0 files, \+0 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "empty"), ("allow_empty", true)));
    }

    /// <summary>The Unix twin of <see cref="Stage_Commit_AndTheirRefusals"/> (2026-10-06, the macOS build): its paths with <c>/</c>.</summary>
    [UnixFact]
    public async Task Stage_Commit_AndTheirRefusals_Unix()
    {
        Repo();
        Assert.Equal("Error: give the paths to act on (\".\" for everything changed under \"path\")", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", Array.Empty<string>())));
        Assert.Equal("Error: '42' is not a list of paths for 'paths'", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", 42)));
        Assert.Equal("Error: 'add' is not an action here", await Invoke(Tool<GitStageTool>(), ("action", "add"), ("paths", new[] { "a.txt" })));
        Assert.Equal("Error: give the commit message", await Invoke(Tool<GitCommitTool>(), ("message", " ")));
        Assert.Equal("Error: nothing is staged; stage the changes with gitlib_stage first", await Invoke(Tool<GitCommitTool>(), ("message", "x")));

        Write("a.txt", "one\n");
        Write(Path.Combine("src", "b.txt"), "b\n");
        Assert.Equal("Staged 2 paths: a.txt, src/b.txt", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." })));
        Assert.Equal("Unstaged 1 path: src/b.txt", await Invoke(Tool<GitStageTool>(), ("action", "unstage"), ("paths", new[] { "src/b.txt" })));
        Assert.Equal("Staged 1 path: src/b.txt", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." }), ("path", "src")));
        Assert.Equal("Nothing to stage under src/", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "." }), ("path", "src")));
        Assert.Equal("Error: 'nope.txt' is not there", await Invoke(Tool<GitStageTool>(), ("action", "stage"), ("paths", new[] { "nope.txt" })));
        Assert.Matches(@"^Committed [0-9a-f]{7} on main: first \(2 files, \+2 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "first")));
        Assert.Matches(@"^Amended [0-9a-f]{7} on main: first, again \(2 files, \+2 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "first, again"), ("amend", true)));
        Assert.Equal("Error: 'yes' is not true or false for 'amend'", await Invoke(Tool<GitCommitTool>(), ("message", "x"), ("amend", "yes")));
        Assert.Matches(@"^Committed [0-9a-f]{7} on main: empty \(0 files, \+0 −0\)$", await Invoke(Tool<GitCommitTool>(), ("message", "empty"), ("allow_empty", true)));
    }

    [Fact]
    public async Task Stash_Push_List_Pop_Apply_AndTheRefusals()
    {
        Repo();
        Assert.Equal("Error: the repository has no commits yet", await Invoke(Tool<GitStashTool>(), ("action", "push")));
        Commit("a.txt", "one\n", "first");
        Assert.Equal("Error: nothing to stash: the working tree is clean", await Invoke(Tool<GitStashTool>(), ("action", "push")));
        Assert.Equal("No stashes", await Invoke(Tool<GitStashTool>(), ("action", "list")));

        Write("a.txt", "two\n");
        Assert.Equal("Stashed 1 file as stash@{0}: On main: wip", await Invoke(Tool<GitStashTool>(), ("action", "push"), ("message", "wip")));
        Assert.Matches(@"^1 stash:\nstash@\{0\} [0-9a-f]{7} On main: wip$", await Invoke(Tool<GitStashTool>(), ("action", "list")));
        Assert.Equal("Error: there is no stash@{3}", await Invoke(Tool<GitStashTool>(), ("action", "pop"), ("index", 3)));
        Assert.Equal("Applied stash@{0}: On main: wip (1 file back)", await Invoke(Tool<GitStashTool>(), ("action", "apply")));
        await Invoke(Tool<GitDiscardTool>(), ("paths", new[] { "a.txt" }));
        Assert.Equal("Popped stash@{0}: On main: wip (1 file back)", await Invoke(Tool<GitStashTool>(), ("action", "pop")));
        Assert.Equal("No stashes", await Invoke(Tool<GitStashTool>(), ("action", "list")));
        Assert.Equal("Error: 'drop' is not an action here", await Invoke(Tool<GitStashTool>(), ("action", "drop")));
        Assert.Equal("Error: 'two' is not a whole number for 'index'", await Invoke(Tool<GitStashTool>(), ("action", "pop"), ("index", "two")));
    }

    // ---- the opt-in pair ----

    [Fact]
    public async Task Discard_Paths_OrTheWholeTree_AndTheRefusals()
    {
        Repo();
        string first = Commit("a.txt", "one\n", "first");
        Commit("b.txt", "b\n", "second");
        Write("a.txt", "x\n");
        Write("b.txt", "y\n");
        Write("new.txt", "n\n");

        Assert.Equal("Discarded changes in 1 path (back to main): a.txt", await Invoke(Tool<GitDiscardTool>(), ("paths", new[] { "a.txt" })));
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("Error: 'new.txt' is not there", await Invoke(Tool<GitDiscardTool>(), ("paths", new[] { "new.txt" })));   // untracked: not in HEAD
        Assert.Equal("Error: 'nope' names no commit, branch or tag", await Invoke(Tool<GitDiscardTool>(), ("ref", "nope")));
        Assert.Equal($"Reset hard to {first[..7]} ({first[..7]}: first): 1 file changed; untracked files left alone", await Invoke(Tool<GitDiscardTool>(), ("ref", first[..7])));
        Assert.False(File.Exists(Path.Combine(_root, "b.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
        Assert.Equal("Reset hard to main (" + first[..7] + ": first): 0 files changed; untracked files left alone", await Invoke(Tool<GitDiscardTool>()));
    }

    [Fact]
    public async Task Delete_ABranch_ATag_AStash_AndTheRefusals()
    {
        Repo();
        string first = Commit("a.txt", "one\n", "first");
        await Invoke(Tool<GitBranchTool>(), ("action", "create"), ("name", "feature"));
        using (var repo = new LibGit2Sharp.Repository(_root))
        {
            repo.Tags.Add("v1", repo.Head.Tip);
        }

        Write("a.txt", "two\n");
        await Invoke(Tool<GitStashTool>(), ("action", "push"), ("message", "keep"));

        Assert.Equal("Error: 'main' is the branch checked out; switch to another first", await Invoke(Tool<GitDeleteTool>(), ("kind", "branch"), ("name", "main")));
        Assert.Equal($"Deleted branch feature (was {first[..7]})", await Invoke(Tool<GitDeleteTool>(), ("kind", "branch"), ("name", "feature")));
        Assert.Equal($"Deleted tag v1 (was {first[..7]})", await Invoke(Tool<GitDeleteTool>(), ("kind", "tag"), ("name", "v1")));
        Assert.Equal("Error: 'v1' names no commit, branch or tag", await Invoke(Tool<GitDeleteTool>(), ("kind", "tag"), ("name", "v1")));
        Assert.Equal("Dropped stash@{0}: On main: keep", await Invoke(Tool<GitDeleteTool>(), ("kind", "stash")));
        Assert.Equal("Error: there is no stash@{0}", await Invoke(Tool<GitDeleteTool>(), ("kind", "stash"), ("index", 0)));
        Assert.Equal("Error: give the branch's name", await Invoke(Tool<GitDeleteTool>(), ("kind", "branch")));
        Assert.Equal("Error: 'remote' is not a kind here", await Invoke(Tool<GitDeleteTool>(), ("kind", "remote"), ("name", "origin")));
    }

    [Fact]
    public void GitText_TheSentencesNotReachedAbove_ArePinned()
    {
        Assert.Equal("Error: the repository's root 'D:\\repo' is above the working directory, so the git tools cannot reach it; the user can /cwd to the repository's root", GitText.Error(GitOutcome.AboveSandbox, "D:\\repo"));
        Assert.Equal("Error: 'x' is a bare repository (no working tree)", GitText.Error(GitOutcome.Bare, "x"));
        Assert.Equal("Error: git refuses the repository (owned by someone else); the user can allow it with git config --global --add safe.directory <path>", GitText.Error(GitOutcome.NotOwned, "owned by someone else"));
        Assert.Equal("Error: 'x' is not inside the repository", GitText.Error(GitOutcome.NotInRepository, "x"));
        Assert.Equal("Error: 'ab' is ambiguous; give a longer sha or the full name", GitText.Error(GitOutcome.RefAmbiguous, "ab"));
        Assert.Equal("Error: there is no commit to amend yet", GitText.Error(GitOutcome.NothingToAmend, ""));
        Assert.Equal("Error: the index has unmerged conflicts; ask the user to resolve them first", GitText.Error(GitOutcome.Conflicts, ""));
        Assert.Equal(GitText.NoIdentity, GitText.Error(GitOutcome.NoIdentity, ""));
        Assert.Equal("Error: 'x.png' is binary", GitText.Error(GitOutcome.Binary, "x.png"));
        Assert.Equal("Error: 'big' is too big to read as text", GitText.Error(GitOutcome.TooBig, "big"));
        Assert.Equal("Error: at most 100 paths in one call", GitText.Error(GitOutcome.TooManyPaths, ""));
        Assert.Equal("Error: git refused (boom)", GitText.Error(GitOutcome.Failed, "boom"));
        Assert.Equal("Error: git refused ()", GitText.Error(GitOutcome.Ok, ""));
        Assert.Equal("first line", GitText.Note("first line\nsecond"));
        Assert.Equal("2026-09-20 14:05", GitText.When(new DateTimeOffset(2026, 9, 20, 21, 5, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time")));
        Assert.Equal("Files changed (2, +3 −1):\nM a (+3 −1)\nD b (binary)", GitText.FilesChanged([new GitChange('M', "a", null, 3, 1, false), new GitChange('D', "b", null, 0, 0, true)], truncated: false));
        Assert.Equal("No files changed", GitText.FilesChanged([], truncated: false));
        Assert.Equal("On branch main (ahead 2, behind 1 of origin/main): 1 conflict, 1 staged\nconflicts:\nU  c\nstaged:\nR  old → new\n" + GitText.Legend,
            GitText.Status(new GitStatusReport(GitOutcome.Ok, "", "main", false, false, "abc1234", "origin/main", 2, 1, [new GitEntry('R', "new", "old")], [], [], [new GitEntry('U', "c")], false)));
        Assert.Equal("On branch main (up to date with origin/main): clean", GitText.Status(new GitStatusReport(GitOutcome.Ok, "", "main", false, false, "abc1234", "origin/main", 0, 0, [], [], [], [], false)));
        Assert.Equal("Detached at abc1234: clean", GitText.Status(new GitStatusReport(GitOutcome.Ok, "", "(no branch)", false, true, "abc1234", null, null, null, [], [], [], [], false)));
    }
}
