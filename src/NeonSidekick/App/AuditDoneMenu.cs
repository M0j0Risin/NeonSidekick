using NeonSidekick.Audits;
using NeonSidekick.Plans;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the user made of a finished code audit (2026-10-18).</summary>
public enum AuditChoice
{
    /// <summary>Plan mode on the open findings.</summary>
    Fix,

    /// <summary>A fresh loop on the same report, its findings kept.</summary>
    More,

    /// <summary>The report kept as it is: Esc, a page's cancel, the cursor's first row.</summary>
    Keep,

    /// <summary>The report and its coverage file deleted, after a yes.</summary>
    Discard,
}

/// <summary>The user's answer to the done pane, with the reviewer More passes… picked.</summary>
public sealed record AuditVerdict(AuditChoice Choice, PlanReviewer? Reviewer = null);

/// <summary>What the done pane shows: the report's name and path, its title and caption, and whether a finding is open (the Fix row).</summary>
public sealed record AuditDone(string Name, string Path, string Title, string Caption, bool HasFix);

/// <summary>
/// The code audit's done pane (2026-10-18, <see cref="PlanApprovalMenu"/>'s shape): Fix via plan (only with a finding open), More passes…,
/// Keep report and Discard report, built per form from one list of entries (<see cref="Entries"/>), so the rows, the hotkeys, the choices,
/// the start row and the Web UI's choices never disagree. The cursor starts on Keep, the safe row (<see cref="StartRow"/>): an Enter pressed
/// out of habit never starts a plan or deletes. More passes… opens the reviewer page while advisors are offered (in the audit's words);
/// Esc there goes back. Esc on the rows, the token and no keyboard are Keep.
/// </summary>
public sealed class AuditDoneMenu
{
    private readonly MenuPane _pane;
    private readonly Func<bool> _advisorsOffered;

    /// <param name="advisorsOffered">Whether the turn would offer <c>neon_advisor</c>: the reviewer page is shown only then.</param>
    public AuditDoneMenu(MenuPane pane, Func<bool>? advisorsOffered = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _advisorsOffered = advisorsOffered ?? (() => false);
    }

    /// <summary>The rows of a form, each its text, hotkey, choice and the Web UI's id; Fix left out when nothing is open.</summary>
    public static IReadOnlyList<(string Row, char Key, AuditChoice Choice, string Id)> Entries(bool hasFix)
    {
        var entries = new List<(string, char, AuditChoice, string)>(4);
        if (hasFix)
        {
            entries.Add((AuditText.FixRow, 'p', AuditChoice.Fix, "fix"));
        }

        entries.Add((AuditText.MoreRow, 'g', AuditChoice.More, "more"));
        entries.Add((AuditText.KeepRow, 'k', AuditChoice.Keep, "keep"));
        entries.Add((AuditText.DiscardRow, 'd', AuditChoice.Discard, "discard"));
        return entries;
    }

    /// <summary>Keep's index in the form shown: 2 with the Fix row, 1 without.</summary>
    public static int StartRow(bool hasFix) => Entries(hasFix).ToList().FindIndex(e => e.Choice == AuditChoice.Keep);

    /// <summary>The page for <paramref name="done"/>.</summary>
    public static MenuPage Page(AuditDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var entries = Entries(done.HasFix);
        return new MenuPage(done.Title, entries.Select(e => Markup.Escape(e.Row)).ToList(), AuditText.DoneKeys(done.HasFix))
        {
            Caption = done.Caption,
            Hotkeys = entries.Select((e, i) => (e.Key, i)).ToDictionary(p => p.Key, p => p.i),
        };
    }

    /// <summary>The reviewer page More passes… opens.</summary>
    public static MenuPage ReviewerPage(AuditDone done)
    {
        ArgumentNullException.ThrowIfNull(done);
        return PlanApprovalMenu.ReviewerPage(AuditText.ReviewerTitle, AuditText.ReviewerRows, done.Caption);
    }

    /// <summary>Shows the pane and reads the choice; null with no pane to draw on.</summary>
    public async Task<AuditVerdict?> AskAsync(AuditDone done, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            var entries = Entries(done.HasFix);
            var page = Page(done);
            while (true)
            {
                var pick = await _pane.PickAsync(page, StartRow(done.HasFix), cancellationToken).ConfigureAwait(false);
                if (pick is not { } picked || picked.Row < 0 || picked.Row >= entries.Count)
                {
                    return new AuditVerdict(AuditChoice.Keep);
                }

                var choice = entries[picked.Row].Choice;
                if (choice != AuditChoice.More)
                {
                    return new AuditVerdict(choice);
                }

                if (!_advisorsOffered())
                {
                    return new AuditVerdict(AuditChoice.More, PlanReviewer.Model);
                }

                var reviewer = await _pane.PickAsync(ReviewerPage(done), 0, cancellationToken).ConfigureAwait(false);
                if (reviewer is { Row: >= 0 and <= (int)PlanReviewer.Confirm } who)
                {
                    return new AuditVerdict(AuditChoice.More, (PlanReviewer)who.Row);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new AuditVerdict(AuditChoice.Keep);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
