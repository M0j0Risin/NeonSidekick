using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_delete(kind, name?, index?, path?)</c>: removes a local branch (never the one checked out), a tag
/// or a stash. Off by name in a fresh profile's <c>ToolsDisabled</c> beside <see cref="GitDiscardTool"/>
/// (2026-09-20): a dropped stash or an unmerged branch's commits are reachable only through the reflog.
/// </summary>
public sealed class GitDeleteTool : GitTool
{
    public const string ToolName = "gitlib_delete";
    public const string KindArgument = "kind";
    public const string NameArgument = "name";
    public const string IndexArgument = "index";

    public const string BranchKind = "branch";
    public const string TagKind = "tag";
    public const string StashKind = "stash";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["branch", "tag", "stash"], "description": "What to remove: a local branch by name (not the one checked out), a tag by name, or a stash by index." },
            "name": { "type": "string", "description": "The branch or tag." },
            "index": { "type": "integer", "description": "For stash: its number (stash@{1} is 1)." },
            {{PathProperty}}
          },
          "required": ["kind"]
        }
        """);

    public GitDeleteTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Removes a local branch (never the one checked out), a tag, or a stash by its index. " +
        "A branch's unmerged commits and a dropped stash are gone from every listing — do it only when the user asked for exactly that.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string kind, string name, int? index, string path)
    {
        GitDeleteKind which;
        switch (kind.Trim().ToLowerInvariant())
        {
            case BranchKind:
                which = GitDeleteKind.Branch;
                break;
            case TagKind:
                which = GitDeleteKind.Tag;
                break;
            case StashKind:
                which = GitDeleteKind.Stash;
                break;
            default:
                return GitText.BadKind(kind);
        }

        if (which != GitDeleteKind.Stash && string.IsNullOrWhiteSpace(name))
        {
            return GitText.NoName;
        }

        var result = Git.Delete(path, which, name, index ?? 0);
        return result.Outcome == GitOutcome.Ok ? GitText.Deleted(result) : GitText.Error(result.Outcome, result.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, IndexArgument, out var index, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(IndexArgument, raw));
        }

        string kind = ToolArguments.ReadString(arguments, KindArgument);
        string name = ToolArguments.ReadString(arguments, NameArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(kind, name, index, path), cancellationToken);
    }
}
