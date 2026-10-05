namespace NeonSidekick.App;

/// <summary>
/// One row of <c>/help</c>'s Keys tab (<see cref="ChatScreen.KeyRows"/>, 2026-10-05, the user's pick): the key as the tab labels it
/// (<c>/keycheck</c> parses it), what it does, and the command it runs, null for a key that runs none (Ctrl+L, the kill switch).
/// The command sat in brackets at the end of the meaning until then; the tab gives it a column of its own.
/// </summary>
public sealed record KeyRow(string Key, string Meaning, string? Command = null)
{
    /// <summary>The meaning on one line, the command after it in brackets: what <c>/keycheck</c> and <c>neon_help</c> show. Pure.</summary>
    public string Text => Command is null ? Meaning : Meaning + " (" + Command + ")";

    /// <summary>The key and <see cref="Text"/>, the pair <see cref="Hotkeys.KeyCheck"/> and <see cref="Help.NeonHelp"/> read.</summary>
    public (string Key, string Meaning) Pair => (Key, Text);
}
