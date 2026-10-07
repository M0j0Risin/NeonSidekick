using NeonSidekick.Camera;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the user did on the shutter pane.</summary>
public enum CameraPaneAction
{
    /// <summary>Space, R, or Enter on the take row: take a photo (again).</summary>
    Snap,

    /// <summary>Enter on the send row: use the photo taken.</summary>
    Send,

    /// <summary>ESC, Ctrl+C, the cancel row, or no pane.</summary>
    Cancel,
}

/// <summary>What the allow pane answered (the <c>model</c> shutter).</summary>
public enum CameraAllow
{
    Deny,
    Once,
    Session,
}

/// <summary>What the shutter pane shows: its title, the prompt (the caption), the status line (or a failure in its place), whether a photo is taken and what sending it is called.</summary>
public sealed record CameraPaneView(string Title, string Prompt, string Status, bool HasShot, string SendRow, string? Error = null);

/// <summary>
/// The camera's panes (2026-10-02): the shutter pane — the prompt and a status line as its caption, Space (or the take row)
/// takes a photo, R takes it again, Enter on the send row uses it, ESC cancels — and the allow pane of the <c>model</c>
/// shutter (Deny / Allow once / Allow for this session, the cursor on Deny, the <see cref="CommandApprovalMenu"/> shape). The
/// pane only reports the keys; <see cref="ChatScreen"/> takes the photos, so a photo being taken keeps the pane up with its
/// status line saying so. Runs on the watcher task through the screen's pane request mid-turn, like the question pane.
/// </summary>
public sealed class CameraMenu
{
    private readonly MenuPane _pane;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task>? _view;

    /// <param name="view">Shows lines in a scrolling view under a label (the screen's info pane), for an allow page's whole text; null offers none.</param>
    public CameraMenu(MenuPane pane, Func<string, IReadOnlyList<string>, CancellationToken, Task>? view = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _view = view;
    }

    /// <summary>
    /// The allow page's hint with the whole-text view (2026-10-07, the consistency pass: a database write's statement is cut at the
    /// caption's three rows, as the shell approval's command was). Pinned.
    /// </summary>
    public const string AllowHintWithView = "d / o / s = pick · v = view · Enter = choose · ESC = deny";

    public bool Enabled => _pane.Enabled;

    /// <summary>The rows for a view, as markup: take (or send, then take again) and cancel. Pinned.</summary>
    public static IReadOnlyList<string> Rows(CameraPaneView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.HasShot
            ? [Markup.Escape(view.SendRow), Markup.Escape(CameraText.RetakeRow), Markup.Escape(CameraText.CancelRow)]
            : [Markup.Escape(CameraText.TakeRow), Markup.Escape(CameraText.CancelRow)];
    }

    /// <summary>The action a row stands for.</summary>
    public static CameraPaneAction ActionOf(CameraPaneView view, int row) => (view.HasShot, row) switch
    {
        (true, 0) => CameraPaneAction.Send,
        (true, 1) => CameraPaneAction.Snap,
        (false, 0) => CameraPaneAction.Snap,
        _ => CameraPaneAction.Cancel,
    };

    public static readonly IReadOnlyList<MenuButton> Buttons = [new(CameraText.SnapButton, ' '), new(CameraText.RetakeButton, 'r')];

    public static MenuPage Page(CameraPaneView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new MenuPage(view.Title, Rows(view), CameraText.PaneHint)
        {
            Caption = view.Prompt,
            Buttons = Buttons,
        };
    }

    /// <summary>
    /// Shows the view — its status (or failure) as the pane's one status line, the last one replaced — and reads one action;
    /// <see cref="CameraPaneAction.Cancel"/> for ESC, the token, or no pane. The pane stays up for the next view until <see cref="Close"/>.
    /// </summary>
    public async Task<CameraPaneAction> NextAsync(CameraPaneView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!_pane.Enabled)
        {
            return CameraPaneAction.Cancel;
        }

        _pane.ClearStatus();
        if (view.Error is { } error)
        {
            _pane.Error(error);
        }
        else
        {
            _pane.Notice(view.Status);
        }

        var pick = await _pane.PickAsync(Page(view), 0, cancellationToken).ConfigureAwait(false);
        return pick switch
        {
            null => CameraPaneAction.Cancel,
            { Button: >= 0 } => CameraPaneAction.Snap,
            { } picked => ActionOf(view, picked.Row),
        };
    }

    public void Close() => _pane.Close();

    public static readonly IReadOnlyDictionary<char, int> AllowHotkeys = new Dictionary<char, int> { ['d'] = 0, ['o'] = 1, ['s'] = 2 };

    public static readonly IReadOnlyList<CameraAllow> AllowChoices = [CameraAllow.Deny, CameraAllow.Once, CameraAllow.Session];

    /// <summary>The allow page: <paramref name="title"/> (the camera's when null; the screen's since 2026-10-04), the three rows, the prompt as its caption.</summary>
    public static MenuPage AllowPage(string prompt, string? title = null) =>
        new(title ?? CameraText.AllowTitle, [Markup.Escape(CameraText.DenyRow), Markup.Escape(CameraText.AllowOnceRow), Markup.Escape(CameraText.AllowSessionRow)], CameraText.AllowHint)
        {
            Caption = prompt,
            Hotkeys = AllowHotkeys,
        };

    /// <summary>
    /// The allow question: the row's answer, Deny for ESC or the token, null with no pane to draw on. The pane closes with the answer.
    /// With <paramref name="whole"/> (2026-10-07: a database write's statement) the title row carries the
    /// <see cref="Shell.ShellText.ViewButton"/> (<c>v</c>), which shows those lines under <paramref name="wholeLabel"/> in a scrolling view
    /// and comes back to the question with the cursor where it was.
    /// </summary>
    public async Task<CameraAllow?> AllowAsync(string prompt, CancellationToken cancellationToken, string? title = null, string? wholeLabel = null, IReadOnlyList<string>? whole = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (!_pane.Enabled)
        {
            return null;
        }

        try
        {
            var page = AllowPage(prompt, title);
            if (whole is not null && _view is not null)
            {
                page = page with { Hint = AllowHintWithView, Buttons = CommandApprovalMenu.Buttons };
            }

            int cursor = 0;
            while (true)
            {
                var pick = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (pick is { Button: >= 0 } viewing && whole is not null && _view is not null)
                {
                    cursor = viewing.Row;
                    await _view(wholeLabel ?? page.Title, whole, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return pick is { } picked && picked.Row >= 0 && picked.Row < AllowChoices.Count ? AllowChoices[picked.Row] : CameraAllow.Deny;
            }
        }
        finally
        {
            _pane.Close();
        }
    }
}
