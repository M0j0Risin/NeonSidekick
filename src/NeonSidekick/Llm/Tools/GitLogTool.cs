using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_log(path?, ref?, max_commits?, since?, until?, author?, grep?)</c>: the commits reachable from <c>ref</c> (HEAD by default), newest
/// first; under a file or folder, those that changed it; filtered by the author date, the author and the message (2026-11-15, feature idea #44).
/// </summary>
public sealed class GitLogTool : GitTool
{
    public const string ToolName = "gitlib_log";
    public const string MaxCommitsArgument = "max_commits";
    public const string SinceArgument = "since";
    public const string UntilArgument = "until";
    public const string AuthorArgument = "author";
    public const string GrepArgument = "grep";

    public const int MinCommits = AppSettingsData.MinGitLibLogMaxCommits;
    public const int MaxCommits = AppSettingsData.MaxGitLibLogMaxCommits;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{PathProperty}},
            "ref": { "type": "string", "description": "The commit to start from: a branch, a tag, a sha, HEAD~3. Leave it out for HEAD." },
            "max_commits": { "type": "integer", "description": "How many commits at most, 1 to 200. Leave it out for the user's default." },
            "since": { "type": "string", "description": "Only commits from then on: an age (30m, 2h, 1d, 1w) or a moment (2026-10-14T21:00, in the user's time zone unless it gives an offset)." },
            "until": { "type": "string", "description": "Only commits until then: an age or a moment; a bare date means its midnight." },
            "author": { "type": "string", "description": "Only commits whose author's name or email holds this, case ignored." },
            "grep": { "type": "string", "description": "Only commits whose message holds this, case ignored." }
          }
        }
        """);

    public GitLogTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the git history: the commits reachable from ref (HEAD by default), newest first, one line each with the short sha, the date, the author and the subject. " +
        "With a file or folder as path, only the commits that changed it. since and until (an age or a moment), author and grep (the message) filter it.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The count a call without <c>max_commits</c> gets: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int DefaultCount(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.GitLibLogMaxCommits, MinCommits, MaxCommits);
    }

    public string Describe(string path, string reference, int? maxCommits, GitLogFilter? filter = null)
    {
        int count = maxCommits ?? DefaultCount(Effective);
        if (count < MinCommits || count > MaxCommits)
        {
            return GitText.BadLogCount(MinCommits, MaxCommits);
        }

        var report = Git.Log(path, reference, count, filter);
        return report.Outcome == GitOutcome.Ok ? GitText.Log(report, Zone) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxCommitsArgument, out var max, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(MaxCommitsArgument, raw));
        }

        if (ReadFilter(arguments, out string? error) is not { } filter)
        {
            return new ValueTask<object?>(error);
        }

        string path = ReadPath(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        return OffThread(() => Describe(path, reference, max, filter), cancellationToken);
    }

    /// <summary>
    /// The filters (2026-11-15): the times through <see cref="AdminArguments.Moment"/> at the clock's now and zone, a start held down to the
    /// second and an end up, so a commit at either moment is kept; the texts held to <see cref="ArgumentText.MaxTextChars"/>. Null with the
    /// refusal for a bad time, a window that ends before it starts, or a text too long.
    /// </summary>
    private GitLogFilter? ReadFilter(AIFunctionArguments arguments, out string? error)
    {
        var now = Git.Now;
        var (since, sinceError) = AdminArguments.Moment(arguments, SinceArgument, now, Zone, end: false, GitText.BadTime);
        var (until, untilError) = AdminArguments.Moment(arguments, UntilArgument, now, Zone, end: true, GitText.BadTime);
        error = sinceError ?? untilError;
        if (error is not null)
        {
            return null;
        }

        if (since is not null && until is not null && since > until)
        {
            error = GitText.SinceAfterUntil;
            return null;
        }

        string? author = AdminArguments.Optional(arguments, AuthorArgument);
        string? grep = AdminArguments.Optional(arguments, GrepArgument);
        foreach (var (name, text) in (ReadOnlySpan<(string, string?)>)[(AuthorArgument, author), (GrepArgument, grep)])
        {
            if (text is { Length: > ArgumentText.MaxTextChars })
            {
                error = ArgumentText.TooLong(name, ArgumentText.MaxTextChars);
                return null;
            }
        }

        return new GitLogFilter(since, until, author, grep);
    }
}
