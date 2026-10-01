using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_blame(path, from_line?, to_line?, ref?)</c>: who last changed each line of a file, over a window of at most <see cref="GitAccess.MaxBlameLines"/> lines.</summary>
public sealed class GitBlameTool : GitTool
{
    public const string ToolName = "gitlib_blame";
    public const string FromLineArgument = "from_line";
    public const string ToLineArgument = "to_line";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The file, relative to the working directory." },
            "from_line": { "type": "integer", "description": "The first line to blame, 1-based. Leave it out for the start." },
            "to_line": { "type": "integer", "description": "The last line, inclusive; at most {{GitAccess.MaxBlameLines}} lines in one call. Leave it out for from_line + {{GitAccess.MaxBlameLines - 1}}." },
            "ref": { "type": "string", "description": "The commit to blame at. Leave it out for HEAD." }
          },
          "required": ["path"]
        }
        """);

    public GitBlameTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows who last changed each line of a file and in which commit: one row per line with the short sha, the date, the author, the line number and the text; " +
        "a window of lines at a time (from_line, to_line), at HEAD unless ref says otherwise.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string path, int? fromLine, int? toLine, string reference)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return GitText.NoBlamePath;
        }

        if (fromLine is < 1)
        {
            return GitText.BadLine(FromLineArgument);
        }

        if (toLine is < 1)
        {
            return GitText.BadLine(ToLineArgument);
        }

        var report = Git.Blame(path, reference, fromLine, toLine);
        return report.Outcome == GitOutcome.Ok ? GitText.Blame(report, Zone) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, FromLineArgument, out var from, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(FromLineArgument, raw));
        }

        if (!ToolArguments.TryReadInt32(arguments, ToLineArgument, out var to, out raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(ToLineArgument, raw));
        }

        string path = ReadPath(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        return OffThread(() => Describe(path, from, to, reference), cancellationToken);
    }
}
