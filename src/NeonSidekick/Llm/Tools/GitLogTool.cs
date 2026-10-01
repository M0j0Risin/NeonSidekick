using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_log(path?, ref?, max_commits?)</c>: the commits reachable from <c>ref</c> (HEAD by default), newest first; under a file or folder, those that changed it.</summary>
public sealed class GitLogTool : GitTool
{
    public const string ToolName = "gitlib_log";
    public const string MaxCommitsArgument = "max_commits";

    public const int MinCommits = AppSettingsData.MinGitLibLogMaxCommits;
    public const int MaxCommits = AppSettingsData.MaxGitLibLogMaxCommits;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{PathProperty}},
            "ref": { "type": "string", "description": "The commit to start from: a branch, a tag, a sha, HEAD~3. Leave it out for HEAD." },
            "max_commits": { "type": "integer", "description": "How many commits at most, 1 to 200. Leave it out for the user's default." }
          }
        }
        """);

    public GitLogTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the git history: the commits reachable from ref (HEAD by default), newest first, one line each with the short sha, the date, the author and the subject. " +
        "With a file or folder as path, only the commits that changed it.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The count a call without <c>max_commits</c> gets: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int DefaultCount(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.GitLibLogMaxCommits, MinCommits, MaxCommits);
    }

    public string Describe(string path, string reference, int? maxCommits)
    {
        int count = maxCommits ?? DefaultCount(Effective);
        if (count < MinCommits || count > MaxCommits)
        {
            return GitText.BadLogCount(MinCommits, MaxCommits);
        }

        var report = Git.Log(path, reference, count);
        return report.Outcome == GitOutcome.Ok ? GitText.Log(report, Zone) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxCommitsArgument, out var max, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(MaxCommitsArgument, raw));
        }

        string path = ReadPath(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        return OffThread(() => Describe(path, reference, max), cancellationToken);
    }
}
