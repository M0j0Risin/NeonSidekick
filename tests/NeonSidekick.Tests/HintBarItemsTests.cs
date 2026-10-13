using NeonSidekick.App;
using NeonSidekick.UI;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary><c>Hint bar</c>'s checklist (2026-10-12, the user's ask): the sections, how a saved list and order read, and where the model stands.</summary>
public sealed class HintBarItemsTests
{
    [Fact]
    public void TheSections_AreTheRowsOwnOrder_EveryOneByDefault()
    {
        Assert.Equal(["status", "queued", "keys", "timers", "reading", "usage", "model"], HintBarItems.Names);
        Assert.True(HintBarItems.Resolve(null).SetEquals(HintBarItems.Names));
        Assert.True(HintBarItems.Resolve([" Usage ", "MODEL", "spark", null!]).SetEquals(["usage", "model"]));   // trimmed, any case, an unknown word and a null dropped
        Assert.Empty(HintBarItems.Resolve([]));
        Assert.Null(HintBarItems.Save(HintBarItems.Names.ToHashSet()));
        Assert.Equal(["status", "usage"], HintBarItems.Save(new HashSet<string> { "usage", "status" }));
        Assert.Equal([], HintBarItems.Save(new HashSet<string>()));
    }

    [Fact]
    public void TheOrder_IsEverySection_MendedAsTheBarsAre()
    {
        Assert.Equal(HintBarItems.Names, HintBarItems.Order(null));
        Assert.Null(HintBarItems.SaveOrder(HintBarItems.Order(null)));
        var order = HintBarItems.Order(["model", "usage"]);
        Assert.Equal(["status", "queued", "keys", "timers", "reading", "model", "usage"], order);   // the rest after their neighbours, as the bars' are
        Assert.Equal(["model", "status", "queued"], HintBarItems.Order(["model", "status", "queued", "keys", "timers", "reading", "usage"]).Take(3));
    }

    [Fact]
    public void TheModel_KeepsTheRightEdge_OnlyWhileItIsTheLastChecked()
    {
        var all = HintBarItems.Resolve(null);
        Assert.True(HintBarItems.ModelPinned(HintBarItems.Order(null), all));
        Assert.False(HintBarItems.ModelPinned(HintBarItems.Order(["model", "status", "queued", "keys", "timers", "reading", "usage"]), all));
        // Last among the checked ones, an unchecked one after it.
        Assert.True(HintBarItems.ModelPinned(HintBarItems.Order(["status", "queued", "keys", "timers", "reading", "model", "usage"]), HintBarItems.Resolve(["status", "model"])));
        Assert.False(HintBarItems.ModelPinned(HintBarItems.Order(null), HintBarItems.Resolve(["status"])));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("all", HintBarItems.Value(null, null));
        Assert.Equal("none", HintBarItems.Value([], null));
        Assert.Equal("Status, Usage", HintBarItems.Value(["usage", "status"], null));
        Assert.Equal("Model, Status, Queued, Keys, Timers, Reading, Usage", HintBarItems.Value(null, ["model", "status", "queued", "keys", "timers", "reading", "usage"]));   // every one, moved: named
        Assert.Equal("[[x]] Status    " + Theme.DimMarkup("the switches and jobs at the start: 🔊 🎤 👂 ✋, 🧠, 🏷️…"), HintBarItems.Label("status", true));
        Assert.Equal("[[ ]] Model     " + Theme.DimMarkup("the model's name and its reasoning mark; last, at the right edge"), HintBarItems.Label("model", false));
        Assert.Equal(["Status", "Queued", "Keys", "Timers", "Reading", "Usage", "Model"], HintBarItems.Names.Select(HintBarItems.Title));
    }
}
