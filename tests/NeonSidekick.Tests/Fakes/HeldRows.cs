using System.Text.RegularExpressions;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The pane's held height in a pinned layout (2026-10-14, ScreenPane: a titled overlay keeps the tallest pane drawn since it
/// opened): a page shorter than one shown before it in the same visit — a yes/no, a re-shown list a row shorter, a row's own page —
/// prints blank rows under its content before the rule. The tests' one place for "this, then any held rows, then that".
/// </summary>
internal static class HeldRows
{
    /// <summary><paramref name="before"/>, any number of blank rows the hold adds, then <paramref name="after"/>, somewhere in <paramref name="output"/>.</summary>
    public static void AssertContains(string before, string after, string output) =>
        Assert.Matches(new Regex(Regex.Escape(before) + "\n*" + Regex.Escape(after)), output);
}
