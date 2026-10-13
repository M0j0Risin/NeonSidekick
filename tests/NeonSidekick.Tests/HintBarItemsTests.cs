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

    /// <summary>The right edge (later on 2026-10-12): the checked model and working directory that end the checked sections, in their order.</summary>
    [Fact]
    public void TheRightEdge_IsTheModelAndTheWorkingDirectory_WhileTheyEndTheRow()
    {
        var order = HintBarItems.Order(null);
        Assert.Equal(["model"], HintBarItems.RightEdge(order, HintBarItems.Resolve(null)));   // the row as it always was
        Assert.Equal(["model", "cwd"], HintBarItems.RightEdge(order, HintBarItems.Names.ToHashSet()));
        Assert.Equal(["cwd"], HintBarItems.RightEdge(order, HintBarItems.Resolve(["status", "cwd"])));
        Assert.Empty(HintBarItems.RightEdge(order, HintBarItems.Resolve(["status"])));
        List<string> modelFirst = ["model", "status", "queued", "keys", "timers", "reading", "usage", "cwd"];
        Assert.Equal(["cwd"], HintBarItems.RightEdge(HintBarItems.Order(modelFirst), HintBarItems.Names.ToHashSet()));   // the model in the flow
        List<string> cwdFirst = ["status", "queued", "keys", "timers", "reading", "usage", "cwd", "model"];
        Assert.Equal(["cwd", "model"], HintBarItems.RightEdge(HintBarItems.Order(cwdFirst), HintBarItems.Names.ToHashSet()));
        // An unchecked section after them does not count.
        Assert.Equal(["model"], HintBarItems.RightEdge(HintBarItems.Order(["status", "queued", "keys", "timers", "reading", "model", "usage", "cwd"]), HintBarItems.Resolve(["status", "model"])));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("default", HintBarItems.Value(null, null));   // "all" until the working directory came, off by default
        Assert.Equal("all", HintBarItems.Value([.. HintBarItems.Names], null));
        Assert.Equal("none", HintBarItems.Value([], null));
        Assert.Equal("Status, Usage", HintBarItems.Value(["usage", "status"], null));
        Assert.Equal("Model, Status, Queued, Keys, Timers, Reading, Usage", HintBarItems.Value(null, ["model", "status", "queued", "keys", "timers", "reading", "usage", "cwd"]));   // the defaults, moved: named
        Assert.Equal("[[x]] Status             " + Theme.DimMarkup("the switches and jobs at the start: 🔊 🎤 👂 ✋, 🧠, 🏷️…"), HintBarItems.Label("status", true));
        Assert.Equal("[[ ]] Model              " + Theme.DimMarkup("the model's name and its reasoning mark; last, at the right edge"), HintBarItems.Label("model", false));
        Assert.Equal("[[ ]] Working directory  " + Theme.DimMarkup("the working directory, as the tool bar shows it (/cwd browse)"), HintBarItems.Label("cwd", false));
        Assert.Equal(["Status", "Queued", "Keys", "Timers", "Reading", "Usage", "Model", "Working directory"], HintBarItems.Names.Select(HintBarItems.Title));
    }
}
