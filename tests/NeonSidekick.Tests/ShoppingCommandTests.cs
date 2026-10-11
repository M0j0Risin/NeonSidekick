using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
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
        ShoppingCommandRunner.RunAsync(_list, ShoppingCommand.Parse(args), _files, confirm, print, CancellationToken.None, time: _time);

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
    [InlineData("print verbose", ShoppingVerb.Print, "", true)]
    [InlineData("print H-E-B #372", ShoppingVerb.Print, "H-E-B #372", false)]
    [InlineData("export H-E-B #372 VERBOSE", ShoppingVerb.Export, "H-E-B #372", true)]
    [InlineData("export Costco", ShoppingVerb.Export, "Costco", false)]
    public void PrintAndExport_TakeAStoreAndVerbose(string args, ShoppingVerb verb, string store, bool verbose) =>
        Assert.Equal(new ShoppingCommandLine(verb, store, Verbose: verbose), ShoppingCommand.Parse(args));

    [Fact]
    public async Task StoreRename_TakesTheOldNameToTheNew_TheFolderKept()
    {
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.RenameStore, "H-E-B #372", To: "H-E-B Bandera"), ShoppingCommand.Parse("store rename H-E-B #372 to H-E-B Bandera"));
        Assert.Equal(ShoppingVerb.Unknown, ShoppingCommand.Parse("store rename H-E-B").Verb);
        await Run("store add H-E-B #372");
        string moved = Path.Combine(_list.Store.ReferenceRoot, "h-e-b-bandera");
        Assert.Equal([ShoppingText.Renamed("store", "H-E-B #372", "H-E-B Bandera") + " " + ShoppingText.FolderMoved(moved)], await Run("store rename h-e-b #372 to H-E-B Bandera"));
        Assert.Equal("h-e-b-bandera", _list.Store.FindStore("H-E-B Bandera")!.Folder);   // the folder follows the name
        Assert.True(Directory.Exists(moved));
        Assert.False(Directory.Exists(Path.Combine(_list.Store.ReferenceRoot, "h-e-b-372")));
        // Only the capitals changed: the same folder, nothing moved, nothing said of it.
        Assert.Equal([ShoppingText.Renamed("store", "H-E-B Bandera", "h-e-b bandera")], await Run("store rename H-E-B Bandera to h-e-b bandera"));
        Assert.Equal([ShoppingText.NoSuchStore("Kroger", _list.Store.Stores())], await Run("store rename Kroger to Fiesta"));
    }

    [Theory]
    [InlineData("check")]
    [InlineData("clear some")]
    [InlineData("store add")]
    [InlineData("milk")]
    public void Parse_AnythingElse_IsTheUsage(string args) =>
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.Unknown, Error: ShoppingText.CommandUsage), ShoppingCommand.Parse(args));

    [Fact]
    public void Complete_TheVerbs_ThenTheStoresAndTheLines()
    {
        Assert.Equal(["add", "check", "uncheck", "remove", "rename", "list", "clear", "store", "export", "print", "reindex"], ShoppingCommand.Words.Select(w => w.Word));
        Assert.Equal(["clear"], ShoppingCommand.Complete("cl", [], []).Select(c => c.Text));
        Assert.Equal(["store add ", "store rename ", "store remove ", "store Costco", "store H-E-B"], ShoppingCommand.Complete("store ", ["Costco", "H-E-B"], []).Select(c => c.Text));
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

        // A file per export, named for the local time (2026-10-11): the next one a minute on is a second file, the first kept.
        string path = Path.Combine(_files.Root, ShoppingText.ExportFileName(_time.GetLocalNow()));
        Assert.Equal("shopping-list-" + _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".md", Path.GetFileName(path));
        Assert.Equal([ShoppingText.Exported(path, 2)], await Run("export"));
        // Compact by default: the store's heading, then a line per group, a hard break ending each but the last; verbose is the roomy form.
        const string Compact = "## Shopping List — H-E-B\n\n**Dairy:** Soy milk (guess)\\\n**Unplaced:** Bread\n";
        const string Verbose = "# Shopping list — H-E-B\n\n## Dairy\n\n- [ ] Soy milk (guess)\n\n## Unplaced\n\n- [ ] Bread\n";
        Assert.Equal(Compact, File.ReadAllText(path));
        _time.Advance(TimeSpan.FromMinutes(1));
        string later = Path.Combine(_files.Root, ShoppingText.ExportFileName(_time.GetLocalNow()));
        Assert.Equal([ShoppingText.Exported(later, 2)], await Run("export verbose"));
        Assert.Equal(Verbose, File.ReadAllText(later));
        Assert.Equal(Compact, File.ReadAllText(path));

        (string Markdown, string Title)? printed = null;
        Task<string> Printer(string md, string title, CancellationToken _) { printed = (md, title); return Task.FromResult("Printed 1 page."); }
        Assert.Equal(["Printed 1 page."], await Run("print", print: Printer));
        Assert.Equal((Compact, "Shopping list — H-E-B"), printed);
        Assert.Equal(["Printed 1 page."], await Run("print h-e-b verbose", print: Printer));
        Assert.Equal((Verbose, "Shopping list — H-E-B"), printed);
        Assert.Equal(["Error: " + ShoppingText.NoPrinting], await Run("print"));

        // Another store, named: its own walk (nothing placed there yet), the active store unchanged.
        await Run("store add Costco");
        Assert.Equal(["Printed 1 page."], await Run("print Costco", print: Printer));
        Assert.Equal(("## Shopping List — Costco\n\n**Unplaced:** Bread · Soy milk\n", "Shopping list — Costco"), printed);
        Assert.Equal("H-E-B", _list.StoreFor(null, out _)!.Name);
        Assert.Equal([ShoppingText.NoSuchStore("Kroger", _list.Store.Stores())], await Run("export Kroger"));
    }

    [Fact]
    public async Task Rename_KeepsTheQuantityAndTick_AndANameAlreadyThere_Merges()
    {
        await Run("add 2 Soda, Milk, Eggs");
        await Run("check soda");
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.Rename, "soda", To: "Dr Pepper"), ShoppingCommand.Parse("rename soda to Dr Pepper"));
        Assert.Equal(ShoppingVerb.Unknown, ShoppingCommand.Parse("rename soda").Verb);

        Assert.Equal([ShoppingText.Renamed("item", "Soda", "Dr Pepper")], await Run("rename soda to Dr Pepper"));
        Assert.Equal([("Dr Pepper", 2, true), ("Milk", 1, false), ("Eggs", 1, false)], _list.Store.Items().Select(i => (i.Name, i.Quantity, i.Checked)));

        // Onto a name already on the list (folded alike): one line, the quantities added, the other's name kept.
        Assert.Equal(["\"Milk\" is on the list already; merged \"Eggs\" into it: now Milk ×2."], await Run("rename 3 to milks"));
        Assert.Equal([("Dr Pepper", 2), ("Milk", 2)], _list.Store.Items().Select(i => (i.Name, i.Quantity)));
        Assert.Equal([ShoppingText.NotOnList("butter")], await Run("rename butter to ghee"));
    }

    private (ShoppingMenu Menu, ScreenPane Pane, ScriptedInput Input) PaneWithInput()
    {
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menu = new ShoppingMenu(_list, new MenuPane(pane, keys), input: new InputLine(pane, keys), zone: TimeZoneInfo.Utc);
        pane.Show();
        return (menu, pane, input);
    }

    [Fact]
    public async Task ThePane_PlacesALine_AtAnAisle_AndForgetsIt()
    {
        await Run("store add H-E-B");
        var shop = _list.StoreFor(null, out _)!;
        _list.AddLocation(shop, "Aisle 7", null);
        _list.AddSection(shop, "Deli & Meats");
        await Run("add Kombucha");
        var (menu, pane, input) = PaneWithInput();
        input.Push(Keys.Char('p'), Keys.Enter);                 // Aisle 7, the first choice
        input.Push(Keys.Char('p'), Keys.Down, Keys.Down, Keys.Enter);   // Aisle 7, Section: Deli & Meats, (forget where it is)
        input.Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Empty(_list.Store.Placements(shop.Id));
        Assert.Contains("At H-E-B, item \"Kombucha\" is in Aisle 7.", TabStrips.Unbadged(_console.Output));
        Assert.Contains(ShoppingText.Unplaced(shop, "Kombucha"), TabStrips.Unbadged(_console.Output));
        pane.Dispose();
    }

    [Fact]
    public async Task ThePane_AddPage_AddsALineBoughtBefore_AndTypedOnes()
    {
        await Run("add 2 Milk");
        await Run("check milk");
        await Run("clear");                                      // Milk ×2 remembered as bought
        Assert.Equal([("Milk", 2, 1)], _list.Bought().Select(b => (b.Name, b.Quantity, b.Times)));
        var (menu, pane, input) = PaneWithInput();
        input.Push(Keys.Char('i'));                              // the add page
        input.Push(Keys.Down, Keys.Enter);                       // Milk again, at ×2
        input.Push(Keys.Up, Keys.Enter);                         // the typed row
        foreach (char c in "Bread, 3 eggs")
        {
            input.Push(Keys.Char(c));
        }

        input.Push(Keys.Enter, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal([("Milk", 2), ("Bread", 1), ("eggs", 3)], _list.Store.Items().Select(i => (i.Name, i.Quantity)));
        Assert.Empty(_list.Bought());                            // Milk is on the list again, so not offered
        Assert.Contains("Milk ×2 · once · last Sep 11", TabStrips.Unbadged(_console.Output));
        pane.Dispose();
    }

    [Fact]
    public async Task Recent_ListsWhatWasBought_TheMostOftenFirst_AndTheCaptionCountsGuesses()
    {
        var recent = ChatScreen.ShoppingTools(_list, null).Single(t => t.Name == ShoppingRecentTool.ToolName);
        Assert.Equal("Nothing bought yet: a line is remembered when it leaves the list ticked.", (string)(await recent.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!);
        foreach (string round in new[] { "Eggs, Bread", "Eggs" })
        {
            await Run("add " + round);
            await Run("tick all");
            _list.CheckAll(true);
            await Run("clear");
        }

        await Run("add Soap");
        await Run("remove soap");                                // removed unticked: not bought
        Assert.Equal(
            "Bought before and not on the list now (2 lines), the most often first:\n- Eggs (2 times, last 2026-09-11)\n- Bread (once, last 2026-09-11)",
            (string)(await recent.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments()))!);
        Assert.Equal("1 item to get · 1 guess (p places one)", ShoppingText.PaneCaption(1, 0, 1));
    }

    [Fact]
    public async Task ThePane_RenamesTheHighlightedLine_OnTheInputRow()
    {
        await Run("add Soda, Bread");
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var keys = new KeySource(input, TimeSpan.FromMilliseconds(1));
        var menu = new ShoppingMenu(_list, new MenuPane(pane, keys), input: new InputLine(pane, keys));
        pane.Show();
        input.Push(Keys.Char('n'));                              // the first line (no store: in the order added): Soda
        foreach (char _ in "Soda")
        {
            input.Push(Keys.Backspace);
        }

        foreach (char c in "Sourdough")
        {
            input.Push(Keys.Char(c));
        }

        input.Push(Keys.Enter);
        input.Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(["Sourdough", "Bread"], _list.Store.Items().Select(i => i.Name));
        pane.Dispose();
    }

    [Fact]
    public async Task ThePane_TickAll_TicksEveryLine_ThenUnticksThemAll_AndTheButtonsHaveGlyphs()
    {
        // The bar drawn (later on 2026-10-11, the user's pick: the title row ran out of room): + and - keys alone, hidden and last.
        var buttons = ShoppingMenu.ButtonsFor(anyOpen: true);
        Assert.Equal(["⊕ add", "⌖ place", "✎ rename", "✖  del", "☑  all", "⊠ clear", "⇄ store"], buttons.Where(b => !b.Hidden).Select(b => b.Title));
        Assert.Equal(['i', 'p', 'n', 'r', 'a', 'c', 's', '+', '-'], buttons.Select(b => b.Key!.Value));
        Assert.Equal([false, false, false, false, false, false, false, true, true], buttons.Select(b => b.Hidden));
        Assert.Equal("☐  none", ShoppingMenu.ButtonsFor(anyOpen: false)[4].Title);
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.Add, ""), ShoppingCommand.Parse("add"));   // alone: the add page on the pane
        Assert.Equal(["Error: " + ShoppingText.NoItems + ": /shopping add milk, 2 eggs"], await Run("add"));   // without one

        await Run("add Bread, Eggs, Milk");
        await Run("check eggs");
        var pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => 100), _time) { Hint = () => "idle" };
        var input = new ScriptedInput();
        var menu = new ShoppingMenu(_list, new MenuPane(pane, new KeySource(input, TimeSpan.FromMilliseconds(1))));
        pane.Show();
        input.Push(Keys.Char('a'));   // tick all: Bread and Milk
        input.Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.All(_list.Store.Items(), i => Assert.True(i.Checked));

        input.Push(Keys.Char('a'));   // every line ticked: untick all
        input.Push(Keys.Escape);
        await menu.ShowAsync(CancellationToken.None);
        Assert.All(_list.Store.Items(), i => Assert.False(i.Checked));
        Assert.Equal("Ticked all 2 lines.", ShoppingText.CheckedAll(2, on: true));
        Assert.Equal("Unticked the one line.", ShoppingText.CheckedAll(1, on: false));
        pane.Dispose();
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
        Assert.Equal("🛒 Costco", ShoppingText.PaneLabel(new ShopStore(1, "Costco", "costco", "")));   // the store alone (later on 2026-10-11)
        Assert.Equal("🛒 Shopping list", ShoppingText.PaneLabel(null));
    }
}
