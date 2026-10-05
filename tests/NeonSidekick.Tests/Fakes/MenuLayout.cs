using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// How a settings tab's rows print on a pane at a given width (2026-10-04, the UI review's sections and footer): the tests' one
/// place for the heading rules and the footer under the list, so a pinned layout reads as the pane draws it.
/// </summary>
internal static class MenuLayout
{
    /// <summary>A section heading as the pane prints it at <paramref name="width"/> columns: <c>── Title</c>, a space, the rule to the edge.</summary>
    public static string Heading(string title, int width)
    {
        string head = SectionRule.Lead + " " + title;
        return head + " " + new string(ScreenPane.RuleGlyph, Math.Max(0, width - TextCells.Width(head) - 1));
    }

    /// <summary>
    /// The footer's <see cref="MenuPane.FooterRows"/> rows as printed under the list at <paramref name="width"/> columns for the cursor on
    /// <paramref name="field"/> (null for a row with none: blank rows), each ending in a line break.
    /// </summary>
    public static string Footer(SettingsField? field, int width) => Footer(field is { } f ? SettingsMenu.FieldFooter(f) : null, width);

    /// <summary>The footer's rows for any <paramref name="footer"/> (a tool's or a skill's, 2026-10-05), as <see cref="Footer(SettingsField?, int)"/>.</summary>
    public static string Footer(MenuFooter? footer, int width)
    {
        var lines = footer is { } f ? MenuPane.FooterLines(f, width - TextCells.Width(MenuPane.NoPointer)) : [];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < MenuPane.FooterRows; i++)
        {
            sb.Append(i < lines.Count ? MenuPane.NoPointer + lines[i] : " ").Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Blank footer rows: a row that says nothing under the list (the servers, a heading, the off line).</summary>
    public static string BlankFooter => Footer((MenuFooter?)null, 1);
}
