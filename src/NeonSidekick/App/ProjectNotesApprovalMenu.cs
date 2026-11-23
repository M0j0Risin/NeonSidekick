using NeonSidekick.ProjectInit;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>/project-init</c>'s Write pane (2026-11-23, the <see cref="PlanApprovalMenu"/> shape): the caption (<see cref="ProjectInitText.CaptionLines"/>,
/// the same lines the page shows) and Write / Write and turn on (only when something keeps the notes out of the prompt) / View / Revise… /
/// Cancel, with <c>w</c> / <c>t</c> / <c>v</c> / <c>r</c> / <c>c</c>. The cursor starts on View, so an Enter typed ahead never writes. View
/// opens the change (or the whole draft for a new file) in the info pane and comes back to the rows; Revise… opens the pane's input slot (Esc
/// there goes back to the rows, an empty line is still a Revise). Esc on the rows, the token and no keyboard are Cancel.
/// </summary>
public sealed class ProjectNotesApprovalMenu
{
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly Func<ProjectNotesDraft, CancellationToken, Task> _view;

    /// <param name="view">Opens the draft's change, or its whole text, in the info pane; returns when it is closed.</param>
    public ProjectNotesApprovalMenu(MenuPane pane, InputLine input, Func<ProjectNotesDraft, CancellationToken, Task> view)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    /// <summary>What each row stands for, by index, with Write and turn on or without.</summary>
    public static IReadOnlyList<ProjectNotesRow> RowsFor(bool turnOn) => turnOn
        ? [ProjectNotesRow.Write, ProjectNotesRow.WriteAndTurnOn, ProjectNotesRow.View, ProjectNotesRow.Revise, ProjectNotesRow.Cancel]
        : [ProjectNotesRow.Write, ProjectNotesRow.View, ProjectNotesRow.Revise, ProjectNotesRow.Cancel];

    /// <summary>The page for <paramref name="draft"/>: <paramref name="turnOn"/> names what Write and turn on turns on, null for no such row.</summary>
    public static MenuPage Page(ProjectNotesDraft draft, IReadOnlyList<string> caption, string? turnOn)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(caption);
        var rows = RowsFor(turnOn is not null);
        var hotkeys = new Dictionary<char, int>();
        for (int i = 0; i < rows.Count; i++)
        {
            hotkeys[rows[i] switch
            {
                ProjectNotesRow.Write => 'w',
                ProjectNotesRow.WriteAndTurnOn => 't',
                ProjectNotesRow.View => 'v',
                ProjectNotesRow.Revise => 'r',
                _ => 'c',
            }] = i;
        }

        return new MenuPage(ProjectInitText.NotesTitle, rows.Select(r => Markup.Escape(Label(r, draft.Target, turnOn))).ToList(), ProjectInitText.NotesKeys)
        {
            Caption = string.Join("\n", caption),
            CaptionGrows = true,
            CaptionKeepLines = true,
            Hotkeys = hotkeys,
        };
    }

    /// <summary>A row's words.</summary>
    public static string Label(ProjectNotesRow row, string target, string? turnOn) => row switch
    {
        ProjectNotesRow.Write => ProjectInitText.WriteRow(target),
        ProjectNotesRow.WriteAndTurnOn => ProjectInitText.WriteAndTurnOnRow(target, turnOn ?? ""),
        ProjectNotesRow.View => ProjectInitText.ViewRow,
        ProjectNotesRow.Revise => ProjectInitText.ReviseRow,
        _ => ProjectInitText.NotesCancelRow,
    };

    /// <summary>Shows the draft and reads the answer; null with no pane to draw on.</summary>
    public async Task<ProjectNotesVerdict?> AskAsync(ProjectNotesDraft draft, IReadOnlyList<string> caption, string? turnOn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            var page = Page(draft, caption, turnOn);
            var rows = RowsFor(turnOn is not null);
            int start = rows.ToList().IndexOf(ProjectNotesRow.View);
            int cursor = start;
            while (true)
            {
                var pick = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (pick is not { } picked || picked.Row < 0 || picked.Row >= rows.Count)
                {
                    return new ProjectNotesVerdict(ProjectNotesChoice.Cancel);
                }

                cursor = picked.Row;
                switch (rows[picked.Row])
                {
                    case ProjectNotesRow.Write:
                        return new ProjectNotesVerdict(ProjectNotesChoice.Write);
                    case ProjectNotesRow.WriteAndTurnOn:
                        return new ProjectNotesVerdict(ProjectNotesChoice.WriteAndTurnOn);
                    case ProjectNotesRow.Cancel:
                        return new ProjectNotesVerdict(ProjectNotesChoice.Cancel);
                    case ProjectNotesRow.View:
                        await _view(draft, cancellationToken).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return new ProjectNotesVerdict(ProjectNotesChoice.Cancel);
                        }

                        continue;
                }

                // Revise…: the words, or none (still a Revise); Esc in the slot goes back to the rows.
                var typed = await _pane.EditAsync(page with { Hint = ProjectInitText.FeedbackKeys, Caption = ProjectInitText.FeedbackPrompt, CaptionKeepLines = false }, cursor, _input, "", allowEmpty: true, cancellationToken).ConfigureAwait(false);
                if (typed is InputResult.Submitted submitted)
                {
                    string feedback = submitted.Text.ReplaceLineEndings(" ").Trim();
                    return new ProjectNotesVerdict(ProjectNotesChoice.Revise, feedback.Length == 0 ? null : feedback);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new ProjectNotesVerdict(ProjectNotesChoice.Cancel);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}

/// <summary>A row of the Write pane (2026-11-23).</summary>
public enum ProjectNotesRow
{
    Write,
    WriteAndTurnOn,
    View,
    Revise,
    Cancel,
}
