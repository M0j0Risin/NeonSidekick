using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_stage(action, paths, path?)</c>: stages or unstages paths — <c>.</c> for everything changed under <c>path</c>.</summary>
public sealed class GitStageTool : GitTool
{
    public const string ToolName = "gitlib_stage";
    public const string ActionArgument = "action";
    public const string PathsArgument = "paths";

    public const string StageAction = "stage";
    public const string UnstageAction = "unstage";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["stage", "unstage"], "description": "stage: add the paths' changes (new, modified, deleted files) to the index. unstage: take them out of the index again; the working tree is untouched either way." },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "The files or folders, relative to the working directory; \".\" alone means everything changed under path (at most {{GitAccess.MaxPathsPerCall}} paths)." },
            {{PathProperty}}
          },
          "required": ["action", "paths"]
        }
        """);

    public GitStageTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Stages or unstages changes for the next commit: the paths named, or \".\" for everything changed under path. " +
        "Stage only what the user asked to commit; gitlib_status shows what is staged.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string action, IReadOnlyList<string> paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        bool unstage;
        switch (action.Trim().ToLowerInvariant())
        {
            case StageAction:
                unstage = false;
                break;
            case UnstageAction:
                unstage = true;
                break;
            default:
                return GitText.BadAction(action);
        }

        if (paths.Count == 0 || paths.All(string.IsNullOrWhiteSpace))
        {
            return GitText.NoPaths;
        }

        var result = Git.Stage(path, paths, unstage);
        return result.Outcome == GitOutcome.Ok ? GitText.Staged(result) : GitText.Error(result.Outcome, result.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var paths, out var raw))
        {
            return new ValueTask<object?>(Files.FileText.BadStringList(PathsArgument, raw));
        }

        string action = ToolArguments.ReadString(arguments, ActionArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(action, paths, path), cancellationToken);
    }
}
