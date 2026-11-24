using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The pages' bare rules between groups (2026-11-24, the user's ask): a rule only between two groups that both have rows, never at the top or
/// bottom or two in a row; items map to page rows and back, a rule to -1; a cursor kept past the end lands on the last item.
/// </summary>
public sealed class RuledRowsTests
{
    [Fact]
    public void EmptyGroups_MakeNoRule_AndNoneAtTheEnds()
    {
        var ruled = RuledRows.Of([[], ["a"], [], ["b", "c"], []]);

        Assert.Equal(["a", SectionRule.Bare, "b", "c"], ruled.Rows);
        Assert.Equal(new HashSet<int> { 1 }, ruled.Headings);
        Assert.Equal(3, ruled.Items);
    }

    [Fact]
    public void OneGroup_HasNoHeadings()
    {
        var ruled = RuledRows.Of([["a", "b"]]);

        Assert.Equal(["a", "b"], ruled.Rows);
        Assert.Null(ruled.Headings);
    }

    [Fact]
    public void ItemOfAndRowOf_RoundTripEveryItem_ARuleIsMinusOne()
    {
        var ruled = RuledRows.Of([["a"], ["b", "c"], ["d"]]);

        Assert.Equal([0, -1, 1, 2, -1, 3], Enumerable.Range(0, ruled.Rows.Count).Select(ruled.ItemOf));
        for (int item = 0; item < ruled.Items; item++)
        {
            Assert.Equal(item, ruled.ItemOf(ruled.RowOf(item)));
        }

        Assert.Equal(-1, ruled.ItemOf(-1));   // Esc's no pick stays itself
        Assert.Equal(-3, ruled.ItemOf(-3));
    }

    [Fact]
    public void RowOf_ClampsToTheItems_NeverARule()
    {
        var ruled = RuledRows.Of([["add"], ["x", "y"]]);

        Assert.Equal(3, ruled.RowOf(5));    // past the end: the last item, as after the last entry's removal
        Assert.Equal(0, ruled.RowOf(-1));
        Assert.Equal(0, RuledRows.Of([[], []]).RowOf(3));   // no items: the pane's own empty page
    }

    [Fact]
    public void Hotkeys_LandOnTheirItemsPageRows()
    {
        var ruled = RuledRows.Of([["a", "b"], ["c"]]);

        var keys = ruled.Hotkeys(new Dictionary<char, int> { ['a'] = 0, ['c'] = 2 });

        Assert.Equal(0, keys['a']);
        Assert.Equal(3, keys['c']);
    }
    /// <summary>A wizard summary (the UNC share's and ten more): the saves, a rule, test and cancel, a rule, the draft's rows; Down crosses each rule.</summary>
    [Fact]
    public void AWizardSummary_RulesItsThreeGroups()
    {
        var ruled = NeonSidekick.App.SettingsMenu.ConnectionWizardSummaryRows(["Save, and offer it", "Save, hidden", "Test", "Cancel"], ["Name  share", "Path  \\\\nas\\media"]);

        Assert.Equal(10 - 2, ruled.Rows.Count);
        Assert.Equal([2, 5], ruled.Headings!.Order());
        Assert.Equal(SectionRule.Bare, ruled.Rows[2]);
        Assert.Equal(SectionRule.Bare, ruled.Rows[5]);
        Assert.Equal(2, ruled.ItemOf(3));   // Test
        Assert.Equal(4, ruled.ItemOf(6));   // the draft's first row
        Assert.Equal(6, ruled.Items);
    }
}
