using NeonSidekick.App;
using NeonSidekick.Shell;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The screen fixture's Unix helpers (2026-10-06, the macOS build).</summary>
public partial class ChatScreenTests
{
    /// <summary>
    /// A blank cell of the 240-wide toolbar for a twin's "the blanks open /settings" click: the middle of the widest run of blanks on
    /// the toolbar row drawn last (the row that starts with ⚙️), the second of two where the path, cut to fit, leaves only the two. The Windows tests click a fixed cell; a macOS temp path
    /// (<c>/var/folders/…/T/NeonSidekick.Tests/…/profiles/default/files</c>) reaches past it, and a click there opens the folders.
    /// One test on both systems reads it too since 2026-10-17 (the Mac pass): <c>ARightClickOnAToolbarGlyph_…_AndTheBlanksOpenTheSettings</c>.
    /// Read off the row since 2026-10-15: it was worked out from <see cref="ChatScreen.ToolbarStripFor(CommandPolicyMode, bool)"/>,
    /// which draws no dividers (three by default since 2026-10-13) and so fell short of the drawn strip, the more so once 🪵 joined
    /// it. Fails, rather than clicking a glyph or the path, should no run of two blanks be left.
    /// </summary>
    private int ToolbarBlank()
    {
        string row = Output.Split('\n').Last(l => l.StartsWith(ChatScreen.SettingsToolGlyph, StringComparison.Ordinal)).TrimEnd('\r');
        // The console's width alone (2026-10-17, the Mac pass): the test console runs the next row's text on after the path, and its
        // gaps were wider than the one before it.
        int at = 0, cells = 0;
        while (at < row.Length && (cells += TextCells.ElementWidth(row, at, out int step)) <= _console.Profile.Width)
        {
            at += step;
        }

        row = row[..at];

        int start = -1, length = 0;
        for (int i = 0; i < row.Length;)
        {
            if (row[i] != ' ')
            {
                i++;
                continue;
            }

            int run = i;
            while (i < row.Length && row[i] == ' ')
            {
                i++;
            }

            if (i < row.Length && i - run > length)
            {
                (start, length) = (run, i - run);   // a run that ends the row is past the path: not between the strip and it
            }
        }

        Assert.True(length >= 2, $"no blank between the glyphs and the working directory on the toolbar row: '{row}'");
        return TextCells.Width(row[..(start + length / 2)]);
    }
}
