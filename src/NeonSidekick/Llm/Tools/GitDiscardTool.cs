using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_discard(paths?, ref?, path?)</c>: throws local changes away — the paths named put back as they
/// are at <c>ref</c> (HEAD by default), or with no paths the whole tree reset hard to it. The one git tool
/// that loses uncommitted work, so it is off by name in a fresh profile's <c>ToolsDisabled</c> (the
/// <c>delete</c> precedent, 2026-09-20) and flipped on <c>/tools</c>' Offered tab.
/// </summary>
public sealed class GitDiscardTool : GitTool
{
    public const string ToolName = "gitlib_discard";
    public const string PathsArgument = "paths";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "paths": { "type": "array", "items": { "type": "string" }, "description": "The files or folders to put back as they are at ref, relative to the working directory (at most {{GitAccess.MaxPathsPerCall}}). Leave it out to reset the whole tree hard to ref." },
            "ref": { "type": "string", "description": "The commit to restore from. Leave it out for HEAD." },
            {{PathProperty}}
          }
        }
        """);

    public GitDiscardTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Throws uncommitted changes away for good: the paths named go back to how they are at ref (HEAD by default), index and working tree alike; " +
        "with no paths the whole tree is reset hard to ref (untracked files are left alone). Nothing brings the changes back — do it only when the user asked for exactly that.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(IReadOnlyList<string> paths, string reference, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var given = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        var result = Git.Discard(path, given, reference);
        return result.Outcome == GitOutcome.Ok ? GitText.Discarded(result) : GitText.Error(result.Outcome, result.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadStringList(arguments, PathsArgument, out var paths, out var raw))
        {
            return new ValueTask<object?>(Files.FileText.BadStringList(PathsArgument, raw));
        }

        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(paths, reference, path), cancellationToken);
    }
}
