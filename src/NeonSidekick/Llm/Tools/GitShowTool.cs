using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_show(ref, path?)</c>: a commit's header, message and changed files; with a path, that file's text (or that folder's entries) as it is at the commit.</summary>
public sealed class GitShowTool : GitTool
{
    public const string ToolName = "gitlib_show";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "ref": { "type": "string", "description": "The commit: a sha, a branch, a tag, HEAD, HEAD~1." },
            {{PathProperty}}
          },
          "required": ["ref"]
        }
        """);

    public GitShowTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows one commit: who made it, when, its message and the files it changed with their line counts. " +
        "With a file as path, the file's text as it is at that commit (read_file shows the working copy); with a folder, its entries there.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string reference, string path)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return GitText.NoReference;
        }

        var report = Git.Show(reference, path);
        return report.Outcome == GitOutcome.Ok ? GitText.Show(report, Zone) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(reference, path), cancellationToken);
    }
}
