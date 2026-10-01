using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_branch(action, name?, new_name?, start_point?, switch_to?, path?)</c>: the branches and tags (<c>list</c>), a new branch (<c>create</c>), a checkout (<c>switch</c>), a rename.</summary>
public sealed class GitBranchTool : GitTool
{
    public const string ToolName = "gitlib_branch";
    public const string ActionArgument = "action";
    public const string NameArgument = "name";
    public const string NewNameArgument = "new_name";
    public const string StartPointArgument = "start_point";
    public const string SwitchToArgument = "switch_to";

    public const string ListAction = "list";
    public const string CreateAction = "create";
    public const string SwitchAction = "switch";
    public const string RenameAction = "rename";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list", "create", "switch", "rename"], "description": "list: the local branches (the current one marked), the remote-tracking branches and the tags. create: a branch named name at start_point (HEAD by default), checked out when switch_to is true. switch: check the local branch name out. rename: the branch name becomes new_name." },
            "name": { "type": "string", "description": "The branch (create, switch, rename)." },
            "new_name": { "type": "string", "description": "For rename: the new name." },
            "start_point": { "type": "string", "description": "For create: the commit the branch starts at (a sha, a branch, a tag). Leave it out for HEAD." },
            "switch_to": { "type": "boolean", "description": "For create: true also checks the new branch out." },
            {{PathProperty}}
          },
          "required": ["action"]
        }
        """);

    public GitBranchTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists, creates, switches to or renames git branches. A switch never overwrites local changes (commit or stash them first); " +
        "deleting a branch is gitlib_delete's job.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string action, string name, string newName, string startPoint, bool switchTo, string path)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case ListAction:
            {
                var report = Git.Refs(path);
                return report.Outcome == GitOutcome.Ok ? GitText.Refs(report) : GitText.Error(report.Outcome, report.Detail);
            }

            case CreateAction:
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return GitText.NoName;
                }

                var result = Git.CreateBranch(path, name, startPoint, switchTo);
                if (result.Outcome == GitOutcome.Ok)
                {
                    return switchTo ? GitText.CreatedAndSwitched(result) : GitText.Created(result);
                }

                // The branch stands when the switch alone failed: say both.
                return result.Name.Length > 0
                    ? GitText.CreatedButNotSwitched(result.Name, result.Short, GitText.Error(result.Outcome, result.Detail))
                    : GitText.Error(result.Outcome, result.Detail);
            }

            case SwitchAction:
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return GitText.NoName;
                }

                var result = Git.SwitchBranch(path, name);
                return result.Outcome == GitOutcome.Ok ? GitText.Switched(result) : GitText.Error(result.Outcome, result.Detail);
            }

            case RenameAction:
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return GitText.NoName;
                }

                if (string.IsNullOrWhiteSpace(newName))
                {
                    return GitText.NoNewName;
                }

                var result = Git.RenameBranch(path, name, newName);
                return result.Outcome == GitOutcome.Ok ? GitText.Renamed(result) : GitText.Error(result.Outcome, result.Detail);
            }

            default:
                return GitText.BadAction(action);
        }
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, SwitchToArgument, out var switchTo, out var raw))
        {
            return new ValueTask<object?>(Files.FileText.BadBoolean(SwitchToArgument, raw));
        }

        string action = ToolArguments.ReadString(arguments, ActionArgument);
        string name = ToolArguments.ReadString(arguments, NameArgument);
        string newName = ToolArguments.ReadString(arguments, NewNameArgument);
        string start = ToolArguments.ReadString(arguments, StartPointArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(action, name, newName, start, switchTo ?? false, path), cancellationToken);
    }
}
