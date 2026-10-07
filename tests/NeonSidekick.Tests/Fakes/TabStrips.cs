using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The screen tests' view of a pane's strip without its badges (2026-10-07). A settings tab's badge counts the rows that differ
/// from the default (<c>SettingsMenu.ChangedBadge</c>), the fixtures set rows of their own (an HTTP voice, scratch folders, tools
/// on), and many tests change a row between two draws, so a strip's badges move under the tests that pin everything else on it.
/// The badges themselves are pinned on their own (<c>InfoPaneTests.ABadge_HugsItsTitle_AndCountsInTheLayoutAndTheClick</c>,
/// <c>SettingsMenuTests.FieldsTab_Badge_CountsTheRowsThatDiffer</c>).
/// </summary>
public static class TabStrips
{
    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>
    /// <paramref name="output"/> with the badges taken off every strip row (a row holding the label's bar), each one's cells put
    /// back as spaces before the row's × so the row keeps its width and the × its column, as an unbadged strip draws it.
    /// </summary>
    public static string Unbadged(string output)
    {
        if (output.AsSpan().IndexOfAny(Superscripts) < 0)
        {
            return output;
        }

        var lines = output.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (!line.Contains(InfoPane.LabelSeparator + " ", StringComparison.Ordinal) || line.AsSpan().IndexOfAny(Superscripts) < 0)
            {
                continue;
            }

            string bare = string.Concat(line.Where(c => !Superscripts.Contains(c)));
            int taken = line.Length - bare.Length;
            int close = bare.LastIndexOf(ScreenPane.CloseGlyph, StringComparison.Ordinal);
            lines[i] = close < 0 ? bare : bare[..close] + new string(' ', taken) + bare[close..];
        }

        return string.Join('\n', lines);
    }
}
