using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_stash(action, message?, index?, include_untracked?, path?)</c>: saves the working tree's changes aside (<c>push</c>), brings them back (<c>pop</c>, <c>apply</c>), lists them; dropping one is <c>gitlib_delete</c>'s.</summary>
public sealed class GitStashTool : GitTool
{
    public const string ToolName = "gitlib_stash";
    public const string ActionArgument = "action";
    public const string MessageArgument = "message";
    public const string IndexArgument = "index";
    public const string IncludeUntrackedArgument = "include_untracked";

    public const string PushAction = "push";
    public const string PopAction = "pop";
    public const string ApplyAction = "apply";
    public const string ListAction = "list";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["push", "pop", "apply", "list"], "description": "push: save the staged and unstaged changes as stash@{0} and restore HEAD. pop: apply stash@{index} and drop it (kept when it conflicts). apply: apply it and keep it. list: every stash, newest first." },
            "message": { "type": "string", "description": "For push: what the stash holds." },
            "index": { "type": "integer", "description": "For pop and apply: the stash's number (stash@{2} is 2). Leave it out for 0." },
            "include_untracked": { "type": "boolean", "description": "For push: true also stashes the untracked files." },
            {{PathProperty}}
          },
          "required": ["action"]
        }
        """);

    public GitStashTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Puts the working tree's changes aside and brings them back: push saves them as a stash and cleans the tree, pop or apply restores stash@{index}, list shows them. " +
        "Dropping a stash is gitlib_delete's job.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string action, string message, int? index, bool includeUntracked, string path)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case ListAction:
            {
                var result = Git.Stashes(path);
                return result.Outcome == GitOutcome.Ok ? GitText.Stashes(result) : GitText.Error(result.Outcome, result.Detail);
            }

            case PushAction:
            {
                var result = Git.StashPush(path, message, includeUntracked);
                return result.Outcome == GitOutcome.Ok ? GitText.Stashed(result) : GitText.Error(result.Outcome, result.Detail);
            }

            case PopAction:
            case ApplyAction:
            {
                bool pop = action.Trim().Equals(PopAction, StringComparison.OrdinalIgnoreCase);
                var result = Git.StashApply(path, index ?? 0, pop);
                return result.Outcome == GitOutcome.Ok ? GitText.StashApplied(result, pop) : GitText.Error(result.Outcome, result.Detail);
            }

            default:
                return GitText.BadAction(action);
        }
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, IndexArgument, out var index, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(IndexArgument, raw));
        }

        if (!ToolArguments.TryReadBoolean(arguments, IncludeUntrackedArgument, out var untracked, out raw))
        {
            return new ValueTask<object?>(Files.FileText.BadBoolean(IncludeUntrackedArgument, raw));
        }

        string action = ToolArguments.ReadString(arguments, ActionArgument);
        string message = ToolArguments.ReadString(arguments, MessageArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(action, message, index, untracked ?? false, path), cancellationToken);
    }
}
