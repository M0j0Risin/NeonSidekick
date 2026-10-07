using NeonSidekick.Shell;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The approval pane (2026-09-21): the question the gate puts to the user before a command runs
/// — the command as its caption, four rows (<see cref="Rows"/>: Deny, Allow once, Allow the
/// prefixes for the session, Allow them always) with <c>d</c> / <c>o</c> / <c>s</c> / <c>a</c> as hotkeys
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

    /// <summary>The hotkeys: the first letter of each row, lower case.</summary>
    public static readonly IReadOnlyDictionary<char, int> Hotkeys = new Dictionary<char, int> { ['d'] = 0, ['o'] = 1, ['s'] = 2, ['a'] = 3 };

    /// <summary>The choice each row stands for, by index.</summary>
    public static readonly IReadOnlyList<CommandChoice> Choices = [CommandChoice.Deny, CommandChoice.Once, CommandChoice.Session, CommandChoice.Permanent];

    /// <summary>The four rows as markup, escaped. Pinned.</summary>
    public static IReadOnlyList<string> Rows(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return
        [
            Markup.Escape(ShellText.DenyRow),
            Markup.Escape(ShellText.OnceRow),
            Markup.Escape(ShellText.SessionRow(request)),
            Markup.Escape(ShellText.PermanentRow(request)),
        ];
    }

    /// <summary>The page for <paramref name="request"/>: the title by kind, the command as the caption, the rows, the keys, and the view's button.</summary>
    public static MenuPage Page(CommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MenuPage(request.IsScript ? ShellText.ScriptApprovalTitle : ShellText.ApprovalTitle, Rows(request), ShellText.ApprovalKeys)
        {
            Caption = ShellText.Caption(request),
            Hotkeys = Hotkeys,
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

                return pick is { } picked && picked.Row >= 0 && picked.Row < Choices.Count ? Choices[picked.Row] : CommandChoice.Deny;
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
