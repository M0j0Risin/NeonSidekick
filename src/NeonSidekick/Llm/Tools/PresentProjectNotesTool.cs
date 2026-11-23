using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.ProjectInit;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>present_project_notes(content)</c> (2026-11-23, <c>/project-init</c>): the model's draft of the working folder's notes, put to the user on
/// the Write pane (the seam it is built over) inside the call, so the write happens while the turn's file journal is entered and <c>/undo</c>
/// and <c>/rewind</c> take it back. Offered only during a pass, through the turn's plan slot. One answer a pass: after Write or Cancel a later
/// call shows nothing (<see cref="ProjectInitText.AlreadyDoneResult"/>); a Revise leaves it open. A draft the same as the file writes
/// nothing. The write lifts only the project-file ask (<see cref="WriteGuard.AllowProjectFile"/>, the user's Write is that yes), never the
/// home's or a skill root's guard. Every answer that changed nothing is a <see cref="ToolNothingRanResult"/>, so <c>/undo</c> never lists it.
/// </summary>
public sealed class PresentProjectNotesTool : AIFunction, IRefusesPrunedStubs, IArgumentCeiling
{
    public const string ToolName = ProjectInitText.ToolName;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "content": { "type": "string", "description": "The whole notes file as Markdown, under 16,000 characters." }
          },
          "required": ["content"]
        }
        """);

    private readonly ProjectInitSession _session;
    private readonly WorkingDirectory _files;
    private readonly Func<ProjectNotesDraft, CancellationToken, Task<ProjectNotesVerdict?>> _review;
    private readonly Action<ProjectNotesVerdict, ProjectNotesDraft, WriteResult>? _written;

    /// <param name="review">Shows the draft and waits for the user's answer; null when nobody could be asked (no pane), which writes nothing.</param>
    /// <param name="written">Called after a Write that landed (or found the file unchanged, with an Ok result of 0 bytes): the screen's notice and Write and turn on.</param>
    public PresentProjectNotesTool(ProjectInitSession session, WorkingDirectory files, Func<ProjectNotesDraft, CancellationToken, Task<ProjectNotesVerdict?>> review, Action<ProjectNotesVerdict, ProjectNotesDraft, WriteResult>? written = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _written = written;
    }

    public override string Name => ToolName;

    public override string Description => ProjectInitText.ToolDescription;

    public override JsonElement JsonSchema => Schema;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> StubArguments { get; } = [ProjectInitText.ContentArgument];

    /// <summary>Twice the notes' cap and a margin, room for JSON's escapes: a 16k draft always gets through, a runaway stream is stopped.</summary>
    public int MaxArgumentChars => (2 * ProjectFile.MaxLength) + 2_000;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (_session.Start is not { } start)
        {
            return new ToolNothingRanResult(ProjectInitText.NotInPassResult);
        }

        if (_session.Answer != ProjectInitAnswer.None)
        {
            return new ToolNothingRanResult(ProjectInitText.AlreadyDoneResult(_session.Answer == ProjectInitAnswer.Written));
        }

        string content = ToolArguments.ReadString(arguments, ProjectInitText.ContentArgument);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ToolNothingRanResult(ProjectInitText.ContentRequired);
        }

        if (ConversationCompactor.IsPrunedStub(content))
        {
            return new ToolNothingRanResult(ProjectInitText.StubSentResult);
        }

        if (content.Length > ProjectFile.MaxLength)
        {
            return new ToolNothingRanResult(ProjectInitText.TooLongResult(content.Length));
        }

        if (_session.Moved(_files.Root))
        {
            return new ToolNothingRanResult(ProjectInitText.MovedResult);
        }

        var draft = Draft(_files, start.Target, content);
        _session.MarkPresented();
        var verdict = await _review(draft, cancellationToken).ConfigureAwait(false);
        if (verdict is null)
        {
            _session.Finish(ProjectInitAnswer.Canceled);
            return new ToolNothingRanResult(ProjectInitText.NoPaneResult);
        }

        DiagnosticLog.Info(ProjectFile.Category, ProjectInitText.VerdictLogLine(verdict.Choice, content.Length, draft.Unchanged));
        switch (verdict.Choice)
        {
            case ProjectNotesChoice.Cancel:
                _session.Finish(ProjectInitAnswer.Canceled);
                return new ToolNothingRanResult(ProjectInitText.CanceledResult);
            case ProjectNotesChoice.Revise:
                return new ToolNothingRanResult(ProjectInitText.ReviseResult(verdict.Feedback, _session.AskUser));
        }

        // Write, or Write and turn on. The folder checked again: the pane may have waited a long time.
        if (_session.Moved(_files.Root))
        {
            return new ToolNothingRanResult(ProjectInitText.MovedResult);
        }

        if (draft.Unchanged)
        {
            _session.Finish(ProjectInitAnswer.Written);
            _written?.Invoke(verdict, draft, new WriteResult(FileOutcome.Ok, start.Target, 0, false));
            return new ToolNothingRanResult(ProjectInitText.UnchangedResult);
        }

        WriteResult wrote;
        using (WriteGuard.AllowProjectFile())
        {
            wrote = _files.WriteText(start.Target, content, overwrite: true);
        }

        if (wrote.Outcome != FileOutcome.Ok)
        {
            // Not an answer: the user can fix the cause and run /project-init again.
            string sentence = FileText.Wrote(wrote);
            DiagnosticLog.Info(ProjectFile.Category, ProjectInitText.WriteFailedLogLine(sentence));
            _written?.Invoke(verdict, draft, wrote);
            return new ToolNothingRanResult(ProjectInitText.WriteFailedResult(sentence));
        }

        _session.Finish(ProjectInitAnswer.Written);
        _written?.Invoke(verdict, draft, wrote);
        return ToolDiffResult.Of(ProjectInitText.WrittenResult(start.Target), wrote.Diff);
    }

    /// <summary>
    /// The draft against the file as it is now (2026-11-23): its text (null when there is none), what would change, whether nothing would (line
    /// endings aside, <see cref="FileDiff.Of"/>), and the lines that look like secrets.
    /// </summary>
    public static ProjectNotesDraft Draft(WorkingDirectory files, string target, string content)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);
        string? old = File.Exists(Path.Combine(files.Root, target)) && files.ReadText(target, null, null, WorkingDirectory.MaxWriteChars) is { Outcome: FileOutcome.Ok } read
            ? read.Text
            : null;
        // Both sides with \n endings and no final newline: Write keeps the file's own endings, and ReadText's lines come back without the
        // last one, so a CRLF file and an LF draft saying the same are no change.
        static string Lines(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n');
        var diff = old is null ? null : FileDiff.Of(target, Lines(old), Lines(content));
        return new ProjectNotesDraft(target, content, old, diff, Unchanged: old is not null && diff is null)
        {
            SecretLines = ProjectSecrets.Lines(content),
        };
    }
}
