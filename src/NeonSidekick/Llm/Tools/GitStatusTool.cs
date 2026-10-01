using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_status(path?)</c>: the branch, its upstream lag, and every staged, unstaged, untracked and conflicted path under <c>path</c>.</summary>
public sealed class GitStatusTool : GitTool
{
    public const string ToolName = "gitlib_status";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{PathProperty}}
          }
        }
        """);

    public GitStatusTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows the state of the git repository under the working directory: the branch, how far ahead or behind its upstream it is, " +
        "and every staged, modified, untracked or conflicted path (narrowed to path when given). Call it before staging or committing.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path)
    {
        var report = Git.Status(path);
        return report.Outcome == GitOutcome.Ok ? GitText.Status(report) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(path), cancellationToken);
    }
}
