using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary><c>gitlib_commit(message, amend?, allow_empty?, path?)</c>: commits the index, signed from git config (<c>user.name</c> / <c>user.email</c>; without them an <c>Error:</c> that asks the user to set them).</summary>
public sealed class GitCommitTool : GitTool
{
    public const string ToolName = "gitlib_commit";
    public const string MessageArgument = "message";
    public const string AmendArgument = "amend";
    public const string AllowEmptyArgument = "allow_empty";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "The commit message: a short imperative subject line, a blank line and details when they help." },
            "amend": { "type": "boolean", "description": "true: replace the last commit with the index and this message instead of adding a new one." },
            "allow_empty": { "type": "boolean", "description": "true: commit even when nothing is staged." },
            {{PathProperty}}
          },
          "required": ["message"]
        }
        """);

    public GitCommitTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Commits what is staged with the message given, signed with the user's git identity (user.name / user.email from git config). " +
        "Stage with gitlib_stage first; commit only what the user asked for, with their message or a short imperative one.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string message, bool amend, bool allowEmpty, string path)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return GitText.NoMessage;
        }

        var result = Git.Commit(path, message.Trim(), amend, allowEmpty);
        return result.Outcome == GitOutcome.Ok ? GitText.Committed(result) : GitText.Error(result.Outcome, result.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, AmendArgument, out var amend, out var raw))
        {
            return new ValueTask<object?>(Files.FileText.BadBoolean(AmendArgument, raw));
        }

        if (!ToolArguments.TryReadBoolean(arguments, AllowEmptyArgument, out var allowEmpty, out raw))
        {
            return new ValueTask<object?>(Files.FileText.BadBoolean(AllowEmptyArgument, raw));
        }

        string message = ToolArguments.ReadString(arguments, MessageArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(message, amend ?? false, allowEmpty ?? false, path), cancellationToken);
    }
}
