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

        WebReply[] replies =
        [
            session.ShopAdd(new WebTextBody { Text = "milk" }, null),
            session.ShopReAdd(new WebShopId { Id = 1 }, null),
            session.ShopForget(new WebShopId { Id = 1 }, null),
            session.ShopQuantity(new WebShopQuantity { Id = 1, Qty = 2 }, null),
            session.ShopRename(new WebShopRename { Id = 1, Name = "x" }, null),
            session.ShopRemove(new WebShopId { Id = 1 }, null),
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
