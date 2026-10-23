using NeonSidekick.Diagnostics;
using NeonSidekick.HomeAssistant;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The home bar (2026-10-12) ─────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>How long a clicked chip waits for its state to change before it shows <see cref="HomeBarText.FailedMark"/>.</summary>
    public static readonly TimeSpan HomePendingWait = TimeSpan.FromSeconds(6);

    /// <summary>How long the failed mark shows.</summary>
    public static readonly TimeSpan HomeFailedShows = TimeSpan.FromSeconds(2);

    /// <summary>The least time between two rebuilds from the feed alone: a sensor reporting every second redraws no faster.</summary>
    public static readonly TimeSpan HomeRebuildEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the 🛒 chip reads the list's count (later on 2026-10-12): the model's tools change it on their own threads.</summary>
    public static readonly TimeSpan HomeShoppingEvery = TimeSpan.FromSeconds(1);

    /// <summary>The domains a room's pane lists, in this order.</summary>
    public static readonly string[] HomeRoomDomains = ["light", "switch", "fan", "input_boolean", "media_player", "cover", "lock", "climate"];

    /// <summary>The home bar's live feed: one WebSocket while the bar is shown.</summary>
    private readonly IHaLive _homeLive;

    /// <summary>The chips' double-clicks.</summary>
    private readonly DoubleClick _homeClicks;

    private readonly object _homeGate = new();

    // Chips clicked and waiting for their state to change, by reference.
    private readonly Dictionary<string, HomePending> _homePending = new(StringComparer.Ordinal);

    // A room pane's devices while it is open: kept current with the bar's.
    private readonly HashSet<string> _homeExtra = new(StringComparer.Ordinal);

    private int _homePage;

    // Moves with a click, a page, a room pane's devices: the cache's key beside the feed's version.
    private long _homeModel;

    private HomeCache? _homeCache;

    // The 🛒 chip's count: the lines to get as last read (-1 the list off, -2 not read yet), when the last read began (UTC ticks, read and
    // written whole: the drawing thread and the turn task both touch it), whether one runs, and the profile's generation (a read begun
    // before a switch never lands after it). Read on a pool thread so a tool holding the store's lock never stalls the pane; the first
    // read waits a moment for the lock at most (2026-10-13, the code review), and every read is made outside _homeGate.
    private int _homeShoppingOpen = -2;
    private long _homeShoppingAt;
    private int _homeShoppingReading;
    private int _homeShoppingGeneration;

    /// <summary>How long the 🛒 chip's first count waits for the store's lock on the drawing thread before it reads on the pool instead.</summary>
    private static readonly TimeSpan HomeShoppingFirstWait = TimeSpan.FromMilliseconds(20);

    // The Home Assistant URL and token as stored, as the bar last saw them: a change wakes the feed (the review's finding,
    // 2026-10-12: a key fixed in /settings, or a profile switch, waited for a wake that never came).
    private string? _homeServerKey;

    // A chip clicked: when, and the entities behind it as they were (any change ends the wait), and whether the wait ran out.
    private sealed record HomePending(DateTimeOffset Since, IReadOnlyList<HaLiveEntity?> Before, IReadOnlyList<string> Ids, bool Failed);

    // The bar as last built, and what it was built from.
    private sealed record HomeCache(string ItemsKey, HaCatalog? Catalog, long Model, IReadOnlySet<string> Ids, long Live, int Cells, ThemePalette Palette, DateTimeOffset Built, IReadOnlyList<HomeChip> Chips, HomeRow? Row);

    /// <summary>
    /// The home bar's row for the settings in force (2026-10-12, the user's ask) in <paramref name="cells"/> cells: null — no
    /// row, the feed told the bar is hidden — while <c>Home bar</c> has nothing on it; else the chips (<see cref="HomeChips"/>)
    /// over the live feed, rendered by <see cref="HomeBar.Render"/>. Asked several times a pane tick, so the row is cached and
    /// rebuilt only when the items, the feed's version, a click, the page, the width or the theme moved — the feed's version at
    /// most every <see cref="HomeRebuildEvery"/> — and a pending chip's wait running out. The feed is told the entities to keep
    /// current (<see cref="IHaLive.Want"/>) when they change. Never waits on the network.
    /// </summary>
    private HomeRow? HomeBarRow(int cells)
    {
        var effective = _effective();
        var items = HomeBarItems.Resolve(effective.HomeBarItems);
        string serverKey = (effective.HomeAssistantUrl ?? "").Trim() + "\n" + (effective.HomeAssistantToken ?? "");
        if (!string.Equals(Interlocked.Exchange(ref _homeServerKey, serverKey), serverKey, StringComparison.Ordinal))
        {
            _homeLive.Nudge();
        }

        if (items.Count == 0)
        {
            lock (_homeGate)
            {
                _homeCache = null;
                _homeBarIds = null;
            }

            HomeWantSync();   // the Web UI's Home tab may still want its own (2026-10-15)
            return null;
        }

        var live = _homeLive.Snapshot;
        var now = _time.GetUtcNow();
        IReadOnlySet<string>? want = null;
        HomeRow? row;
        // The 🛒 count before the gate (2026-10-13, the code review): a read never holds the bar's lock.
        int? shopping = items.Contains(HomeBarItems.Shopping, StringComparer.Ordinal) ? HomeShoppingOpen(now) : null;
        lock (_homeGate)
        {
            // The items with their aliases (2026-10-12) and emoji (later that day): a rename redraws at once, as an added item does.
            var aliases = effective.HomeBarAliases;
            var glyphs = effective.HomeBarGlyphs;
            // The groups too (later on 2026-10-13, the user's ask: pinned left, center or right): a pin redraws at once.
            var sides = HomeBarItems.Sides(items, effective.HomeBarLeft, effective.HomeBarRight);
            string key = string.Join('\n', items.Select(i => (HomeBarItems.AliasOf(aliases, i) is { } alias ? i + "=" + alias : i) + (HomeBarItems.GlyphOf(glyphs, i) is { } glyph ? "#" + glyph : "")))
                + "\n🛒" + (shopping?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?")
                + "\n<" + string.Join(' ', sides.Left) + "\n>" + string.Join(' ', sides.Right);
            var cache = _homeCache;
            bool sameItems = cache is not null && string.Equals(cache.ItemsKey, key, StringComparison.Ordinal) && cache.Model == _homeModel;
            var ids = sameItems && ReferenceEquals(cache!.Catalog, live.Catalog) ? cache.Ids : HomeIds(items, live.Catalog);
            if (cache is null || !ReferenceEquals(ids, cache.Ids))
            {
                want = ids;
                _homeBarIds = ids;
            }

            bool due = cache is null || !sameItems || cache.Cells != cells || !ReferenceEquals(cache.Palette, Theme.Current) || !ReferenceEquals(cache.Ids, ids)
                || HomeExpiryDue(now)
                || (cache.Live != live.Version && now - cache.Built >= HomeRebuildEvery);
            if (!due)
            {
                return cache!.Row;
            }

            UpdateHomePending(live, now);
            bool stale = live.Status != HaLiveStatus.Live;
            // Each group's chips, a divider only between two of them (2026-10-13); the cache keeps them left, center, right, as the spans count them.
            // The monitors' 🖥️ only where there is a monitor system (2026-10-23: a profile copied to a Mac keeps it saved, unshown).
            List<HomeChip> Chips(IReadOnlyList<string> group) => HomeBar.ShownDividers(group.Where(i => _monitors is not null || !HomeBarItems.IsMonitors(i)).Select(i => HomeChips.Build(i, live.Entities, live.Catalog, HomeMarkOf(i), stale, aliases, glyphs, shopping)).ToList());
            var (left, center, right) = (Chips(sides.Left), Chips(sides.Center ?? []), Chips(sides.Right));
            List<HomeChip> chips = [.. left, .. center, .. right];
            row = HomeBar.Render(left, center, right, cells, _homePage);
            if (row is not null && _homePage >= row.Pages)
            {
                _homePage = row.Pages - 1;
                row = HomeBar.Render(left, center, right, cells, _homePage);
            }

            _homeCache = new HomeCache(key, live.Catalog, _homeModel, ids, live.Version, cells, Theme.Current, now, chips, row);
        }

        if (want is not null)
        {
            HomeWantSync();   // the bar's, with the Web UI's Home tab's while a page holds it (2026-10-15)
        }

        return row;
    }

    /// <summary>
    /// The 🛒 chip's count (later on 2026-10-12): the lines to get as last read, a new read started on a pool thread at most every
    /// <see cref="HomeShoppingEvery"/> (<see cref="Shopping.ShoppingStore.OpenCount"/>); the first read is made here, so the chip never
    /// shows a count it has not read. Null while the list is off.
    /// </summary>
    private int? HomeShoppingOpen(DateTimeOffset now)
    {
        long ticks = now.UtcTicks;
        int generation = Volatile.Read(ref _homeShoppingGeneration);
        var store = _shopping.Store;
        if (Volatile.Read(ref _homeShoppingOpen) == -2 && Interlocked.CompareExchange(ref _homeShoppingReading, 1, 0) == 0)
        {
            // The first read here, unless a write holds the store (then on the pool, the chip ? until it lands).
            Interlocked.Exchange(ref _homeShoppingAt, ticks);
            if (store.TryOpenCount(HomeShoppingFirstWait, out int? first))
            {
                Land(first);
                Volatile.Write(ref _homeShoppingReading, 0);
            }
            else
            {
                _ = Task.Run(ReadAndLand);
            }
        }
        else if (ticks - Interlocked.Read(ref _homeShoppingAt) >= HomeShoppingEvery.Ticks && Interlocked.CompareExchange(ref _homeShoppingReading, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _homeShoppingAt, ticks);
            _ = Task.Run(ReadAndLand);
        }

        int open = Volatile.Read(ref _homeShoppingOpen);
        return open >= 0 ? open : null;

        void ReadAndLand()
        {
            try
            {
                Land(store.OpenCount());
            }
            finally
            {
                Volatile.Write(ref _homeShoppingReading, 0);
            }
        }

        // A read begun under another profile is dropped: the new profile's store reads afresh.
        void Land(int? count)
        {
            if (Volatile.Read(ref _homeShoppingGeneration) == generation)
            {
                Volatile.Write(ref _homeShoppingOpen, count ?? -1);
            }
        }
    }

    /// <summary>The 🛒 chip reads its count again at the next draw: <c>/shopping</c> or its pane just ran.</summary>
    private void HomeShoppingChanged() => Interlocked.Exchange(ref _homeShoppingAt, 0);

    /// <summary>The 🛒 chip forgets the last profile's count (2026-10-13, the code review): read afresh from the loaded one's store.</summary>
    private void HomeShoppingForget()
    {
        Interlocked.Increment(ref _homeShoppingGeneration);
        Volatile.Write(ref _homeShoppingOpen, -2);
        Interlocked.Exchange(ref _homeShoppingAt, 0);
    }

    // The entities the bar keeps current: its items' (an area's lights) and an open room pane's devices.
    private IReadOnlySet<string> HomeIds(IReadOnlyList<string> items, HaCatalog? catalog)
    {
        var ids = new HashSet<string>(HomeBarItems.EntityIds(items, catalog), StringComparer.Ordinal);
        ids.UnionWith(_homeExtra);
        return ids;
    }

    // Under _homeGate: a pending chip's mark.
    private HomeChipMark HomeMarkOf(string reference) =>
        _homePending.TryGetValue(reference, out var pending) ? (pending.Failed ? HomeChipMark.Failed : HomeChipMark.Pending) : HomeChipMark.None;

    // Under _homeGate: whether a pending chip's wait (or its failed mark) has run out since the last build.
    private bool HomeExpiryDue(DateTimeOffset now) =>
        _homePending.Values.Any(p => now - p.Since >= (p.Failed ? HomeFailedShows : HomePendingWait));

    // Under _homeGate: the pending chips against the feed — a change ends the wait; a wait run out shows the failed mark, then goes. Any change
    // moves _homeModel (2026-10-19, the review: the Web UI's Home tab runs this too, and a mark it changed left the bar's cached row as it was).
    private void UpdateHomePending(HaLiveSnapshot live, DateTimeOffset now)
    {
        bool marked = false;
        foreach (var (reference, pending) in _homePending.ToList())
        {
            bool moved = pending.Ids.Select((id, i) => !ReferenceEquals(live.Entities.GetValueOrDefault(id), pending.Before[i])).Any(changed => changed);
            if (moved || (pending.Failed && now - pending.Since >= HomeFailedShows))
            {
                _homePending.Remove(reference);
                marked = true;
            }
            else if (!pending.Failed && now - pending.Since >= HomePendingWait)
            {
                _homePending[reference] = pending with { Since = now, Failed = true };
                DiagnosticLog.Info(HaText.Category, HomeBarText.NoChange(HomeBarItems.NameOf(reference, live.Catalog)));
                marked = true;
            }
        }

        if (marked)
        {
            _homeModel++;
        }
    }

    /// <summary>
    /// A click on the screen (2026-10-12, <see cref="KeySource.HomeBarClick"/>, on the reading task): not the bar's, or the bar's
    /// — a single click on a chip flashes its note, a second within the pair's interval does what the chip does (a call at once,
    /// marked pending until the state changes; or the line that asks or opens its pane); a right-click opens its pane; ‹ › turn
    /// the page (the ⌂ status glyph's note and its double-click to the picker went later on 2026-10-12, the user's call). A click on the bar's blanks is not the bar's: it
    /// goes on to the readers as a click on the toolbar's blanks (<see cref="ScreenPane.TryHitToolbar"/>), so a double-click there
    /// opens <c>/settings</c> as on every other bar (2026-10-12, the user's ask; spent until then). Since 2026-10-13 (the user's ask: as
    /// on the home bar) a right-click on a toolbar glyph is its double-click's line too (<see cref="ToolbarRightClick"/>), and later that
    /// day (the user's ask) a right-click on a bar's open space is the blanks' <c>/settings</c>.
    /// </summary>
    /// <summary>
    /// A right-click on a toolbar glyph (2026-10-13, the user's ask: as a home bar chip's): its double-click's line
    /// (<see cref="ToolbarWord"/>), read as a command chord's wherever the click is read — the idle line, a pane, under a reply — and never
    /// the line's paste. The path, the model's name and its mark are not ours (the user's call). Later on 2026-10-13 (the user's ask: "just
    /// like double-clicking" there) a bar's open space is <c>/settings</c> too, as its double-click is: the toolbar's blanks and dividers,
    /// anywhere on the performance bar and the home bar's blanks (<see cref="ScreenPane.ToolbarZone.Row"/>), and the hint row's
    /// (<see cref="ScreenPane.HintZone.Row"/>: its text and blanks, never a glyph, the name, the path, the timers, the tally or the
    /// scroll's row) — a right-click anywhere else still pastes. Under an open pane the bars are off their hit tests, so the click is read
    /// as the off-pane pair's (<see cref="ScreenPane.OffPaneHitAt"/>), and the line closes the pane it names or switches to another, as
    /// that pair does.
    /// </summary>
    private HomeClick ToolbarRightClick(InputEvent.Click click)
    {
        if (click.Button != MouseButton.Right)
        {
            return HomeClick.NotOurs;
        }

        var off = _pane.OverlayOpen ? _pane.OffPaneHitAt(click.X, click.Y) : null;
        ScreenPane.ToolbarHit? tool = _pane.TryHitToolbar(click.X, click.Y, out var hit) ? hit : off?.Toolbar;
        if (tool is { Zone: ScreenPane.ToolbarZone.Glyph } glyph)
        {
            return ToolbarWord(glyph.Glyph) is { } line ? HomeClick.Run(line) : HomeClick.NotOurs;
        }

        ScreenPane.HintHit? hint = tool is null && _pane.TryHitHint(click.X, click.Y, out var row) ? row : off?.Hint;
        return tool is { Zone: ScreenPane.ToolbarZone.Row } || hint is { Zone: ScreenPane.HintZone.Row } ? HomeClick.Run(SlashCommands.SettingsWord) : HomeClick.NotOurs;
    }

    private HomeClick HomeBarClick(InputEvent.Click click)
    {
        if (!_pane.TryHitHomeBar(click.X, click.Y, out var span))
        {
            return ToolbarRightClick(click);
        }

        HomeCache? cache;
        lock (_homeGate)
        {
            cache = _homeCache;
        }

        if (cache is null)
        {
            return HomeClick.Spent;
        }

        switch (span.Target)
        {
            case HomeTarget.Prev or HomeTarget.Next:
                _homeClicks.Reset();
                lock (_homeGate)
                {
                    _homePage = Math.Max(0, _homePage + (span.Target == HomeTarget.Next ? 1 : -1));
                    _homeModel++;
                }

                return HomeClick.Spent;
        }

        if (span.Index < 0 || span.Index >= cache.Chips.Count)
        {
            return HomeClick.Spent;
        }

        var chip = cache.Chips[span.Index];
        if (click.Button == MouseButton.Right)
        {
            _homeClicks.Reset();
            return HomeClick.Run(HomeBarText.Command + " " + HomeBarText.ShowWord + " " + chip.Reference);
        }

        if (!_homeClicks.Second(span.Index))
        {
            _pane.Flash(chip.Note);
            return HomeClick.Spent;
        }

        switch (chip.Action)
        {
            case HomeChipAction.Call when chip.Call is { } call:
                StartHomeCall(chip.Reference, chip.Name, call, cache.Catalog);
                return HomeClick.Spent;
            case HomeChipAction.Confirm:
                return HomeClick.Run(HomeBarText.Command + " " + HomeBarText.TapWord + " " + chip.Reference);
            case HomeChipAction.Detail:
                return HomeClick.Run(HomeBarText.Command + " " + HomeBarText.ShowWord + " " + chip.Reference);
            default:
                _pane.Flash(chip.Note);
                return HomeClick.Spent;
        }
    }

    /// <summary>A call from the bar or a chip's pane: the chip marked pending, the call on a task of its own, a failure flashed.</summary>
    private void StartHomeCall(string reference, string name, HaServiceCall call, HaCatalog? catalog)
    {
        var live = _homeLive.Snapshot;
        var ids = HomeBarItems.EntityIds([reference], catalog ?? live.Catalog).ToList();
        lock (_homeGate)
        {
            _homePending[reference] = new HomePending(_time.GetUtcNow(), ids.Select(id => live.Entities.GetValueOrDefault(id)).ToList(), ids, Failed: false);
            _homeModel++;
        }

        _ = RunHomeCallAsync(reference, name, call);
    }

    private async Task RunHomeCallAsync(string reference, string name, HaServiceCall call)
    {
        HaReply reply;
        try
        {
            reply = await _homeLive.CallAsync(call, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reply = HaReply.Failed(ex.Message);
        }

        if (reply.Ok)
        {
            return;
        }

        DiagnosticLog.Info(HaText.Category, "The home bar's " + call.Name + " on " + name + " failed: " + reply.Error);
        lock (_homeGate)
        {
            _homePending.Remove(reference);
            _homeModel++;
        }

        _pane.Flash(HomeBarText.Failed(name, reply.Error ?? ""));
    }

    /// <summary>Whether <c>/homebar</c>'s argument is a show or hide (bare, <c>on</c>, <c>off</c>): display only, run at once anywhere. Pure.</summary>
    internal static bool HomeBarToggles(string args)
    {
        string word = args.Trim();
        return word.Length == 0
            || string.Equals(word, HomeBarText.OnWord, StringComparison.OrdinalIgnoreCase)
            || string.Equals(word, HomeBarText.OffWord, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>/homebar [on|off]</c> where no pane may open (under a reply, a chord in a pane): the bar shown or hidden
    /// (<see cref="HomeBarItems.Toggle"/>); with nothing ever picked, a notice naming the picker.
    /// </summary>
    private void HandleHomeBarToggle(string args)
    {
        if (ApplyHomeBarToggle(args) is { NeedsPicker: true })
        {
            _transcript.Notice(HomeBarText.NothingPicked);
        }
    }

    // The toggle saved, its notice written; the toggle (null after the usage error).
    private HomeToggle? ApplyHomeBarToggle(string args)
    {
        var saved = _settings.Current;
        if (HomeBarItems.Toggle(args, saved.HomeBarItems, saved.HomeBarLastItems) is not { } next)
        {
            _transcript.Error(HomeBarText.Usage);
            return null;
        }

        if (next.NeedsPicker)
        {
            return next;
        }

        _settings.Update(d =>
        {
            d.HomeBarItems = next.Items;
            d.HomeBarLastItems = next.LastItems;
        });
        _transcript.Notice(next.Items.Count > 0 ? HomeBarText.Shown : HomeBarText.Hidden);
        return next;
    }

    /// <summary>
    /// <c>/homebar …</c> where a pane may open (the idle line; under a reply on the watcher): a toggle (the picker when nothing
    /// was ever picked), <c>edit</c> the picker, <c>tap</c> a chip's double-click that asks first, <c>show</c> its pane, <c>name</c>
    /// an item's alias (<see cref="HomeRename"/>), <c>emoji</c> its emoji (<see cref="HomeEmoji"/>).
    /// </summary>
    private async Task HandleHomeBarAsync(string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (HomeBarToggles(args))
        {
            if (ApplyHomeBarToggle(args) is { NeedsPicker: true })
            {
                await _menu.EditHomeBarAsync(own: true, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        string[] parts = args.Trim().Split(' ', 2, StringSplitOptions.TrimEntries);
        string word = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? parts[1] : "";
        switch (word)
        {
            case HomeBarText.EditWord when rest.Length == 0:
                await _menu.EditHomeBarAsync(own: true, cancellationToken).ConfigureAwait(false);
                break;
            case HomeBarText.TapWord when rest.Length > 0:
                await HomeTapAsync(rest, sink, cancellationToken).ConfigureAwait(false);
                break;
            case HomeBarText.ShowWord when rest.Length > 0:
                await HomeShowAsync(rest, sink, cancellationToken).ConfigureAwait(false);
                break;
            case HomeBarText.NameWord when rest.Contains('=', StringComparison.Ordinal):
                HomeRename(rest, sink);
                break;
            case HomeBarText.EmojiWord when rest.Contains('=', StringComparison.Ordinal):
                HomeEmoji(rest, sink);
                break;
            default:
                sink.Error(HomeBarText.Usage);
                break;
        }
    }

    /// <summary>The chip on the bar <paramref name="item"/> names: its reference, or its name (any case); null when none is.</summary>
    private HomeChip? FindHomeChip(string item)
    {
        HomeCache? cache;
        lock (_homeGate)
        {
            cache = _homeCache;
        }

        if (cache is null)
        {
            return null;
        }

        string? reference = HomeBarItems.Normalize(item);
        var chips = cache.Chips.Where(c => !c.Divider).ToList();   // a divider is never the one named (2026-10-13)
        return chips.FirstOrDefault(c => string.Equals(c.Reference, reference, StringComparison.Ordinal))
            ?? chips.FirstOrDefault(c => string.Equals(c.Label, item.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? chips.FirstOrDefault(c => string.Equals(c.Name, item.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The item on the bar <paramref name="item"/> names for <c>name</c> and <c>emoji</c> (<see cref="HomeBarItems.Find"/>), else the
    /// drawn chip it names (<see cref="FindHomeChip"/>, later on 2026-10-12: the live feed reads the registries without the states,
    /// so an entity's friendly name, "Desk", is only on its chip), and the item's name as its chip has it; null when none is.
    /// </summary>
    private (string Reference, string Name)? FindHomeItem(string item, Settings.AppSettingsData saved)
    {
        var catalog = _homeLive.Snapshot.Catalog;
        if ((HomeBarItems.Find(HomeBarItems.Resolve(saved.HomeBarItems), item, catalog, saved.HomeBarAliases) ?? FindHomeChip(item)?.Reference) is not { } reference)
        {
            return null;
        }

        return (reference, FindHomeChip(reference)?.Name ?? HomeBarItems.NameOf(reference, catalog));
    }

    /// <summary>
    /// <c>/homebar name &lt;item&gt; = &lt;alias&gt;</c> (2026-10-12, the user's ask): the chip of an item on the bar (its reference,
    /// alias, name or id, <see cref="HomeBarItems.Find"/>) labelled <c>alias</c>; blank the emoji alone (later that day), <c>default</c>
    /// or the item's own name takes the alias off.
    /// </summary>
    private void HomeRename(string rest, INoticeSink sink)
    {
        int equals = rest.IndexOf('=', StringComparison.Ordinal);
        string item = rest[..equals].Trim();
        string alias = rest[(equals + 1)..].Trim();
        var saved = _settings.Current;
        if (FindHomeItem(item, saved) is not { } found)
        {
            sink.Error(HomeBarText.NotOnBar(item));
            return;
        }

        var (reference, name) = found;
        if (HomeBarItems.IsNameless(reference))
        {
            sink.Error(HomeBarText.Nameless(name));
            return;
        }

        // Nothing after the = is the emoji alone, default the item's own name again (later on 2026-10-12; until then nothing took it off).
        bool reset = string.Equals(alias, HomeBarText.DefaultWord, StringComparison.OrdinalIgnoreCase);
        var next = HomeBarItems.Rename(saved.HomeBarAliases, reference, reset ? null : alias, name);
        _settings.Update(d => d.HomeBarAliases = next);
        sink.Notice(HomeBarItems.AliasOf(next, reference) switch
        {
            null => HomeBarText.Unnamed(name),
            "" => HomeBarText.Bared(name),
            var shown => HomeBarText.Renamed(name, shown),
        });
    }

    /// <summary>
    /// <c>/homebar emoji &lt;item&gt; = &lt;emoji&gt;</c> (later on 2026-10-12, the user's ask): the chip of an item on the bar
    /// (<see cref="HomeBarItems.Find"/>) drawn with <c>emoji</c>; blank, <c>default</c> or the item's own glyph takes it off. Anything
    /// but one emoji (<see cref="Emojis.AvatarRules.IsEmoji"/>) is refused, so the chip keeps its width.
    /// </summary>
    private void HomeEmoji(string rest, INoticeSink sink)
    {
        int equals = rest.IndexOf('=', StringComparison.Ordinal);
        string item = rest[..equals].Trim();
        string emoji = rest[(equals + 1)..].Trim();
        var saved = _settings.Current;
        var live = _homeLive.Snapshot;
        if (FindHomeItem(item, saved) is not { } found)
        {
            sink.Error(HomeBarText.NotOnBar(item));
            return;
        }

        var (reference, name) = found;

        bool reset = emoji.Length == 0 || string.Equals(emoji, HomeBarText.DefaultWord, StringComparison.OrdinalIgnoreCase);
        if (!reset && !Emojis.AvatarRules.IsEmoji(emoji))
        {
            sink.Error(HomeBarText.NotEmoji(emoji));
            return;
        }

        HomeBarItems.TryParse(reference, out _, out string id);
        string own = HomeChips.DefaultGlyph(reference, live.Catalog, live.Entities.GetValueOrDefault(id));
        var next = HomeBarItems.SetGlyph(saved.HomeBarGlyphs, reference, reset ? null : emoji, own);
        _settings.Update(d => d.HomeBarGlyphs = next);
        sink.Notice(HomeBarItems.GlyphOf(next, reference) is { } shown ? HomeBarText.GlyphSet(name, shown) : HomeBarText.GlyphReset(name, own));
        if (!reset && HomeBarItems.GlyphOf(next, reference) is { } picked)
        {
            Emojis.EmojiPrefs.Load(_settings.ProfileDirectory).Picked(picked);
        }
    }

    /// <summary><c>/homebar tap &lt;item&gt;</c>: the chip's double-click — a call, or the Yes/No first for an unlock, a guarded opening, a disarm.</summary>
    private async Task HomeTapAsync(string item, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (FindHomeChip(item) is not { } chip)
        {
            sink.Error(HomeBarText.NotOnBar(item));
            return;
        }

        switch (chip.Action)
        {
            case HomeChipAction.Call when chip.Call is { } call:
                StartHomeCall(chip.Reference, chip.Name, call, null);
                break;
            case HomeChipAction.Confirm when chip.Call is { } asked:
                await ConfirmedHomeCallAsync(chip.Reference, chip.Name, asked, cancellationToken).ConfigureAwait(false);
                break;
            case HomeChipAction.Detail:
                await HomeShowAsync(item, sink, cancellationToken).ConfigureAwait(false);
                break;
            default:
                sink.Notice(chip.Note);
                break;
        }
    }

    // The Yes/No pane (the cursor on No), then the call on a yes; a no flashed.
    private async Task ConfirmedHomeCallAsync(string reference, string name, HaServiceCall call, CancellationToken cancellationToken)
    {
        if (!_menu.CanShowMenus() || !await _menu.ConfirmAsync(HomeBarText.Confirm(call, name), cancellationToken).ConfigureAwait(false))
        {
            _pane.Flash(HomeBarText.Declined(name));
            return;
        }

        StartHomeCall(reference, name, call, null);
    }

    /// <summary><c>/homebar show &lt;item&gt;</c>: a chip's pane — its actions, or a room's devices.</summary>
    private async Task HomeShowAsync(string item, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (FindHomeChip(item) is not { } chip || !HomeBarItems.TryParse(chip.Reference, out var kind, out string id))
        {
            sink.Error(HomeBarText.NotOnBar(item));
            return;
        }

        if (!_pane.Enabled)
        {
            sink.Notice(chip.Note);
            return;
        }

        try
        {
            if (kind == HomeItemKind.Shopping)
            {
                // The 🛒 chip (later on 2026-10-12): the list's own pane, as /shopping opens it.
                await HandleShoppingAsync("", sink, spinner: false, cancellationToken).ConfigureAwait(false);
            }
            else if (kind == HomeItemKind.Monitors)
            {
                // The 🖥️ chip (2026-10-23, the monitors plan): the monitors pane on its Layout tab.
                await ShowMonitorsAsync(null, sink, cancellationToken).ConfigureAwait(false);
            }
            else if (kind == HomeItemKind.Area)
            {
                await HomeRoomAsync(chip, id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await HomeEntityAsync(chip, id, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _menuPane.Close();
        }
    }

    // One entity's pane: its actions (HomeActions); a pick runs one (asking first where the chip would), and the pane closes.
    private async Task HomeEntityAsync(HomeChip chip, string id, CancellationToken cancellationToken)
    {
        var entity = _homeLive.Snapshot.Entities.GetValueOrDefault(id);
        string title = HomeBarText.DetailTitle(chip.Name, entity is null ? "" : HomeBarText.StateWord(entity.State));
        var actions = entity is null ? [] : HomeActions(entity);
        if (actions.Count == 0)
        {
            await _menuPane.ShowEmptyAsync(title, HomeBarText.NothingToDo, cancellationToken).ConfigureAwait(false);
            return;
        }

        var page = new MenuPage(title, actions.Select(a => Markup.Escape(a.Label)).ToList(), SettingsMenu.PickKeys);
        if (await _menuPane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } pick || pick.Row >= actions.Count)
        {
            return;
        }

        var (_, call, ask) = actions[pick.Row];
        if (ask)
        {
            _menuPane.Close();
            await ConfirmedHomeCallAsync(chip.Reference, chip.Name, call, cancellationToken).ConfigureAwait(false);
            return;
        }

        StartHomeCall(chip.Reference, chip.Name, call, null);
    }

    // A room's pane: all its lights on or off, then each device (Space or Enter switches one; a lock or a guarded cover asks).
    // Its devices are kept current while it is open, and the list (and the title's count) is drawn again whenever a state it shows
    // changes (2026-10-12, the user's report: a light turned on from its own page still read off back on the room's list — a call
    // runs in the background, so the pass after it was built before Home Assistant's answer came). A watcher compares the live
    // states with the drawn ones each tick (HomeRoomKey) and ends the wait for a key, the cursor kept, when they differ.
    private async Task HomeRoomAsync(HomeChip chip, string areaId, CancellationToken cancellationToken)
    {
        var catalog = _homeLive.Snapshot.Catalog;
        var members = catalog?.MembersOf(areaId, HomeRoomDomains)
            .OrderBy(id => Array.IndexOf(HomeRoomDomains, HaCatalog.DomainOf(id)))
            .ToList() ?? [];
        lock (_homeGate)
        {
            _homeExtra.UnionWith(members);
            _homeModel++;
        }

        try
        {
            int cursor = 0;
            while (true)
            {
                var entities = _homeLive.Snapshot.Entities;
                var rows = new List<string> { Markup.Escape(HomeBarText.AllOnAction), Markup.Escape(HomeBarText.AllOffAction) };
                foreach (string id in members)
                {
                    var entity = entities.GetValueOrDefault(id);
                    string glyph = HomeChips.GlyphFor(id, entity);
                    rows.Add(Markup.Escape(glyph + " " + (entity?.Name ?? catalog!.NameOf(id))) + Theme.DimMarkup("  " + HomeBarText.StateWord(entity?.State ?? "…")));
                }

                string value = HomeChips.Build(chip.Reference, entities, catalog, HomeChipMark.None, stale: false).Value;
                string drawn = HomeRoomKey(members, entities, value);
                var page = new MenuPage(HomeBarText.DetailTitle(chip.Name, value), rows, HomeBarText.RoomKeys) { SpaceToggles = true };
                MenuPick? picked;
                bool redraw;
                using (var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var watcher = WatchHomeRoomAsync(chip.Reference, members, catalog, drawn, watch);
                    try
                    {
                        // The key read answers a cancellation with null (KeySource's contract), so the pick ends as on ESC; the
                        // watcher's own cancellation, not the caller's, tells a change from the user leaving.
                        picked = await _menuPane.PickAsync(page, cursor, watch.Token, row => cursor = row).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        picked = null;
                    }
                    finally
                    {
                        redraw = watch.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                        await watch.CancelAsync().ConfigureAwait(false);
                        await watcher.ConfigureAwait(false);
                    }
                }

                if (picked is not { } pick)
                {
                    if (redraw && !_pane.Dismissed)
                    {
                        // A state it shows changed: drawn again at the same row.
                        continue;
                    }

                    return;
                }

                cursor = pick.Row;
                if (pick.Row < 2)
                {
                    StartHomeCall(chip.Reference, chip.Name, new HaServiceCall("light", pick.Row == 0 ? "turn_on" : "turn_off", [], areaId), catalog);
                    continue;
                }

                string member = members[pick.Row - 2];
                if (entities.GetValueOrDefault(member) is not { } device)
                {
                    continue;
                }

                if (!pick.Toggle)
                {
                    // Enter: the device's own pane, then this one again.
                    var one = HomeChips.Build(HomeBarItems.Entity(member), entities, catalog, HomeChipMark.None, stale: false);
                    await HomeEntityAsync(one, member, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Space: the device's first action, a thermostat's none (its warmer is no switch).
                if (device.Domain == "climate" || HomeActions(device).FirstOrDefault() is not { Label.Length: > 0 } first)
                {
                    continue;
                }

                string name = device.Name;
                if (first.Ask)
                {
                    _menuPane.Close();
                    await ConfirmedHomeCallAsync(HomeBarItems.Entity(member), name, first.Call, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                StartHomeCall(HomeBarItems.Entity(member), name, first.Call, catalog);
            }
        }
        finally
        {
            lock (_homeGate)
            {
                _homeExtra.ExceptWith(members);
                _homeModel++;
            }
        }
    }

    // The room pane's watcher: each tick, the live states against the drawn ones; a difference cancels the pane's wait. Never throws.
    private async Task WatchHomeRoomAsync(string reference, IReadOnlyList<string> members, HaCatalog? catalog, string drawn, CancellationTokenSource watch)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ScreenPane.Tick, _time, watch.Token).ConfigureAwait(false);
                var entities = _homeLive.Snapshot.Entities;
                string value = HomeChips.Build(reference, entities, catalog, HomeChipMark.None, stale: false).Value;
                if (!string.Equals(HomeRoomKey(members, entities, value), drawn, StringComparison.Ordinal))
                {
                    await watch.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The pane's pick ended first.
        }
    }

    /// <summary>What a room's pane shows of the live states: each member's state in order, then the title's count. Pure.</summary>
    internal static string HomeRoomKey(IReadOnlyList<string> members, IReadOnlyDictionary<string, HaLiveEntity> entities, string value)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(entities);
        var key = new System.Text.StringBuilder(value);
        foreach (string id in members)
        {
            key.Append('\n').Append(entities.GetValueOrDefault(id)?.State ?? "…");
        }

        return key.ToString();
    }

    /// <summary>
    /// An entity's actions on its pane, the one a room's Space runs first: a toggle (and on, off, brightness steps for a light),
    /// a player's play/pause, volume and mute, a cover's open (asking for a garage, gate or door), close and stop, a lock's lock and
    /// unlock (asking), a thermostat's warmer and cooler, a scene or script run, an alarm's disarm (asking; none when it wants a
    /// code). Empty for a sensor. Pure over the entity.
    /// </summary>
    internal static IReadOnlyList<(string Label, HaServiceCall Call, bool Ask)> HomeActions(HaLiveEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        string id = entity.Id;
        string domain = entity.Domain;
        var actions = new List<(string Label, HaServiceCall Call, bool Ask)>();
        switch (domain)
        {
            case "light":
                actions.Add((HomeBarText.ToggleAction, HaServiceCall.On("light", "toggle", id), false));
                actions.Add((HomeBarText.TurnOnAction, HaServiceCall.On("light", "turn_on", id), false));
                actions.Add((HomeBarText.TurnOffAction, HaServiceCall.On("light", "turn_off", id), false));
                foreach (int percent in (int[])[25, 50, 75, 100])
                {
                    actions.Add((HomeBarText.Brightness(percent), HaServiceCall.On("light", "turn_on", id, HaJson.Object(w => w.WriteNumber("brightness_pct", percent))), false));
                }

                break;
            case "switch" or "input_boolean" or "fan":
                actions.Add((HomeBarText.ToggleAction, HaServiceCall.On(domain, "toggle", id), false));
                actions.Add((HomeBarText.TurnOnAction, HaServiceCall.On(domain, "turn_on", id), false));
                actions.Add((HomeBarText.TurnOffAction, HaServiceCall.On(domain, "turn_off", id), false));
                break;
            case "media_player":
                bool off = entity.State is "off" or "standby";
                if (off)
                {
                    actions.Add((HomeBarText.TurnOnAction, HaServiceCall.On(domain, "turn_on", id), false));
                    break;
                }

                actions.Add((HomeBarText.PlayPauseAction, HaServiceCall.On(domain, "media_play_pause", id), false));
                actions.Add((HomeBarText.VolumeUpAction, HaServiceCall.On(domain, "volume_up", id), false));
                actions.Add((HomeBarText.VolumeDownAction, HaServiceCall.On(domain, "volume_down", id), false));
                bool muted = entity.Flag("is_volume_muted") == true;
                actions.Add((HomeBarText.MuteAction, HaServiceCall.On(domain, "volume_mute", id, HaJson.Object(w => w.WriteBoolean("is_volume_muted", !muted))), false));
                actions.Add((HomeBarText.TurnOffAction, HaServiceCall.On(domain, "turn_off", id), false));
                break;
            case "cover":
                bool open = entity.State is "open" or "opening";
                bool guarded = HomeChips.GuardedCovers.Contains(entity.Text("device_class") ?? "", StringComparer.Ordinal);
                var opening = (HomeBarText.OpenAction, HaServiceCall.On(domain, "open_cover", id), guarded);
                var closing = (HomeBarText.CloseAction, HaServiceCall.On(domain, "close_cover", id), false);
                if (open)
                {
                    actions.Add(closing);
                    actions.Add(opening);
                }
                else
                {
                    actions.Add(opening);
                    actions.Add(closing);
                }

                actions.Add((HomeBarText.StopAction, HaServiceCall.On(domain, "stop_cover", id), false));
                break;
            case "lock":
                var lockIt = (HomeBarText.LockAction, HaServiceCall.On(domain, "lock", id), false);
                var unlockIt = (HomeBarText.UnlockAction, HaServiceCall.On(domain, "unlock", id), true);
                if (entity.State == "locked")
                {
                    actions.Add(unlockIt);
                    actions.Add(lockIt);
                }
                else
                {
                    actions.Add(lockIt);
                    actions.Add(unlockIt);
                }

                break;
            case "climate":
                if (entity.Number("temperature") is { } target)
                {
                    double step = entity.Number("target_temp_step") is { } s and > 0 ? s : 0.5;
                    actions.Add((HomeBarText.Warmer, HaServiceCall.On(domain, "set_temperature", id, HaJson.Object(w => w.WriteNumber("temperature", target + step))), false));
                    actions.Add((HomeBarText.Cooler, HaServiceCall.On(domain, "set_temperature", id, HaJson.Object(w => w.WriteNumber("temperature", target - step))), false));
                }

                actions.Add((entity.State == "off" ? HomeBarText.TurnOnAction : HomeBarText.TurnOffAction, HaServiceCall.On(domain, entity.State == "off" ? "turn_on" : "turn_off", id), false));
                break;
            case "scene":
                actions.Add((HomeBarText.RunAction, HaServiceCall.On(domain, "turn_on", id), false));
                break;
            case "script":
                actions.Add(entity.State == "on" ? (HomeBarText.StopAction, HaServiceCall.On(domain, "turn_off", id), false) : (HomeBarText.RunAction, HaServiceCall.On(domain, "turn_on", id), false));
                break;
            case "alarm_control_panel":
                bool armed = entity.State.StartsWith("armed", StringComparison.Ordinal) || entity.State is "triggered" or "arming" or "pending";
                bool needsCode = entity.Attributes.TryGetValue("code_format", out var format) && format.ValueKind == System.Text.Json.JsonValueKind.String;
                if (armed && !needsCode)
                {
                    actions.Add((HomeBarText.DisarmAction, HaServiceCall.On(domain, "alarm_disarm", id), true));
                }

                break;
        }

        return actions;
    }
}
