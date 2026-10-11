using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/shopping</c> (2026-10-11, the user's ask): the line read, what each verb does through <see cref="ShoppingCommandRunner"/> (the
/// screen's and headless's one runner), and the list's pane.
/// </summary>
public sealed class ShoppingCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStoreHolder _holder;
    private readonly ShoppingList _list;
    private readonly WorkingDirectory _files;
    private readonly TestConsole _console = new();

    public ShoppingCommandTests()
    {
        _holder = new ShoppingStoreHolder(() => _dir, _time);
        _list = new ShoppingList(_holder.Current);
        _files = new WorkingDirectory(() => Path.Combine(_dir, "files"), _time);
        _console.Interactive();
        _console.Profile.Width = 120;
        _console.Profile.Height = 40;
    }

    public void Dispose()
    {
        _holder.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Task<IReadOnlyList<string>> Run(string args, Func<string, CancellationToken, Task<bool>>? confirm = null, Func<string, string, CancellationToken, Task<string>>? print = null) =>
        ShoppingCommandRunner.RunAsync(_list, ShoppingCommand.Parse(args), _files, confirm, print, CancellationToken.None);

    [Theory]
    [InlineData("", ShoppingVerb.Pane, "")]
    [InlineData("list", ShoppingVerb.List, "")]
    [InlineData("list Costco", ShoppingVerb.List, "Costco")]
    [InlineData("add milk, 2 eggs", ShoppingVerb.Add, "milk, 2 eggs")]
    [InlineData("TICK milk", ShoppingVerb.Check, "milk")]
    [InlineData("uncheck 2", ShoppingVerb.Uncheck, "2")]
    [InlineData("remove soy milk", ShoppingVerb.Remove, "soy milk")]
    [InlineData("clear", ShoppingVerb.Clear, "")]
    [InlineData("clear ALL", ShoppingVerb.ClearAll, "")]
    [InlineData("store", ShoppingVerb.Stores, "")]
    [InlineData("stores", ShoppingVerb.Stores, "")]
    [InlineData("store H-E-B #372", ShoppingVerb.UseStore, "H-E-B #372")]
    [InlineData("store add Costco", ShoppingVerb.AddStore, "Costco")]
    [InlineData("store remove Costco", ShoppingVerb.RemoveStore, "Costco")]
    [InlineData("export", ShoppingVerb.Export, "")]
    [InlineData("print", ShoppingVerb.Print, "")]
    public void Parse_ReadsEachForm(string args, ShoppingVerb verb, string text) =>
        Assert.Equal(new ShoppingCommandLine(verb, text), ShoppingCommand.Parse(args));

    [Theory]
    [InlineData("add")]
    [InlineData("check")]
    [InlineData("clear some")]
    [InlineData("store add")]
    [InlineData("export now")]
    [InlineData("milk")]
    public void Parse_AnythingElse_IsTheUsage(string args) =>
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.Unknown, Error: ShoppingText.CommandUsage), ShoppingCommand.Parse(args));

    [Fact]
    public void Complete_TheVerbs_ThenTheStoresAndTheLines()
    {
        Assert.Equal(["add", "check", "uncheck", "remove", "list", "clear", "store", "export", "print"], ShoppingCommand.Words.Select(w => w.Word));
        Assert.Equal(["clear"], ShoppingCommand.Complete("cl", [], []).Select(c => c.Text));
        Assert.Equal(["store add ", "store remove ", "store Costco", "store H-E-B"], ShoppingCommand.Complete("store ", ["Costco", "H-E-B"], []).Select(c => c.Text));
        Assert.Equal(["check Soy milk"], ShoppingCommand.Complete("check so", [], ["Soy milk", "Bread"]).Select(c => c.Text));
        Assert.Empty(ShoppingCommand.Complete("add mi", ["Costco"], ["Milk"]));
        Assert.Equal(["milk", "2 eggs", "bread x2"], ShoppingCommand.Items(" milk, 2 eggs;; bread x2 ,"));
    }

    [Fact]
    public async Task TheVerbs_AddCheckListClear_LikeTheTools_WithoutTheModelsHint()
    {
        Assert.Equal(["Added Milk.", "Added Eggs ×2."], await Run("add Milk, 2 Eggs"));
        Assert.Equal(["Ticked Milk."], await Run("check milk"));
        Assert.Equal(["Unticked Milk."], await Run("uncheck 1"));
        Assert.Equal([ShoppingText.NotOnList("butter")], await Run("remove butter"));

        Assert.Equal(["Added the store \"Costco\" (the active store); its reference documents go in " + Path.Combine(Path.GetFullPath(_dir), "db", "shopping", "reference", "costco") + "."], await Run("store add Costco"));
        // At a store, an unplaced add says so, but the hint to the model is not the user's.
        Assert.Equal(["Added Bread — not placed at Costco yet."], await Run("add Bread"));
        Assert.Equal(["Shopping list at Costco: 3 items to get.", "Unplaced", "- Bread", "- Eggs ×2", "- Milk"], await Run(""));
        Assert.Equal(await Run(""), await Run("list"));

        await Run("check bread");
        Assert.Equal(["Cleared the ticked lines (1 line)."], await Run("clear"));
    }

    [Fact]
    public async Task ClearAll_AndRemovingAStore_AskFirst_AndHeadlessTheLineIsTheAct()
    {
        await Run("add Milk, Eggs");
        await Run("store add H-E-B");
        string? asked = null;
        Assert.Equal([ChatScreen.KeptNotice], await Run("clear all", (q, _) => { asked = q; return Task.FromResult(false); }));
        Assert.Equal(ShoppingText.ClearAllQuestion(2), asked);
        Assert.Equal(2, _list.Store.Items().Count);
        Assert.Equal(["Cleared the whole list (2 lines)."], await Run("clear all"));   // headless: no confirm

        Assert.Equal([ChatScreen.KeptNotice], await Run("store remove h-e-b", (q, _) => { asked = q; return Task.FromResult(false); }));
        Assert.Equal("Remove the store \"H-E-B\" with its layout (0 placement facts)? Its reference folder stays.", asked);
        Assert.Equal(["Removed the store \"H-E-B\"."], await Run("store remove H-E-B", (_, _) => Task.FromResult(true)));
        Assert.Empty(_list.Store.Stores());
        Assert.Equal([ShoppingText.NoSuchStore("Kroger", [])], await Run("store Kroger"));
    }

    [Fact]
    public async Task Export_WritesTheItemsLeftToBuy_InTheWalk_AndPrint_HandsTheSameMarkdownOn()
    {
        Assert.Equal([ShoppingText.NothingToBuy], await Run("export"));
        await Run("store add H-E-B");
        _list.Place(_list.StoreFor(null, out _)!, "Milk", ShoppingKinds.Category, "Dairy", null, ShoppingSources.Guess);
        await Run("add Bread, Soy milk, Eggs");
        await Run("check eggs");

        string path = Path.Combine(_files.Root, ShoppingText.ExportFileName);
        Assert.Equal([ShoppingText.Exported(path, 2)], await Run("export"));
        const string Expected = "# Shopping list — H-E-B\n\n## Dairy\n\n- [ ] Soy milk (guess)\n\n## Unplaced\n\n- [ ] Bread\n";
        Assert.Equal(Expected, File.ReadAllText(path));

        (string Markdown, string Title)? printed = null;
        Assert.Equal(["Printed 1 page."], await Run("print", print: (md, title, _) => { printed = (md, title); return Task.FromResult("Printed 1 page."); }));
        Assert.Equal((Expected, "Shopping list — H-E-B"), printed);
        Assert.Equal(["Error: " + ShoppingText.NoPrinting], await Run("print"));
    }

    [Fact]
    public async Task ThePane_TicksWithEnter_ChangesTheQuantity_Removes_AndClearsTheTicked()
    {
        await Run("store add H-E-B");
        _list.Place(_list.StoreFor(null, out _)!, "Milk", ShoppingKinds.Category, "Aisle 2", null, ShoppingSources.User);
        await Run("add Soy milk, Bread, Eggs");

        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var menu = new ShoppingMenu(_list, new MenuPane(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))));
        pane.Show();
        input.Push(Keys.Enter);                 // the first line, under Aisle 2: Soy milk ticked, the cursor following it to Ticked, last
        input.Push(Keys.Up);                    // past the Ticked heading: Eggs
        input.Push(Keys.Up);                    // Bread
        input.Push(Keys.Char('+'));             // Bread ×2
        input.Push(Keys.Down);                  // Eggs
        input.Push(Keys.Char('r'));             // Eggs removed
        input.Push(Keys.Char('c'));             // the ticked lines cleared: Soy milk gone
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([("Bread", 2, false)], _list.Store.Items().Select(i => (i.Name, i.Quantity, i.Checked)));
        pane.Dispose();
    }

    [Fact]
    public void ThePanesRows_AreHeadingsOverLines_TheTickedLastAndDim()
    {
        var milk = new ShopItem(1, "Milk", 2, "", false, DateTimeOffset.UnixEpoch);
        var bread = new ShopItem(2, "Bread", 1, "", true, DateTimeOffset.UnixEpoch);
        var view = new ShopView(null, [new ShopGroup(null, null, [new PlacedItem(milk, ShopPlace.Unplaced)])], [bread], []);

        var (rows, headings, items) = ShoppingMenu.Rows(view);

        Assert.Equal([0, 2], headings.Order());
        Assert.Equal([null, milk, null, bread], items);
        Assert.Equal(SectionRule.Markup(ShoppingText.UnplacedHeading), rows[0]);
        Assert.Equal(Spectre.Console.Markup.Escape("[ ] Milk ×2"), rows[1]);
        Assert.Equal(Theme.DimMarkup("[x] Bread"), rows[3]);
        Assert.Equal("1 item to get, 1 ticked", ShoppingText.PaneCaption(1, 1));
        Assert.Equal("🛒 Shopping list — Costco", ShoppingText.PaneLabel(new ShopStore(1, "Costco", "costco", "")));
    }
}
