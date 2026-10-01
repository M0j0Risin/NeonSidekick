using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Git;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>gitlib_diff(path?, ref?, from?, to?, staged?, max_lines?)</c>: the unstaged changes (a bare call), the
/// staged ones (<c>staged</c>), one commit's against its parent (<c>ref</c>) or two refs' (<c>from</c> + <c>to</c>),
/// as the changed files with their counts and a unified patch. The patch is the app's own
/// (<see cref="UnifiedDiff"/>): LibGit2Sharp's diff kills the process under NativeAOT.
/// </summary>
public sealed class GitDiffTool : GitTool
{
    public const string ToolName = "gitlib_diff";
    public const string FromArgument = "from";
    public const string ToArgument = "to";
    public const string StagedArgument = "staged";
    public const string MaxLinesArgument = "max_lines";

    public const int MinLines = AppSettingsData.MinGitLibDiffMaxLines;
    public const int MaxLines = AppSettingsData.MaxGitLibDiffMaxLines;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            {{PathProperty}},
            "ref": { "type": "string", "description": "One commit: its changes against its parent." },
            "from": { "type": "string", "description": "With to: the two commits to compare (from's tree to to's)." },
            "to": { "type": "string", "description": "With from: the newer commit." },
            "staged": { "type": "boolean", "description": "true: what is staged (HEAD against the index). Leave it out for the unstaged changes (the index against the working tree)." },
            "max_lines": { "type": "integer", "description": "The most patch lines to show, 20 to 5000. Leave it out for the user's default; the file list is always whole." }
          }
        }
        """);

    public GitDiffTool(GitAccess git, Func<AppSettingsData> effective) : base(git, effective)
    {
    }

    public override string Name => ToolName;

    public override string Description =>
        "Shows changes as a unified diff with the files and their line counts first: with nothing but path, the unstaged changes in the working tree; " +
        "staged: true, what is staged; ref, one commit against its parent; from and to, everything between two commits. " +
        "Untracked files are not in it (gitlib_status lists them); a long patch is cut at max_lines — narrow it with path.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The line cap a call without <c>max_lines</c> gets: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int DefaultLines(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.GitLibDiffMaxLines, MinLines, MaxLines);
    }

    /// <summary>Which diff the arguments ask for, or null for a mix that names none (<see cref="GitText.BadDiffArguments"/>).</summary>
    public static GitDiffRequest? Request(string path, string reference, string from, string to, bool staged)
    {
        bool hasRef = !string.IsNullOrWhiteSpace(reference);
        bool hasFrom = !string.IsNullOrWhiteSpace(from);
        bool hasTo = !string.IsNullOrWhiteSpace(to);
        int forms = (hasRef ? 1 : 0) + (hasFrom || hasTo ? 1 : 0) + (staged ? 1 : 0);
        if (forms > 1 || hasFrom != hasTo)
        {
            return null;
        }

        if (hasRef)
        {
            return new GitDiffRequest(GitDiffKind.Commit, path, Reference: reference.Trim());
        }

        if (hasFrom)
        {
            return new GitDiffRequest(GitDiffKind.Range, path, From: from.Trim(), To: to.Trim());
        }

        return new GitDiffRequest(staged ? GitDiffKind.Staged : GitDiffKind.Unstaged, path);
    }

    public string Describe(string path, string reference, string from, string to, bool staged, int? maxLines)
    {
        int lines = maxLines ?? DefaultLines(Effective);
        if (lines < MinLines || lines > MaxLines)
        {
            return GitText.BadDiffLines(MinLines, MaxLines);
        }

        if (Request(path, reference, from, to, staged) is not { } request)
        {
            return GitText.BadDiffArguments;
        }

        var report = Git.Diff(request, lines);
        string under = path.Trim().Trim('\\', '/');
        return report.Outcome == GitOutcome.Ok ? GitText.Diff(report, under == "." ? "" : under) : GitText.Error(report.Outcome, report.Detail);
    }

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxLinesArgument, out var max, out var raw))
        {
            return new ValueTask<object?>(ClockText.BadInteger(MaxLinesArgument, raw));
        }

        if (!ToolArguments.TryReadBoolean(arguments, StagedArgument, out var staged, out raw))
        {
            return new ValueTask<object?>(Files.FileText.BadBoolean(StagedArgument, raw));
        }

        string path = ReadPath(arguments);
        string reference = ToolArguments.ReadString(arguments, ReferenceArgument);
        string from = ToolArguments.ReadString(arguments, FromArgument);
        string to = ToolArguments.ReadString(arguments, ToArgument);
        return OffThread(() => Describe(path, reference, from, to, staged ?? false, max), cancellationToken);
    }
}
