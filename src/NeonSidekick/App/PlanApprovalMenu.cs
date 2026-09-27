using NeonSidekick.Plans;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The plan's approval pane (2026-09-26): what <c>present_plan</c> puts to the user once the plan is
/// saved and printed — the title, path and revision as the caption, four rows (<see cref="Choices"/>:
/// Approve &amp; run, Approve with a clear context, Keep refining, Cancel) with <c>a</c> / <c>f</c> / <c>r</c> /
/// <c>c</c> as hotkeys and the cursor on Keep refining: an Enter while a reply streams must never
/// approve, the <see cref="CommandApprovalMenu"/> shape. Keep refining opens the pane's input slot for
/// what should change (<see cref="MenuPane.EditAsync"/>, the question pane's Other row); ESC in the slot
/// goes back to the rows. ESC on the rows, the token and no keyboard are Keep refining with nothing
/// said. Runs on the watcher task through <see cref="ChatScreen"/>'s pane request.
/// </summary>
public sealed class PlanApprovalMenu
{
    private readonly MenuPane _pane;
    private readonly InputLine _input;

    public PlanApprovalMenu(MenuPane pane, InputLine input)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>The hotkeys: the rows' letters, lower case.</summary>
    public static readonly IReadOnlyDictionary<char, int> Hotkeys = new Dictionary<char, int> { ['a'] = 0, ['f'] = 1, ['r'] = 2, ['c'] = 3 };

    /// <summary>The choice each row stands for, by index.</summary>
    public static readonly IReadOnlyList<PlanChoice> Choices = [PlanChoice.Approve, PlanChoice.ApproveFresh, PlanChoice.Refine, PlanChoice.Cancel];

    /// <summary>The row the cursor starts on: Keep refining.</summary>
    public const int StartRow = 2;

    /// <summary>The four rows as markup, escaped.</summary>
    public static IReadOnlyList<string> Rows { get; } =
    [
        Markup.Escape(PlanText.ApproveRow),
        Markup.Escape(PlanText.ApproveFreshRow),
        Markup.Escape(PlanText.RefineRow),
        Markup.Escape(PlanText.CancelRow),
    ];

    /// <summary>The page for <paramref name="plan"/>.</summary>
    public static MenuPage Page(PlanPresentation plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new MenuPage(PlanText.ApprovalTitle, Rows, PlanText.ApprovalKeys)
        {
            Caption = PlanText.Caption(plan),
            Hotkeys = Hotkeys,
        };
    }

    /// <summary>Shows the question and reads the verdict; null with no pane to draw on.</summary>
    public async Task<PlanVerdict?> AskAsync(PlanPresentation plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            var page = Page(plan);
            while (true)
            {
                var pick = await _pane.PickAsync(page, StartRow, cancellationToken).ConfigureAwait(false);
                if (pick is not { } picked || picked.Row < 0 || picked.Row >= Choices.Count)
                {
                    return new PlanVerdict(PlanChoice.Refine);
                }

                var choice = Choices[picked.Row];
                if (choice != PlanChoice.Refine)
                {
                    return new PlanVerdict(choice);
                }

                var typed = await _pane.EditAsync(page with { Hint = PlanText.FeedbackKeys, Caption = PlanText.FeedbackPrompt }, StartRow, _input, "", allowEmpty: true, cancellationToken).ConfigureAwait(false);
                if (typed is InputResult.Submitted submitted)
                {
                    string feedback = submitted.Text.ReplaceLineEndings(" ").Trim();
                    return new PlanVerdict(PlanChoice.Refine, feedback.Length == 0 ? null : feedback);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new PlanVerdict(PlanChoice.Refine);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
