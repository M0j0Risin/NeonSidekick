using System.Collections.Concurrent;
using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Shopping;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Shopping tab's list controls over a real SQLite list (2026-10-16, the user's ask): each action changes the list, writes one WebUi audit line,
/// flashes on the PC and recounts the home bar's 🛒; a repeat changes nothing and does none of them; a line gone meanwhile is not found; rename
/// merges and quantity floors at 1; the add box parses as the PC's add page, over 100 items refused whole; a past item re-added twice adds once;
/// the unavailable list refuses every post.
/// </summary>
public sealed partial class WebUiTests
{
    /// <summary>The WebUi log lines and the PC's flashes while it is held.</summary>
    private sealed class ShopWatch : IDisposable
    {
        private readonly SemaphoreSlim _landed = new(0);

        public ShopWatch(WebUiSession session)
        {
            session.FlashOnPc = text => { Flashes.Enqueue(text); _landed.Release(); };
            DiagnosticLog.Emitted += Record;
        }

        public ConcurrentQueue<string> Lines { get; } = new();

        public ConcurrentQueue<string> Flashes { get; } = new();

        /// <summary>The next flash to land (each action flashes once, so a test waits for it before the next).</summary>
        public string NextFlash()
        {
            Assert.True(_landed.Wait(TimeSpan.FromSeconds(10)), "no flash landed");
            Assert.True(Flashes.TryDequeue(out string? text));
            return text!;
        }

        public void Dispose()
        {
            DiagnosticLog.Emitted -= Record;
            _landed.Dispose();
        }

        private void Record(DiagnosticEvent e)
        {
            if (e.Category == "WebUi")
            {
                Lines.Enqueue(e.Message);
            }
        }
    }

    private ShopItem OnList(string name) => _list.Store.Items().Single(i => i.Name == name);

    // ── ⌖ place (2026-11-06, the user's ask) ─────────────────────────────────

    /// <summary>A store with Aisle 2 in Dairy, a section of its own (Bakery), and Milk on the list.</summary>
    private (ShopStore Shop, ShopLocation Aisle, ShopSection Bakery, ShopItem Milk) PlaceFixture()
    {
        Assert.DoesNotContain("Error", _list.AddStore("H-E-B"), StringComparison.Ordinal);
        var shop = _list.StoreFor(null, out _)!;
        _list.AddLocation(shop, "Aisle 2", "Dairy");
        _list.AddSection(shop, "Bakery");
        var milk = _list.Add("Milk", 1, shop)!.Item;
        return (shop, _list.Store.Locations(shop.Id).Single(), _list.Store.Sections(shop.Id).Single(s => s.Name == "Bakery"), milk);
    }

    [Fact]
    public void ShopPlaces_IsWhatThePcsPickerOffers_ItsRowMarked_ForgetOnlyWithAFact()
    {
        var (shop, aisle, _, milk) = PlaceFixture();

        var before = _session.ShopPlaces(milk.Id)!;
        Assert.Equal(ShoppingText.PlaceTitle(milk), before.Title);
        Assert.Equal(shop.Id, before.Store);
        Assert.Equal(ShoppingList.PlaceChoices(_list.Store.Locations(shop.Id), _list.Store.Sections(shop.Id)).Select(c => c.Label), before.Choices.Select(c => c.Label));
        Assert.Equal(-1, before.Current);
        Assert.False(before.CanForget);
        Assert.Null(before.Note);

        _list.PlaceItem(shop, milk, aisle, null);
        var after = _session.ShopPlaces(milk.Id)!;
        Assert.Equal(aisle.Id, after.Choices[after.Current].Location);
        Assert.True(after.CanForget);

        Assert.Null(_session.ShopPlaces(milk.Id + 100));   // a line gone
    }

    [Fact]
    public void ShopPlaces_WithNoStore_OrNoLayout_IsANoteAndNoRows()
    {
        var bare = _list.Add("Milk", 1, null)!.Item;
        var none = _session.ShopPlaces(bare.Id)!;
        Assert.Equal((ShoppingText.NoStoresYet, null, 0), (none.Note, none.Store, none.Choices.Count));

        _list.AddStore("Aldi");
        var shop = _list.StoreFor(null, out _)!;
        var empty = _session.ShopPlaces(bare.Id)!;
        Assert.Equal((WebUiText.NoLayoutOnPage(shop), shop.Id, 0), (empty.Note, empty.Store, empty.Choices.Count));
        Assert.DoesNotContain("(s, then l)", empty.Note, StringComparison.Ordinal);   // no PC keys on a phone
    }

