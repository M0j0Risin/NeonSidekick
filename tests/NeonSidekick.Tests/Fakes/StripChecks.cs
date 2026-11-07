using NeonSidekick.UI;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests.Fakes;

/// <summary>Checks on a strip as drawn against its layout (2026-11-07, the code review's long labels).</summary>
internal static class StripChecks
{
    /// <summary>The <c>/tasks</c> folders from the reproduction: the root, the one whose strip broke, and a task's path under it.</summary>
    public static readonly string[] TaskFolders =
    [
        @"\",
        @"\Microsoft\Windows\Customer Experience Improvement Program",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
    ];

    /// <summary>The widths the strips are tried at: the reproduction's 70–130.</summary>
    public static readonly int[] Widths = [70, 75, 80, 85, 90, 95, 100, 110, 120, 130];

    /// <summary>
    /// <paramref name="rows"/> drawn as the panes draw a strip (one <see cref="FittedMarkup"/> each, at <paramref name="width"/>): as many as the
    /// layout counts, each within the window, and every title at the column the layout gives it, so a click there lands on it.
    /// </summary>
    public static void DrawnAsLaidOut(IReadOnlyList<string> rows, (int Rows, (int Row, int Column)[] Places) layout, IReadOnlyList<string> titles, int width)
    {
        using var console = new TestConsole();
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        var drawn = rows.Select(r => string.Concat(new FittedMarkup(r).Render(options, width).Select(s => s.Text))).ToList();
        Assert.Equal(layout.Rows, drawn.Count);
        Assert.All(drawn, d => Assert.True(TextCells.Width(d) <= width, $"{width}: {d}"));
        for (int i = 0; i < titles.Count; i++)
        {
            var (row, column) = layout.Places[i];
            Assert.StartsWith(" " + titles[i] + " ", FromColumn(drawn[row], column), StringComparison.Ordinal);
        }
    }

    /// <summary>What <paramref name="text"/> draws from cell <paramref name="column"/> on.</summary>
    private static string FromColumn(string text, int column)
    {
        int i = 0;
        int cells = 0;
        while (i < text.Length && cells < column)
        {
            cells += TextCells.ElementWidth(text, i, out int length);
            i += length;
        }

        return text[i..];
    }
}
