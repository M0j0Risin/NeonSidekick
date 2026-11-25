using NeonSidekick.Shell;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The approval pane (2026-09-21): the question the gate puts to the user before a command runs
/// — the command as its caption, up to four rows (<see cref="Rows"/>: Deny, Allow once, Allow the
/// prefixes for the session, Allow them always; since 2026-11-24 a row that would hold nothing is hidden, and a script has no Always) with <c>d</c> / <c>o</c> / <c>s</c> / <c>a</c> as hotkeys
/// and the cursor on Deny, the <see cref="SettingsMenu.ConfirmAsync"/> shape: an Enter while a
/// reply streams must never approve. ESC, the token and no keyboard are Deny. The pane closes with
/// the answer. Runs on the watcher task through <see cref="ChatScreen"/>'s pane request, like the
/// question pane.
/// <para>The <see cref="ShellText.ViewButton"/> on its title row (2026-10-07, the user's ask: the caption is cut at three rows, a script
/// shown by its first line) opens the whole command in a scrolling view (<see cref="ShellText.WholeCommandLines"/>, a script's lines
/// numbered); ESC there comes back to the question with the cursor where it was.</para>
/// </summary>
public sealed class CommandApprovalMenu
{
    private readonly MenuPane _pane;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task>? _view;

    /// <param name="view">Shows lines in a scrolling view under a label (the screen's info pane); null offers no view.</param>
    public CommandApprovalMenu(MenuPane pane, Func<string, IReadOnlyList<string>, CancellationToken, Task>? view = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _view = view;
    }

    /// <summary>The title row's button: the whole command.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons = [new(ShellText.ViewButton, ShellText.ViewKey)];

    /// <summary>
    /// The choice each row shown stands for, by index (2026-11-24, the session log review): Deny and Once always; Session when something on the
    /// line can be held, Always when something can be saved (never for a script). Per request, so a hidden Session row can't shift Always onto its index.
    /// </summary>
    public static IReadOnlyList<CommandChoice> ChoicesFor(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var choices = new List<CommandChoice> { CommandChoice.Deny, CommandChoice.Once };
        if (ShellText.ShowsSessionRow(request))
        {
            choices.Add(CommandChoice.Session);
        }

        if (ShellText.ShowsPermanentRow(request))
        {
            choices.Add(CommandChoice.Permanent);
        }

        return choices;
    }

    /// <summary>The hotkeys of the rows shown: the first letter of each, lower case (<c>d</c> / <c>o</c> / <c>s</c> / <c>a</c>).</summary>
    public static IReadOnlyDictionary<char, int> HotkeysFor(CommandRequest request)
    {
        var choices = ChoicesFor(request);
        var keys = new Dictionary<char, int>();
        for (int i = 0; i < choices.Count; i++)
        {
            keys[choices[i] switch { CommandChoice.Deny => 'd', CommandChoice.Once => 'o', CommandChoice.Session => 's', _ => 'a' }] = i;
        }

        return keys;
    }

    /// <summary>The rows shown as markup, escaped (<see cref="ChoicesFor"/>'s order). Pinned.</summary>
    public static IReadOnlyList<string> Rows(CommandRequest request) =>
        ChoicesFor(request).Select(choice => Markup.Escape(choice switch
        {
            CommandChoice.Deny => ShellText.DenyRow,
            CommandChoice.Once => ShellText.OnceRow,
            CommandChoice.Session => ShellText.SessionRow(request),
            _ => ShellText.PermanentRow(request),
        })).ToList();

    /// <summary>The page for <paramref name="request"/>: the title by kind, the command as the caption, the rows, the keys, and the view's button.</summary>
    public static MenuPage Page(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MenuPage(request.IsScript ? ShellText.ScriptApprovalTitle : ShellText.ApprovalTitle, Rows(request), ShellText.ApprovalKeysFor(request))
        {
            Caption = ShellText.Caption(request),
            Hotkeys = HotkeysFor(request),
            Buttons = Buttons,
        };
    }

    /// <summary>Shows the question and reads one pick: the row's choice, <see cref="CommandChoice.Deny"/> for ESC (or the token: the turn is over then, the answer moot), null with no pane to draw on.</summary>
    public async Task<CommandChoice?> AskAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_pane.Enabled)
        {
            return null;
        }

        var choices = ChoicesFor(request);
        try
        {
            int cursor = 0;
            while (true)
            {
                var pick = await _pane.PickAsync(Page(request), cursor, cancellationToken).ConfigureAwait(false);
                if (pick is { Button: >= 0 } viewing)
                {
                    // The whole command (2026-10-07), then the question again, the cursor where it was.
                    cursor = viewing.Row;
                    if (_view is not null)
                    {
                        await _view(ShellText.WholeCommandLabel(request), ShellText.WholeCommandLines(request), cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                return pick is { } picked && picked.Row >= 0 && picked.Row < choices.Count ? choices[picked.Row] : CommandChoice.Deny;
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
