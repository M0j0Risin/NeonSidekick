using NeonSidekick.App;
using NeonSidekick.UI;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Status bar</c>'s checklist (<c>Hint bar</c>'s until 2026-10-14; 2026-10-12, the user's ask): the sections, how a saved list and
/// order read, and what stands at the right edge. Keys went to the Hints bar on 2026-10-14: a saved <c>keys</c> is dropped.
/// </summary>
public sealed class StatusBarItemsTests
{
    [Fact]
    public void TheSections_AreTheRowsOwnOrder_TheWorkingDirectoryOffByDefault()
    {
        Assert.Equal(["status", "spinner", "queued", "timers", "reading", "usage", "model", "cwd"], StatusBarItems.Names);   // the working directory after the model (later on 2026-10-12); the spinner after Status (2026-10-13, the user's order)
        Assert.Equal(StatusBarItems.Names.SkipLast(1), StatusBarItems.Defaults);
        Assert.True(StatusBarItems.Resolve(null).SetEquals(StatusBarItems.Defaults));
        Assert.True(StatusBarItems.Resolve([" Usage ", "MODEL", "spark", null!]).SetEquals(["spinner", "usage", "model"]));
        Assert.True(StatusBarItems.Resolve(["status", "keys"]).SetEquals(["status", "spinner"]));   // the Hints bar's since 2026-10-14   // trimmed, any case, an unknown word and a null dropped; the spinner always
        Assert.Null(StatusBarItems.Save(StatusBarItems.Defaults.ToHashSet()));
        Assert.Equal(StatusBarItems.Names, StatusBarItems.Save(StatusBarItems.Names.ToHashSet()));   // every one is a list now
        Assert.Equal(["status", "usage"], StatusBarItems.Save(new HashSet<string> { "usage", "status" }));
        Assert.Equal([], StatusBarItems.Save(new HashSet<string>()));
    }

    /// <summary>The spinner (2026-10-13, the user's ask): always on, whatever a saved list says, from before it came or not.</summary>
    [Fact]
    public void TheSpinner_IsAlwaysOn()
    {
        Assert.Equal(["spinner"], StatusBarItems.Fixed);
        Assert.Equal(["spinner"], StatusBarItems.Resolve([]));
        Assert.Contains("spinner", StatusBarItems.Resolve(["status"]));
        Assert.Contains("spinner", StatusBarItems.Resolve(null));
        Assert.Equal("The spinner is always on: Shift+↑↓ and Shift+←→ move it", StatusBarItems.SpinnerFixedNotice);
        Assert.Equal("[[x]] Spinner            " + Theme.DimMarkup("turn activity spinner, info and stop hint: always on"), StatusBarItems.Label("spinner", true));
    }

    [Fact]
    public void TheOrder_IsEverySection_MendedAsTheBarsAre()
    {
        Assert.Equal(StatusBarItems.Names, StatusBarItems.Order(null));
        Assert.Null(StatusBarItems.SaveOrder(StatusBarItems.Order(null)));
        var order = StatusBarItems.Order(["model", "usage"]);
        Assert.Equal(["status", "spinner", "queued", "timers", "reading", "model", "cwd", "usage"], order);   // the rest after their neighbours, as the bars' are; the spinner, missing, after Status
        Assert.Equal(["model", "status", "spinner", "queued"], StatusBarItems.Order(["model", "status", "queued", "keys", "timers", "reading", "usage", "cwd"]).Take(4));   // an order from before the spinner: it lands after Status
        Assert.Equal(["model", "cwd", "spinner", "queued"], StatusBarItems.Order(["model", "spinner", "status"]).Take(4));   // a saved spinner keeps its place (cwd after the model, its neighbour)
    }

    /// <summary>The sides (2026-10-13, the user's ask): the model and the working directory pinned right by default, any section either side, each in the order's order.</summary>
    [Fact]
    public void TheSides_AreTheModelAndTheWorkingDirectoryRight_ByDefault_AnySectionEitherSide()
    {
        var sides = StatusBarItems.Sides(null, null);
        Assert.Equal(["status", "spinner", "queued", "timers", "reading", "usage"], sides.Left);
        Assert.Equal(["model", "cwd"], sides.Right);
        Assert.Null(StatusBarItems.SaveRight(sides.Right));
        var moved = StatusBarItems.Sides(["model", "status", "usage"], ["usage", "status"]);
        Assert.Equal(["status", "usage"], moved.Right);   // the order's order, not the saved list's
        Assert.Equal(["model", "cwd", "spinner", "queued", "timers", "reading"], moved.Left);   // cwd after the model, its neighbour
        Assert.Equal(["status", "usage"], StatusBarItems.SaveRight(moved.Right));
    }

    [Fact]
    public void Value_AndLabels_ArePinned()
    {
        Assert.Equal("default", StatusBarItems.Value(null, null));   // "all" until the working directory came, off by default
        Assert.Equal("all", StatusBarItems.Value([.. StatusBarItems.Names], null));
        Assert.Equal("none", StatusBarItems.Value([], null));   // the spinner alone, always on, is none
        Assert.Equal("none", StatusBarItems.Value(["spinner"], null));
        Assert.Equal("Status", StatusBarItems.Value(["status"], ["status", "spinner"]));   // after Status, its own place: unnamed
        Assert.Equal("Spinner, Status", StatusBarItems.Value(["status"], ["queued", "spinner", "status"]));   // named away from it (2026-10-13)
        Assert.Equal("Status | Spinner", StatusBarItems.Value(["status"], null, ["spinner"]));
        Assert.Equal("Status, Usage", StatusBarItems.Value(["usage", "status"], null));
        Assert.Equal("Status, Queued, Timers, Reading, Usage | Model", StatusBarItems.Value(null, ["model", "status", "queued", "timers", "reading", "usage", "cwd"]));   // the defaults, moved: named, each side (2026-10-13)
        Assert.Equal("Status, Queued, Timers, Reading, Model | Usage", StatusBarItems.Value(null, null, ["usage"]));
        Assert.Equal("| Status, Usage", StatusBarItems.Value(["usage", "status"], null, ["usage", "status"]));
        Assert.Equal("[[x]] Status             " + Theme.DimMarkup("status icons: 🔊 🎤 👂 ✋, 🧠, 🏷️…"), StatusBarItems.Label("status", true));
        Assert.Equal("[[ ]] Model              " + Theme.DimMarkup("the model's name and its reasoning mark"), StatusBarItems.Label("model", false));
        Assert.Equal("[[ ]] Working directory  " + Theme.DimMarkup("the working directory, as the tool bar shows it (/cwd browse)"), StatusBarItems.Label("cwd", false));
        Assert.Equal(["Status", "Spinner", "Queued", "Timers", "Reading", "Usage", "Model", "Working directory"], StatusBarItems.Names.Select(StatusBarItems.Title));
    }
}
