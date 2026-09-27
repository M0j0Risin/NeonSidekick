using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The panes of <c>/skills add</c> (2026-09-26): a pick among search hits or among the skills of
/// one repository (<see cref="PickAsync"/>), and the install question (<see cref="AskAsync"/>) — to
/// the profile's skills, the global ones, or cancel; for a skill installed before from the same
/// source, replace it where it is, or cancel. The <see cref="PlanApprovalMenu"/> shape, with the
/// cursor on Cancel — an Enter must never write
/// someone else's scripts into the skills by accident (<see cref="CommandApprovalMenu"/>'s rule),
/// so <c>--global</c> / <c>--profile</c> are not consulted here (they pick headless's scope and the
/// typed question's). ESC, the token and no pane are cancel.
/// </summary>
public sealed class SkillInstallMenu
{
    private readonly MenuPane _pane;

    public SkillInstallMenu(MenuPane pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    public static readonly IReadOnlyDictionary<char, int> InstallHotkeys = new Dictionary<char, int> { ['p'] = 0, ['g'] = 1, ['c'] = 2 };
    public static readonly IReadOnlyDictionary<char, int> UpdateHotkeys = new Dictionary<char, int> { ['u'] = 0, ['c'] = 1 };

    /// <summary>The install page for a new skill: profile, global, cancel — the cursor on Cancel.</summary>
    public static MenuPage InstallPage(SkillCandidate candidate, SkillSource source) =>
        new(Markup.Escape(SkillInstallText.InstallTitle(candidate.Name)),
            [Markup.Escape(SkillInstallText.ProfileRow), Markup.Escape(SkillInstallText.GlobalRow), Markup.Escape(SkillInstallText.CancelRow)],
            SkillInstallText.InstallKeys)
        {
            Caption = SkillInstallText.InstallCaption(candidate, source),
            Hotkeys = InstallHotkeys,
        };

    /// <summary>The page for an update in <paramref name="scope"/>: replace, cancel — the cursor on Cancel.</summary>
    public static MenuPage UpdatePage(SkillCandidate candidate, SkillSource source, SkillScope scope) =>
        new(Markup.Escape(SkillInstallText.InstallTitle(candidate.Name)),
            [Markup.Escape(SkillInstallText.UpdateRow(scope)), Markup.Escape(SkillInstallText.CancelRow)],
            SkillInstallText.UpdateKeys)
        {
            Caption = SkillInstallText.InstallCaption(candidate, source),
            Hotkeys = UpdateHotkeys,
        };

    /// <summary>One of <paramref name="rows"/>, or null (ESC, the token, no pane).</summary>
    public async Task<int?> PickAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(rows);
        if (!_pane.Enabled || rows.Count == 0)
        {
            return null;
        }

        try
        {
            var page = new MenuPage(Markup.Escape(title), rows.Select(Markup.Escape).ToList(), SkillInstallText.PickKeys);
            var pick = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
            return pick is { } picked && picked.Row >= 0 && picked.Row < rows.Count ? picked.Row : null;
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>The scope to install to, or null to keep the skill out.</summary>
    public async Task<SkillScope?> AskAsync(SkillCandidate candidate, SkillSource source, SkillInstallCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(check);
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            if (check.Option == SkillInstallOption.Update && check.Scope is { } scope)
            {
                var update = await _pane.PickAsync(UpdatePage(candidate, source, scope), 1, cancellationToken).ConfigureAwait(false);
                return update is { Row: 0 } ? scope : null;
            }

            var pick = await _pane.PickAsync(InstallPage(candidate, source), 2, cancellationToken).ConfigureAwait(false);
            return pick?.Row switch
            {
                0 => SkillScope.Profile,
                1 => SkillScope.Global,
                _ => null,
            };
        }
        finally
        {
            _pane.Close();
        }
    }
}
