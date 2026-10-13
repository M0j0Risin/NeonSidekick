using NeonSidekick.App;
using NeonSidekick.UI;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary><c>Hint bar</c>'s checklist (2026-10-12, the user's ask): the sections, how a saved list and order read, and what stands at the right edge.</summary>
public sealed class HintBarItemsTests
{
    [Fact]
    public void TheSections_AreTheRowsOwnOrder_TheWorkingDirectoryOffByDefault()
    {
        Assert.Equal(["status", "queued", "keys", "timers", "reading", "usage", "model", "cwd"], HintBarItems.Names);   // the working directory after the model (later on 2026-10-12)
        Assert.Equal(HintBarItems.Names.SkipLast(1), HintBarItems.Defaults);
        Assert.True(HintBarItems.Resolve(null).SetEquals(HintBarItems.Defaults));
        Assert.True(HintBarItems.Resolve([" Usage ", "MODEL", "spark", null!]).SetEquals(["usage", "model"]));   // trimmed, any case, an unknown word and a null dropped
        Assert.Empty(HintBarItems.Resolve([]));
        Assert.Null(HintBarItems.Save(HintBarItems.Defaults.ToHashSet()));
        Assert.Equal(HintBarItems.Names, HintBarItems.Save(HintBarItems.Names.ToHashSet()));   // every one is a list now
        Assert.Equal(["status", "usage"], HintBarItems.Save(new HashSet<string> { "usage", "status" }));
        Assert.Equal([], HintBarItems.Save(new HashSet<string>()));
    }

    [Fact]
    public void TheOrder_IsEverySection_MendedAsTheBarsAre()
    {
        Assert.Equal(HintBarItems.Names, HintBarItems.Order(null));
        Assert.Null(HintBarItems.SaveOrder(HintBarItems.Order(null)));
        var order = HintBarItems.Order(["model", "usage"]);
        Assert.Equal(["status", "queued", "keys", "timers", "reading", "model", "cwd", "usage"], order);   // the rest after their neighbours, as the bars' are
        Assert.Equal(["model", "status", "queued"], HintBarItems.Order(["model", "status", "queued", "keys", "timers", "reading", "usage", "cwd"]).Take(3));
    }

    /// <summary>The sides (2026-10-13, the user's ask): the model and the working directory pinned right by default, any section either side, each in the order's order.</summary>
    [Fact]
    public void TheSides_AreTheModelAndTheWorkingDirectoryRight_ByDefault_AnySectionEitherSide()
    {
        var sides = HintBarItems.Sides(null, null);
        Assert.Equal(["status", "queued", "keys", "timers", "reading", "usage"], sides.Left);
        Assert.Equal(["model", "cwd"], sides.Right);
        Assert.Null(HintBarItems.SaveRight(sides.Right));
        var moved = HintBarItems.Sides(["model", "status", "usage"], ["usage", "status"]);
        Assert.Equal(["status", "usage"], moved.Right);   // the order's order, not the saved list's
        Assert.Equal(["model", "cwd", "queued", "keys", "timers", "reading"], moved.Left);   // cwd after the model, its neighbour
        Assert.Equal(["status", "usage"], HintBarItems.SaveRight(moved.Right));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("default", HintBarItems.Value(null, null));   // "all" until the working directory came, off by default
        Assert.Equal("all", HintBarItems.Value([.. HintBarItems.Names], null));
        Assert.Equal("none", HintBarItems.Value([], null));
        Assert.Equal("Status, Usage", HintBarItems.Value(["usage", "status"], null));
        Assert.Equal("Status, Queued, Keys, Timers, Reading, Usage | Model", HintBarItems.Value(null, ["model", "status", "queued", "keys", "timers", "reading", "usage", "cwd"]));   // the defaults, moved: named, each side (2026-10-13)
        Assert.Equal("Status, Queued, Keys, Timers, Reading, Model | Usage", HintBarItems.Value(null, null, ["usage"]));
        Assert.Equal("| Status, Usage", HintBarItems.Value(["usage", "status"], null, ["usage", "status"]));
        Assert.Equal("[[x]] Status             " + Theme.DimMarkup("the switches and jobs at the start: 🔊 🎤 👂 ✋, 🧠, 🏷️…"), HintBarItems.Label("status", true));
        Assert.Equal("[[ ]] Model              " + Theme.DimMarkup("the model's name and its reasoning mark"), HintBarItems.Label("model", false));
        Assert.Equal("[[ ]] Working directory  " + Theme.DimMarkup("the working directory, as the tool bar shows it (/cwd browse)"), HintBarItems.Label("cwd", false));
        Assert.Equal(["Status", "Queued", "Keys", "Timers", "Reading", "Usage", "Model", "Working directory"], HintBarItems.Names.Select(HintBarItems.Title));
    }
}
