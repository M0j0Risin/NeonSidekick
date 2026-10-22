using NeonSidekick.Plans;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>A checkpoint's answer (2026-10-18).</summary>
public enum RalphCheckpointChoice
{
    Keep,
    Redo,
    Stop,
}

/// <summary>What the user made of a step at the checkpoint: the choice, and Redo's note (null for none).</summary>
public sealed record RalphCheckpointAnswer(RalphCheckpointChoice Choice, string? Note);

/// <summary>
/// Which of the loop's questions the pane asks (2026-10-21, the plan at <c>~/.claude/plans/i-put-you-in-snoopy-perlis.md</c>): the checkpoint after
/// N steps that stood, or the by-hand question on a step only a person can check.
/// </summary>
public enum RalphAskKind
{
    /// <summary>Keep this step? Esc keeps it.</summary>
    Checkpoint,

    /// <summary>Does it work? Esc stops: a step nobody checked is never ticked by accident.</summary>
    ByHand,
}

/// <summary>
/// The Ralph loop's checkpoint pane (2026-10-18, <c>Ralph loop checkpoint</c> above 0): after a step that stood, Keep, Redo with a note… (the
/// pane's input slot for what the next pass should do differently), View the changes (the pass's diff on an info page, then back), or Stop the
/// loop, with <c>k</c> / <c>r</c> / <c>v</c> / <c>s</c> as hotkeys and the cursor on Keep. Esc is Keep: the loop goes on. The
/// <see cref="PlanApprovalMenu"/> shape; the page asks the same at once (<c>ChatScreen</c>'s race), the first answer winning. Since 2026-10-21 it asks
/// the by-hand question too (<see cref="RalphAskKind.ByHand"/>): its own title, rows and hint, <c>w</c> for "it works", the cursor on View, and Esc,
/// a closed pane or a cancel is Stop, never Keep.
/// </summary>
public sealed class RalphCheckpointMenu
{
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly RalphAskKind _kind;

    public RalphCheckpointMenu(MenuPane pane, InputLine input, RalphAskKind kind = RalphAskKind.Checkpoint)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _kind = kind;
    }

    /// <summary>The hotkeys: the rows' letters.</summary>
    public static readonly IReadOnlyDictionary<char, int> Hotkeys = new Dictionary<char, int> { ['k'] = 0, ['r'] = 1, ['v'] = 2, ['s'] = 3 };

    /// <summary>The by-hand question's hotkeys (2026-10-21): <c>w</c> for "it works".</summary>
    public static readonly IReadOnlyDictionary<char, int> ByHandHotkeys = new Dictionary<char, int> { ['w'] = 0, ['r'] = 1, ['v'] = 2, ['s'] = 3 };

    private const int KeepRow = 0;
    private const int RedoRow = 1;
    private const int ViewRow = 2;
    private const int StopRow = 3;

    /// <summary>The page with <paramref name="caption"/> (the step and its summary).</summary>
    public static MenuPage Page(string caption) =>
        new(RalphText.CheckpointTitle, RalphText.CheckpointRows.Select(Markup.Escape).ToList(), RalphText.CheckpointKeys) { Caption = caption, Hotkeys = Hotkeys };

    /// <summary>The page for <paramref name="kind"/>: the by-hand question's lets its caption grow, the check leading it.</summary>
    public static MenuPage Page(string caption, RalphAskKind kind) => kind == RalphAskKind.ByHand
        ? new(RalphText.ByHandTitle, RalphText.ByHandRows.Select(Markup.Escape).ToList(), RalphText.ByHandKeys) { Caption = caption, Hotkeys = ByHandHotkeys, CaptionGrows = true }
        : Page(caption);

    /// <summary>
    /// Asks until answered; Esc and a closed pane are Keep at the checkpoint, Stop at the by-hand question (2026-10-21). Null with no pane to draw on.
    /// </summary>
    public async Task<RalphCheckpointAnswer?> AskAsync(string caption, string diff, CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var unanswered = new RalphCheckpointAnswer(_kind == RalphAskKind.ByHand ? RalphCheckpointChoice.Stop : RalphCheckpointChoice.Keep, null);
        var rows = _kind == RalphAskKind.ByHand ? RalphText.ByHandRows : RalphText.CheckpointRows;
        // The by-hand question opens on View the changes (2026-10-21, a review: on "It works", an Enter typed for the input row as the pane came up
        // ticked a step nobody checked); the checkpoint keeps its cursor on Keep, Esc's answer there anyway.
        int start = _kind == RalphAskKind.ByHand ? ViewRow : KeepRow;
        try
        {
            var page = Page(caption, _kind);
            while (true)
            {
                var pick = await _pane.PickAsync(page, start, cancellationToken).ConfigureAwait(false);
                switch (pick?.Row)
                {
                    case RedoRow:
                        var typed = await _pane.EditAsync(page with { Hint = PlanText.FeedbackKeys, Caption = RalphText.RedoPrompt }, RedoRow, _input, "", allowEmpty: true, cancellationToken).ConfigureAwait(false);
                        if (typed is InputResult.Submitted submitted)
                        {
                            string note = submitted.Text.ReplaceLineEndings(" ").Trim();
                            return new RalphCheckpointAnswer(RalphCheckpointChoice.Redo, note.Length == 0 ? null : note);
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            return unanswered;
                        }

                        continue;
                    case ViewRow:
                        var lines = (diff.Length == 0 ? RalphText.NoChangesLine : diff).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        var view = new MenuPage(rows[ViewRow], lines.Select(Markup.Escape).ToList(), MenuPane.EmptyKeys);
                        await _pane.PickAsync(view, 0, cancellationToken).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return unanswered;
                        }

                        continue;
                    case StopRow:
                        return new RalphCheckpointAnswer(RalphCheckpointChoice.Stop, null);
                    case KeepRow:
                        return new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null);
                    default:
                        return unanswered;
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