    [Fact]
    public void ShopPlace_AtALocation_Audits_Flashes_AndClearsTheGuessMark()
    {
        using var watch = new ShopWatch(_session);
        var (shop, aisle, _, milk) = PlaceFixture();
        _list.Place(shop, "Milk", ShoppingKinds.Category, "Aisle 2", null, ShoppingSources.Guess);
        Assert.True(_list.PlaceOf("Milk", shop).IsGuess);

        var reply = _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Contains("Aisle 2", reply.Message, StringComparison.Ordinal);
        Assert.Equal(aisle.Id, _list.PlaceOf("Milk", shop).Location?.Id);
        Assert.False(_list.PlaceOf("Milk", shop).IsGuess);
        Assert.Equal(WebUiText.PlacedLogLine("Milk", "Aisle 2", IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.PlacedFlash("Milk", "Aisle 2"), watch.NextFlash());
        Assert.Equal(1, _shoppingChanges);
    }

    [Fact]
    public void ShopPlace_ASectionAlone_PlacesItThere()
    {
        var (shop, _, bakery, milk) = PlaceFixture();

        var reply = _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Section = bakery.Id }, null);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        var place = _list.PlaceOf("Milk", shop);
        Assert.Null(place.Location);
        Assert.Equal(bakery.Id, place.Section?.Id);
    }

    [Fact]
    public void ShopPlace_Forget_RemovesEverySpelling_AndAGoneFactChangesNothing()
    {
        using var watch = new ShopWatch(_session);
        var (shop, _, _, _) = PlaceFixture();
        var eggs = _list.Add("Eggs", 1, shop)!.Item;
        _list.Place(shop, "Egg", ShoppingKinds.Item, "Aisle 2", null, ShoppingSources.User);
        _list.Place(shop, "Eggs", ShoppingKinds.Item, "Aisle 2", null, ShoppingSources.User);
        Assert.Equal(2, _list.ItemFacts(shop, eggs.Name).Count);

        var reply = _session.ShopPlace(new WebShopPlace { Id = eggs.Id, Store = shop.Id, Forget = true }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Empty(_list.ItemFacts(shop, eggs.Name));
        Assert.Equal(WebUiText.PlacedLogLine("Eggs", null, IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.PlacedFlash("Eggs", null), watch.NextFlash());

        var again = _session.ShopPlace(new WebShopPlace { Id = eggs.Id, Store = shop.Id, Forget = true }, IPAddress.Loopback);
        Assert.Equal(WebOutcome.Ok, again.Outcome);
        Assert.Single(watch.Lines);
        Assert.Equal(1, _shoppingChanges);
    }

    /// <summary>
    /// A tap on the place a line already has (2026-11-07, the code review): nothing rewritten, logged or flashed, at a location and in a section alone.
    /// A model's guess there is still taken as the user's, and a name with two facts is still cleaned up to one.
    /// </summary>
    [Fact]
    public void ShopPlace_WhereTheLineAlreadyIs_ChangesNothing_ButAGuessOrASecondFactStillDoes()
    {
        using var watch = new ShopWatch(_session);
        var (shop, aisle, bakery, milk) = PlaceFixture();
        Assert.Equal(WebOutcome.Ok, _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id }, null).Outcome);
        Assert.Single(watch.Lines);
        watch.NextFlash();

        var again = _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id }, null);
        Assert.Equal((WebOutcome.Ok, (string?)null), (again.Outcome, again.Message));
        Assert.Single(watch.Lines);
        Assert.Equal(1, _shoppingChanges);

        Assert.Equal(WebOutcome.Ok, _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Section = bakery.Id }, null).Outcome);
        Assert.Equal(2, watch.Lines.Count);
        Assert.Equal(WebOutcome.Ok, _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Section = bakery.Id }, null).Outcome);
        Assert.Equal(2, watch.Lines.Count);

        // A guess at the same spot is still made the user's.
        var bread = _list.Add("Bread", 1, shop)!.Item;
        _list.Place(shop, "Bread", ShoppingKinds.Item, null, "Bakery", ShoppingSources.Guess);
        _session.ShopPlace(new WebShopPlace { Id = bread.Id, Store = shop.Id, Section = bakery.Id }, null);
        Assert.Equal(ShoppingSources.User, Assert.Single(_list.ItemFacts(shop, "Bread")).Source);
        Assert.Equal(3, watch.Lines.Count);

        // Two spellings at the very place: the tap still leaves one.
        var eggs = _list.Add("Eggs", 1, shop)!.Item;
        _list.Place(shop, "Egg", ShoppingKinds.Item, "Aisle 2", null, ShoppingSources.User);
        _list.Place(shop, "Eggs", ShoppingKinds.Item, "Aisle 2", null, ShoppingSources.User);
        _session.ShopPlace(new WebShopPlace { Id = eggs.Id, Store = shop.Id, Location = aisle.Id }, null);
        Assert.Single(_list.ItemFacts(shop, "Eggs"));
    }

    [Fact]
    public void ShopPlace_RefusesWhatHasAged_AndAnythingButOneChoice()
    {
        var (shop, aisle, bakery, milk) = PlaceFixture();
        using var watch = new ShopWatch(_session);

        // A line gone meanwhile.
        var gone = _session.ShopPlace(new WebShopPlace { Id = milk.Id + 100, Store = shop.Id, Location = aisle.Id }, null);
        Assert.Equal((WebOutcome.NotFound, WebUiText.NoSuchItem), (gone.Outcome, gone.Message));

        // Naming none, or two, of a location, a section or forget.
        foreach (var pick in new[]
        {
            new WebShopPlace { Id = milk.Id, Store = shop.Id },
            new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id, Section = bakery.Id },
            new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id, Forget = true },
            new WebShopPlace { Id = milk.Id, Store = shop.Id, Section = bakery.Id, Forget = true },
        })
        {
            var refused = _session.ShopPlace(pick, null);
            Assert.Equal((WebOutcome.Refused, WebUiText.PlaceNeedsOne), (refused.Outcome, refused.Message));
        }

        // A location of another store, and a store switched mid-pick.
        _list.AddStore("Aldi");
        var aldi = _list.Store.FindStore("Aldi")!;
        _list.AddLocation(aldi, "Aisle 9", null);
        var other = _list.Store.Locations(aldi.Id).Single();
        var foreign = _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = other.Id }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.NoSuchPlace(shop)), (foreign.Outcome, foreign.Message));

        _list.UseStore(aldi);
        var switched = _session.ShopPlace(new WebShopPlace { Id = milk.Id, Store = shop.Id, Location = aisle.Id }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.PlaceStoreChanged(aldi)), (switched.Outcome, switched.Message));

        Assert.Empty(_list.ItemFacts(shop, "Milk"));
        Assert.Empty(watch.Lines);
        Assert.Equal(0, _shoppingChanges);
    }

    [Fact]
    public void ShopAdd_ParsesTheBoxAsThePcsAddPage_AuditsOnce_Flashes_AndRecounts()
    {
        using var watch = new ShopWatch(_session);

        var reply = _session.ShopAdd(new WebTextBody { Text = "2 milk, eggs" }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Equal([("milk", 2), ("eggs", 1)], _list.Store.Items().Select(i => (i.Name, i.Quantity)));
        Assert.Contains("milk", reply.Message, StringComparison.Ordinal);
        Assert.Equal(WebUiText.AddedLogLine(["milk ×2", "eggs"], IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.AddedFlash(["milk ×2", "eggs"]), watch.NextFlash());
        Assert.Equal(1, _shoppingChanges);

        // A box with no item in it is refused with the page's own sentence, and nothing changes.
        var blank = _session.ShopAdd(new WebTextBody { Text = " , ;" }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.NothingToAdd), (blank.Outcome, blank.Message));
        Assert.Equal(1, _shoppingChanges);
        Assert.Single(watch.Lines);
    }

    [Fact]
    public void ShopAdd_OfMoreThanAHundred_IsRefusedWhole_WithNothingAdded()
    {
        string many = string.Join(", ", Enumerable.Range(1, WebShoppingView.MaxAdd + 1).Select(i => "item" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var reply = _session.ShopAdd(new WebTextBody { Text = many }, null);

        Assert.Equal((WebOutcome.Refused, WebUiText.TooManyToAdd(100)), (reply.Outcome, reply.Message));
        Assert.Empty(_list.Store.Items());
        Assert.Equal(0, _shoppingChanges);

        string hundred = string.Join(", ", Enumerable.Range(1, WebShoppingView.MaxAdd).Select(i => "item" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(WebOutcome.Ok, _session.ShopAdd(new WebTextBody { Text = hundred }, null).Outcome);
        Assert.Equal(100, _list.Store.Items().Count);
    }

    [Fact]
    public void ShopQuantity_IsAbsolute_FlooredAtOne_ARepeatChangesNothing_AndAGoneLineIsNotFound()
    {
        long milk = _list.Add("Milk", 1, null)!.Item.Id;
        using var watch = new ShopWatch(_session);

        Assert.Equal(WebOutcome.Ok, _session.ShopQuantity(new WebShopQuantity { Id = milk, Qty = 3 }, IPAddress.Loopback).Outcome);
        Assert.Equal(3, OnList("Milk").Quantity);
        Assert.Equal(WebUiText.QuantityLogLine("Milk ×3", IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.QuantityFlash("Milk ×3"), watch.NextFlash());
        Assert.Equal(1, _shoppingChanges);

        Assert.Equal(WebOutcome.Ok, _session.ShopQuantity(new WebShopQuantity { Id = milk, Qty = 3 }, null).Outcome);   // again: nothing
        Assert.Equal(1, _shoppingChanges);
        Assert.Single(watch.Lines);

        Assert.Equal(WebOutcome.Ok, _session.ShopQuantity(new WebShopQuantity { Id = milk, Qty = -4 }, null).Outcome);
        Assert.Equal(1, OnList("Milk").Quantity);   // floored at 1

        Assert.Equal(WebOutcome.Ok, _session.ShopQuantity(new WebShopQuantity { Id = milk, Qty = int.MaxValue }, null).Outcome);
        Assert.Equal(ShoppingList.MaxQuantity, OnList("Milk").Quantity);   // capped (2026-10-19): the PC's + wrapped int.MaxValue round to 1

        var gone = _session.ShopQuantity(new WebShopQuantity { Id = 9999, Qty = 2 }, null);
        Assert.Equal((WebOutcome.NotFound, WebUiText.NoSuchItem), (gone.Outcome, gone.Message));
    }

    [Fact]
    public void ShopRename_RenamesOrMerges_TheSameNameIsNothing_AndABlankOneIsRefused()
    {
        long milk = _list.Add("Milk", 1, null)!.Item.Id;
        _list.Add("Oat milk", 2, null);
        long eggs = _list.Add("Eggs", 1, null)!.Item.Id;
        using var watch = new ShopWatch(_session);

        Assert.Equal(WebOutcome.Ok, _session.ShopRename(new WebShopRename { Id = eggs, Name = " Eggs " }, null).Outcome);   // its own name: nothing
        Assert.Empty(watch.Lines);
        Assert.Equal(0, _shoppingChanges);

        var renamed = _session.ShopRename(new WebShopRename { Id = eggs, Name = "Free-range eggs" }, IPAddress.Loopback);
        Assert.Equal((WebOutcome.Ok, ShoppingText.Renamed("item", "Eggs", "Free-range eggs")), (renamed.Outcome, renamed.Message));
        Assert.Equal(WebUiText.RenamedLogLine("Eggs", "Free-range eggs", IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.RenamedFlash("Eggs", "Free-range eggs"), watch.NextFlash());

        // A name another line has merges the two, as at the PC: the reply's sentence is the merge's.
        var merged = _session.ShopRename(new WebShopRename { Id = milk, Name = "oat milk" }, null);
        Assert.Equal(WebOutcome.Ok, merged.Outcome);
        Assert.Contains("merged", merged.Message, StringComparison.Ordinal);
        Assert.Equal(3, OnList("Oat milk").Quantity);
        Assert.DoesNotContain(_list.Store.Items(), i => i.Id == milk);
        Assert.Equal(2, _shoppingChanges);

        var blank = _session.ShopRename(new WebShopRename { Id = eggs, Name = "  " }, null);
        Assert.Equal(WebOutcome.Refused, blank.Outcome);
        Assert.StartsWith("Error: ", blank.Message, StringComparison.Ordinal);
        Assert.Equal(WebOutcome.NotFound, _session.ShopRename(new WebShopRename { Id = milk, Name = "x" }, null).Outcome);   // merged away
    }

    [Fact]
    public void ShopRemove_TakesTheLineOff_AuditsAndFlashes_AndASecondIsNotFound()
    {
        long milk = _list.Add("Milk", 2, null)!.Item.Id;
        using var watch = new ShopWatch(_session);

        var reply = _session.ShopRemove(new WebShopId { Id = milk }, IPAddress.Loopback);

        Assert.Equal(WebOutcome.Ok, reply.Outcome);
        Assert.Empty(_list.Store.Items());
        Assert.Equal(WebUiText.RemovedLogLine("Milk ×2", IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.RemovedFlash("Milk ×2"), watch.NextFlash());
        Assert.Equal(1, _shoppingChanges);
        Assert.Equal(WebOutcome.NotFound, _session.ShopRemove(new WebShopId { Id = milk }, null).Outcome);
        Assert.Equal(1, _shoppingChanges);
    }

    [Fact]
    public void ShopCheckAll_AndClearTicked_CountWhatChanged_AndARepeatDoesNothing()
    {
        _list.Add("Milk", 1, null);
        _list.Add("Eggs", 1, null);
        using var watch = new ShopWatch(_session);

        var all = _session.ShopCheckAll(new WebShopCheckAll { On = true }, IPAddress.Loopback);
        Assert.Equal((WebOutcome.Ok, ShoppingText.CheckedAll(2, on: true)), (all.Outcome, all.Message));
        Assert.All(_list.Store.Items(), i => Assert.True(i.Checked));
        Assert.Equal(WebUiText.CheckedAllLogLine(2, true, IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.CheckedAllFlash(2, true), watch.NextFlash());
        Assert.False(_session.Shopping().AnyOpen);   // every line ticked: ☐ none

        var again = _session.ShopCheckAll(new WebShopCheckAll { On = true }, null);
        Assert.Equal((WebOutcome.Ok, ShoppingText.CheckedAll(0, on: true)), (again.Outcome, again.Message));
        Assert.Equal(1, _shoppingChanges);
        Assert.Single(watch.Lines);

        var cleared = _session.ShopClearTicked(IPAddress.Loopback);
        Assert.Equal((WebOutcome.Ok, ShoppingText.Cleared(2, all: false)), (cleared.Outcome, cleared.Message));
        Assert.Empty(_list.Store.Items());
        Assert.Equal(WebUiText.ClearedLogLine(2, IPAddress.Loopback), watch.Lines.Last());
        Assert.Equal(WebUiText.ClearedFlash(2), watch.NextFlash());
        Assert.Equal(2, _shoppingChanges);

        Assert.Equal(WebOutcome.Ok, _session.ShopClearTicked(null).Outcome);   // nothing ticked: no line, no recount
        Assert.Equal(2, _shoppingChanges);
        Assert.Equal(2, watch.Lines.Count);
    }

    [Fact]
    public void PastItems_AreListed_ReAddedOnce_AndForgotten()
    {
        var bread = _list.Add("Bread", 2, null)!.Item;
        _list.Check(bread, true);
        _list.Clear(all: false);   // bought: remembered among the past items
        using var watch = new ShopWatch(_session);

        var past = Assert.Single(_session.ShopBought()!.Items);
        Assert.Equal(("Bread", 2), (past.Name, past.Qty));
        Assert.StartsWith("Bread ×2 · once · last ", past.Text, StringComparison.Ordinal);
        Assert.Equal(ShoppingText.BoughtCaption(1), _session.ShopBought()!.Caption);

        var readd = _session.ShopReAdd(new WebShopId { Id = past.Id }, IPAddress.Loopback);
        Assert.Equal(WebOutcome.Ok, readd.Outcome);
        Assert.Equal(2, OnList("Bread").Quantity);
        Assert.Equal(WebUiText.AddedLogLine(["Bread ×2"], IPAddress.Loopback), Assert.Single(watch.Lines));
        Assert.Equal(WebUiText.AddedFlash(["Bread ×2"]), watch.NextFlash());
        Assert.Empty(_session.ShopBought()!.Items);   // on the list now

        // A quick second tap finds nothing: the quantity is never bumped twice.
        var twice = _session.ShopReAdd(new WebShopId { Id = past.Id }, null);
        Assert.Equal((WebOutcome.NotFound, WebUiText.NoSuchPastItem), (twice.Outcome, twice.Message));
        Assert.Equal(2, OnList("Bread").Quantity);
        Assert.Equal(1, _shoppingChanges);

        _list.Check(OnList("Bread"), true);
        _list.Clear(all: false);
        past = Assert.Single(_session.ShopBought()!.Items);
        var forgot = _session.ShopForget(new WebShopId { Id = past.Id }, IPAddress.Loopback);
        Assert.Equal(WebOutcome.Ok, forgot.Outcome);
        Assert.Empty(_session.ShopBought()!.Items);
        Assert.Equal(ShoppingText.NothingBoughtYet, _session.ShopBought()!.Caption);
        Assert.Equal(WebUiText.ForgotLogLine("Bread", IPAddress.Loopback), watch.Lines.Last());
        Assert.Equal(WebUiText.ForgotFlash("Bread"), watch.NextFlash());
        Assert.Equal(WebOutcome.NotFound, _session.ShopForget(new WebShopId { Id = past.Id }, null).Outcome);
    }

    [Fact]
    public void TheList_CarriesItsControls_NamesQuantities_AndWords()
    {
        _list.Add("Milk", 2, null, "whole");

        var page = _session.Shopping();

        Assert.True(page.Writable);
        Assert.True(page.AnyOpen);
        Assert.Same(WebUiText.ShopWords, page.Words);
        var milk = Assert.Single(Assert.Single(page.Groups).Items);
        Assert.Equal(("Milk", 2), (milk.Name, milk.Qty));

        // The PC's button words, their Windows Terminal spacing collapsed.
        Assert.Equal(("☑ all", "☐ none", "✎ rename"), (WebUiText.ShopWords.TickAll, WebUiText.ShopWords.UntickAll, WebUiText.ShopWords.Rename));
        Assert.DoesNotContain(new[] { WebUiText.ShopWords.TickAll, WebUiText.ShopWords.Clear, WebUiText.ShopWords.Add, WebUiText.ShopWords.Less }, w => w.Contains("  ", StringComparison.Ordinal));

        // An empty but available list keeps its controls: an empty list is when adding matters most.
        _list.Remove(OnList("Milk"));
        Assert.True(_session.Shopping().Writable);
    }

    [Fact]
    public void AnUnavailableList_IsNotWritable_AndRefusesEveryPost()
    {
        string dir = Path.Combine(_dir, "dead");
        var dead = new ShoppingStore(dir, _time);
        dead.Dispose();   // closed: unavailable from now on
        var list = new ShoppingList(() => dead);
        int changes = 0;
        using var session = new WebUiSession(State, _ => WebReply.Ok, () => list, () => changes++, _time);

        var page = session.Shopping();
        Assert.False(page.Writable);
        Assert.Equal(ShoppingText.Unavailable, page.Note);
        Assert.Empty(session.ShopBought()!.Items);

        // The place sheet says why (2026-11-07, the code review): a null was a 404, which the page reads as the line gone.
        var sheet = session.ShopPlaces(1)!;
        Assert.Equal(("", ShoppingText.Unavailable, 0), (sheet.Title, sheet.Note, sheet.Choices.Count));
        using var noList = new WebUiSession(State, _ => WebReply.Ok, () => null, () => changes++, _time);
        Assert.Equal(ShoppingText.Unavailable, noList.ShopPlaces(1)!.Note);

        WebReply[] replies =
        [
            session.ShopAdd(new WebTextBody { Text = "milk" }, null),
            session.ShopReAdd(new WebShopId { Id = 1 }, null),
            session.ShopForget(new WebShopId { Id = 1 }, null),
            session.ShopQuantity(new WebShopQuantity { Id = 1, Qty = 2 }, null),
            session.ShopRename(new WebShopRename { Id = 1, Name = "x" }, null),
            session.ShopRemove(new WebShopId { Id = 1 }, null),
            session.ShopPlace(new WebShopPlace { Id = 1, Store = 1, Forget = true }, null),
            session.ShopCheckAll(new WebShopCheckAll { On = true }, null),
            session.ShopClearTicked(null),
            session.Check(new WebCheckRequest { Id = 1, On = true }, null),
            session.UseStore(new WebStoreRequest { Id = 1 }, null),
        ];
        Assert.All(replies, r => Assert.Equal((WebOutcome.Refused, ShoppingText.Unavailable), (r.Outcome, r.Message)));
        Assert.Equal(0, changes);

        // No list at all answers the same.
        using var none = new WebUiSession(State, _ => WebReply.Ok, () => null, () => changes++, _time);
        Assert.False(none.Shopping().Writable);
        Assert.Null(none.ShopBought());
        Assert.Equal(WebOutcome.Refused, none.ShopAdd(new WebTextBody { Text = "milk" }, null).Outcome);
    }
}
