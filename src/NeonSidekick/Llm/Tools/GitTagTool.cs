using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_tag(name?, ref?, message?, path?)</c> (2026-11-15, feature idea #32): without a name the tags, as <c>gitlib_branch list</c> shows
/// them; with one, a tag on <c>ref</c> (HEAD by default), lightweight or annotated with a message (<see cref="GitAccess.CreateTag"/>). Never moves
/// an existing tag; <c>gitlib_delete</c> removes one. Both of <c>git tag</c>'s forms redirect here.
/// </summary>
public sealed class GitTagTool : GitTool, IListOnlyTwin
{
    public const string ToolName = "gitlib_tag";
    public const string NameArgument = "name";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The new tag's name, e.g. v1.2.0. Leave it out (with ref and message) to list the tags." },
            "ref": { "type": "string", "description": "The commit to tag: a sha, a branch, HEAD~1. Leave it out for HEAD." },
            "message": { "type": "string", "description": "A message makes an annotated tag, signed with the user's git identity; leave it out for a lightweight tag." },
            {{RepoPathProperty}}
          }
        }
        """);

    private readonly bool _listOnly;

    public GitTagTool(GitAccess git, Func<AppSettingsData> effective) : this(git, effective, listOnly: false)
    {
    }

    private GitTagTool(GitAccess git, Func<AppSettingsData> effective, bool listOnly) : base(git, effective) => _listOnly = listOnly;

    /// <summary>Plan mode's twin (2026-11-15, the tool-arguments review's #7, #106): the list form alone.</summary>
    public AIFunction AsListOnly() => _listOnly ? this : new GitTagTool(Git, EffectiveSource, listOnly: true);

    /// <inheritdoc/>
    public string? Refusal(IDictionary<string, object?>? arguments) =>
        _listOnly && !Plans.PlanTools.IsListForm(ToolName, arguments) ? Plans.PlanText.ListOnlyRefusal(ToolName, Plans.PlanText.TagListForm) : null;

    public override string Name => ToolName;

    public override string Description => _listOnly ? FullDescription + " " + Plans.PlanText.ListOnlyNote : FullDescription;

    private const string FullDescription =
        "Lists the tags, or with a name makes one on ref (HEAD by default): lightweight, or annotated with a message; " +
        "never moves an existing tag, and never takes a branch's name; deleting a tag is gitlib_delete's job.";

    public override JsonElement JsonSchema => Schema;

    public string Describe(string name, string reference, string message, string path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // ref or message without a name is the tool's own refusal, never GitText.Error's BadArguments (which talks about staging).
            if (!string.IsNullOrWhiteSpace(reference) || !string.IsNullOrWhiteSpace(message))
            {
                return GitText.TagNeedsName;
            }

            var report = Git.Tags(path);
            return report.Outcome == GitOutcome.Ok ? GitText.Tags(report) : GitText.Error(report.Outcome, report.Detail);
        }

        // "list" as a name is the list asked the wrong way (2026-11-15).
        if (name.Trim().Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return GitText.TagNamedList;
        }

        var result = Git.CreateTag(path, name, reference, message);
        return result.Outcome switch
        {
            GitOutcome.Ok => GitText.Tagged(result),
            GitOutcome.NoIdentity => GitText.NoTagIdentity,   // #94: a tag's own sentence, never the commit's
            _ => GitText.Error(result.Outcome, result.Detail),
        };
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (Refusal(arguments) is { } refused)
        {
            return new ValueTask<object?>(refused);
        }

        string name = ToolArguments.ReadString(arguments, NameArgument);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        string message = ToolArguments.ReadString(arguments, MessageArgument);
        string path = ReadPath(arguments);
        return OffThread(() => Describe(name, reference, message, path), cancellationToken);
    }
}
