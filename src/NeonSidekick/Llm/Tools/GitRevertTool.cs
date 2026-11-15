using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_revert(ref, message?, path?)</c> (2026-11-15, feature idea #32): a new commit undoing one, nothing lost and history never
/// rewritten (<see cref="GitAccess.Revert"/>). The commit is named <c>ref</c>, as every git tool names one, so <c>HEAD~2</c>, a tag or a short
/// sha all work; a blank one is refused rather than read as HEAD.
/// </summary>
public sealed class GitRevertTool : GitTool
{
    public const string ToolName = "gitlib_revert";
    public const string MessageArgument = "message";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "ref": { "type": "string", "description": "The commit to undo: a sha, a branch, a tag, HEAD, HEAD~1." },
            "message": { "type": "string", "description": "The new commit's message. Leave it out for git's: Revert \"<subject>\" and the commit it reverts." },
            {{PathProperty}}
          },
          "required": ["ref"]
        }
        """);

    public GitRevertTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Undoes a commit with a new commit that reverses its changes; nothing is lost, and the reverted commit stays in the history. " +
        "Refuses while there are uncommitted changes, and for a merge commit; a revert that would conflict is put back and its files named. " +
        "It never rewrites history, and leaves nothing half done to abort.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string reference, string message, string path)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return GitText.NoRevertReference;
        }

        var result = Git.Revert(path, reference, message);
        return result.Outcome == GitOutcome.Ok ? GitText.Reverted(result) : GitText.Error(result.Outcome, result.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        string message = ToolArguments.ReadString(arguments, MessageArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(reference, message, path), cancellationToken);
    }
}
