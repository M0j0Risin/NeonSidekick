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
/// The Ralph loop's checkpoint pane (2026-10-18, <c>LLM Ralph loop checkpoint</c> above 0): after a step that stood, Keep, Redo with a note… (the
/// pane's input slot for what the next pass should do differently), View the changes (the pass's diff on an info page, then back), or Stop the
/// loop, with <c>k</c> / <c>r</c> / <c>v</c> / <c>s</c> as hotkeys and the cursor on Keep. Esc is Keep: the loop goes on. The
/// <see cref="PlanApprovalMenu"/> shape; the page asks the same at once (<c>ChatScreen</c>'s race), the first answer winning.
/// </summary>
public sealed class RalphCheckpointMenu
{
    private readonly MenuPane _pane;
    private readonly InputLine _input;

    public RalphCheckpointMenu(MenuPane pane, InputLine input)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>The hotkeys: the rows' letters.</summary>
    public static readonly IReadOnlyDictionary<char, int> Hotkeys = new Dictionary<char, int> { ['k'] = 0, ['r'] = 1, ['v'] = 2, ['s'] = 3 };

    private const int KeepRow = 0;
    private const int RedoRow = 1;
    private const int ViewRow = 2;
    private const int StopRow = 3;

    /// <summary>The page with <paramref name="caption"/> (the step and its summary).</summary>
    public static MenuPage Page(string caption) =>
        new(RalphText.CheckpointTitle, RalphText.CheckpointRows.Select(Markup.Escape).ToList(), RalphText.CheckpointKeys) { Caption = caption, Hotkeys = Hotkeys };

    /// <summary>Asks until answered; Esc and a closed pane are Keep. Null with no pane to draw on.</summary>
    public async Task<RalphCheckpointAnswer?> AskAsync(string caption, string diff, CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            var page = Page(caption);
            while (true)
            {
                var pick = await _pane.PickAsync(page, KeepRow, cancellationToken).ConfigureAwait(false);
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
                            return new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null);
                        }

                        continue;
                    case ViewRow:
                        var lines = (diff.Length == 0 ? RalphText.NoChangesLine : diff).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        var view = new MenuPage(RalphText.CheckpointRows[ViewRow], lines.Select(Markup.Escape).ToList(), MenuPane.EmptyKeys);
                        await _pane.PickAsync(view, 0, cancellationToken).ConfigureAwait(false);
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null);
                        }

                        continue;
                    case StopRow:
                        return new RalphCheckpointAnswer(RalphCheckpointChoice.Stop, null);
                    default:
                        return new RalphCheckpointAnswer(RalphCheckpointChoice.Keep, null);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
