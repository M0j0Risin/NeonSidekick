using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>
/// The Web UI's Home tab (2026-10-15, the plan at <c>~/.claude/plans/read-in-this-plan-glimmering-unicorn.md</c>, Part D): the home bar's items as
/// tiles over Home Assistant's live states, a room's or a device's sheet, the rooms (2026-10-16: every room's switch, its devices' rows and its
/// scenes, <see cref="WebHomeView.Rooms"/>; the scenes alone until then), <i>Ask the house</i>, and the cards (the PC's YouTube window, its
/// meters, the model). What a page may read or act on is the tiles' and the rooms' reach (<see cref="HomeReach"/>). Every call comes on a Kestrel thread: nothing here waits on the pane's lock (the flashes go through the session's queue),
/// and nothing calls the pane while holding <c>_homeGate</c> — the pane's draw holds its own lock and then takes that one (<see cref="HomeBarRow"/>).
/// A read holds the live states and the meters for <see cref="HomeWebLease"/>: the tab's tiles and an open sheet's devices are kept current even with
/// the home bar hidden, and the session's 1-second tick (<see cref="HomeWebTick"/>) lets them go once the phone has left. Acting needs <i>Web UI home
/// controls</i>; the home bar's own confirms (an unlock, a garage, gate or door opening, a disarm) ask on the page first. No <c>HaPolicy</c>: the bar
/// has none either.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>How long a read of the Home tab keeps its states and meters live (2026-10-15).</summary>
    public static readonly TimeSpan HomeWebLease = TimeSpan.FromSeconds(10);

    /// <summary>The most characters a question to Assist may hold from the page.</summary>
    public const int HomeSayMaxChars = 1000;

    // Under _homeGate: the lease's end (UTC ticks, 0 for none), the tab's entity ids and an open sheet's, and the set last sent to the feed.
    private long _homeWebUntil;
    private IReadOnlySet<string> _homeWebIds = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _homeWebSheet = new(StringComparer.Ordinal);
    private IReadOnlySet<string>? _homeBarIds;
    private string? _homeWanted = "\u0000";   // the key last sent to Want; "\0" is never a set's, so the first sync always sends

    private readonly SemaphoreSlim _homeSaying = new(1, 1);

    // What a page may reach, as last worked out, and what from (2026-10-19, the review: the rooms were rebuilt at every sheet read, tap and act, about
    // twice a second with a sheet open). Swapped whole; the snapshot is the feed's, replaced on every change, so a reference compare is its version.
    private sealed record HomeReachCache(string ItemsKey, HaLiveSnapshot Live, IReadOnlySet<string> Reach);

    private HomeReachCache? _homeReach;

    /// <summary>The Home tab's side, as the session hands it to the host.</summary>
    private sealed class HomeSide(ChatScreen screen) : IWebHome
    {
        public WebHome Read() => screen.WebHomeNow();

        public WebHomeSheet? Sheet(string reference) => screen.WebHomeSheetOf(reference);

        public WebReply Tap(WebHomeTapRequest request, IPAddress? remote) => screen.WebHomeTap(request, remote);

        public WebReply Act(WebHomeActRequest request, IPAddress? remote) => screen.WebHomeAct(request, remote);

        public WebReply Scene(WebHomeSceneRequest request, IPAddress? remote) => screen.WebHomeScene(request, remote);

        public Task<WebReply> SayAsync(WebHomeSayRequest request, IPAddress? remote, CancellationToken cancellationToken) => screen.WebHomeSayAsync(request, remote, cancellationToken);

        public WebReply Video(WebHomeVideoRequest request, IPAddress? remote) => screen.WebHomeVideo(request, remote);

        public Task<WebCameraAnswer> CameraAsync(string reference, IPAddress? remote, CancellationToken cancellationToken) => screen.WebHomeCameraAsync(reference, remote, cancellationToken);
    }

    // ── The lease and the feed's wanted set ─────────────────────────────────

    // Under _homeGate: whether a page read the tab within the lease.
    private bool HomeWebLive() => _homeWebUntil > _time.GetUtcNow().UtcTicks;

    /// <summary>
    /// The entities the live feed keeps current, sent when they changed (2026-10-15): the home bar's (with an open room pane's), and while a page
    /// holds the lease the tab's tiles and its open sheet — so a hidden bar never cuts off a page, and a page that left lets its states go. Null
    /// (the feed's "hidden") only when nothing at all is wanted. Never waits: <c>Want</c> takes the feed's own lock briefly.
    /// </summary>
    private void HomeWantSync()
    {
        List<string>? send;
        lock (_homeGate)
        {
            bool web = HomeWebLive();
            if (!web)
            {
                _homeWebUntil = 0;
                _homeWebSheet.Clear();
            }

            if (_homeBarIds is null && !web && _homeExtra.Count == 0)
            {
                send = null;
            }
            else
            {
                var all = new HashSet<string>(_homeBarIds ?? new HashSet<string>(), StringComparer.Ordinal);
                all.UnionWith(_homeExtra);
                if (web)
                {
                    all.UnionWith(_homeWebIds);
                    all.UnionWith(_homeWebSheet);
                }

                send = [.. all.Order(StringComparer.Ordinal)];
            }

            string? key = send is null ? null : string.Join('\n', send);
            if (string.Equals(key, _homeWanted, StringComparison.Ordinal))
            {
                return;
            }

            _homeWanted = key;
        }

        _homeLive.Want(send);
    }

    /// <summary>
    /// The Web UI session's 1-second tick (2026-10-15): a lease run out lets the tab's states and the card's meters go, whether or not the bars are
    /// drawn (the pane's tick drops them too when it runs).
    /// </summary>
    private void HomeWebTick()
    {
        HomeWantSync();
        _perf.Ensure(PerfReadsNow());
    }

    /// <summary>
    /// The meters the sampler reads now (2026-10-15, the sixth review: one place decides them): the perf bar's checked ones, and while a page holds
    /// the Home tab's lease every meter its card shows. <see cref="PerfBarRow"/> and <see cref="HomeWebTick"/> both pass it, so they never disagree.
    /// </summary>
    private Perf.PerfReads PerfReadsNow()
    {
        var reads = PerfBarItems.Reads(PerfBarItems.Resolve(_effective().PerformanceBarItems));
        bool web;
        lock (_homeGate)
        {
            web = HomeWebLive();
        }

        return web ? reads | Perf.PerfReads.All : reads;
    }

    /// <summary>A profile switch (2026-10-15): the tab's sheet and lease go; the next read builds from the new profile's items.</summary>
    private void HomeWebForget()
    {
        lock (_homeGate)
        {
            _homeWebUntil = 0;
            _homeWebSheet.Clear();
            _homeWebIds = new HashSet<string>(StringComparer.Ordinal);
        }

        HomeWantSync();
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    /// <summary>The tab now (a Kestrel thread): the tiles over the live states, the rooms, the cards; the lease renewed and the feed told.</summary>
    private WebHome WebHomeNow()
    {
        var effective = _effective();
        var items = WebHomeView.Items(effective);
        var live = _homeLive.Snapshot;
        Dictionary<string, HomeChipMark> marks;
        lock (_homeGate)
        {
            _homeWebUntil = (_time.GetUtcNow() + HomeWebLease).UtcTicks;
            UpdateHomePending(live, _time.GetUtcNow());
            marks = _homePending.Keys.ToDictionary(r => r, HomeMarkOf, StringComparer.Ordinal);
        }

        // The tiles and the rooms outside the lock, over a copy of the marks (2026-10-19, the review: the PC bar's draw takes _homeGate under the
        // pane's lock, and a big catalog's rooms held it there).
        HomeChipMark MarkOf(string reference) => marks.GetValueOrDefault(reference);
        var tiles = WebHomeView.Tiles(items, live, MarkOf, effective.HomeBarAliases, effective.HomeBarGlyphs, effective.WebUiHomeControls);
        var rooms = WebHomeView.Rooms(live, items, MarkOf, HomeRoomDomains);
        Volatile.Write(ref _homeReach, new HomeReachCache(HomeItemsKey(items), live, WebHomeView.Reach(items, live.Catalog, HomeRoomDomains, rooms)));
        var ids = new HashSet<string>(HomeBarItems.EntityIds(items, live.Catalog), StringComparer.Ordinal);
        // The scenes too, so their names come with their states (the registry often has none).
        ids.UnionWith(live.Catalog?.EntitiesOf("scene") ?? []);
        // And every room's devices, an area's lights and a zone's group light (2026-10-16): a zone's members come with its group's
        // attributes, so they are wanted from the next read on.
        ids.UnionWith(HomeRoomIds(rooms, live.Catalog));
        lock (_homeGate)
        {
            _homeWebIds = ids;
        }

        HomeWantSync();
        _perf.Ensure(PerfReadsNow());
        var (status, text) = WebHomeView.StatusOf(live, HaSession.Configured(effective));
        var (working, loading) = ScopesNow();
        var parts = UsageText.HintParts(_session.Usage, _session.ContextLength);
        var model = new WebHomeModel(ModelLabel(_session.Endpoint?.ModelId), WebHomeView.ServerOf(_session.Endpoint?.BaseUrl), _session.ContextLength?.Tokens, loading, working,
            parts is null ? null : new WebUsage(parts.Tokens, parts.Percent, parts.Speed));
        return new WebHome(status, text, effective.WebUiHomeControls, tiles, rooms, WebHomeView.Video(_videoPlayer?.Snapshot),
            WebHomeView.Pc(_perf.Read().Latest), model, _webUi.Pulse(), WebUiText.HomeWords);
    }

    /// <summary>
    /// The entities the rooms keep live for the tab (2026-10-16): each area's room-pane members, every light that may be a zone's group light (its
    /// attributes say whether it is), a zone's group light, every row's device.
    /// </summary>
    private static IEnumerable<string> HomeRoomIds(IReadOnlyList<WebHomeRoom> rooms, HaCatalog? catalog)
    {
        foreach (string light in catalog is null ? [] : WebHomeView.ZoneLights(catalog))
        {
            yield return light;
        }

        foreach (var room in rooms)
        {
            if (catalog?.Area(room.Key) is not null)
            {
                foreach (string member in catalog.MembersOf(room.Key, HomeRoomDomains))
                {
                    yield return member;
                }
            }

            if (room.Ref is { } reference && HomeBarItems.TryParse(reference, out var kind, out string id) && kind == HomeItemKind.Entity)
            {
                yield return id;
            }

            foreach (var device in room.Devices)
            {
                yield return device.Entity;
            }
        }
    }

    /// <summary>
    /// What a page may read or act on now (2026-10-16): the tiles' reach and the rooms' (<see cref="WebHomeView.Reach"/>), the rooms built
    /// without marks. Checked again at every request against the states as they are: worked out again only when the items or the feed's
    /// snapshot moved (2026-10-19, the review), the marks never changing which devices a room holds.
    /// </summary>
    private IReadOnlySet<string> HomeReach(IReadOnlyList<string> items, HaLiveSnapshot live)
    {
        string key = HomeItemsKey(items);
        if (Volatile.Read(ref _homeReach) is { } cache && ReferenceEquals(cache.Live, live) && string.Equals(cache.ItemsKey, key, StringComparison.Ordinal))
        {
            return cache.Reach;
        }

        var reach = WebHomeView.Reach(items, live.Catalog, HomeRoomDomains, WebHomeView.Rooms(live, items, static _ => HomeChipMark.None, HomeRoomDomains));
        Volatile.Write(ref _homeReach, new HomeReachCache(key, live, reach));
        return reach;
    }

    private static string HomeItemsKey(IReadOnlyList<string> items) => string.Join('\n', items);

    /// <summary>
    /// A tile's sheet (a Kestrel thread): a room's devices with their states and actions (its own row first: all on, all off), or one device's;
    /// null for a reference outside the tiles' and the rooms' reach (a room's device row's <c>›</c> since 2026-10-16). Its devices join the
    /// wanted set under the lease.
    /// </summary>
    private WebHomeSheet? WebHomeSheetOf(string reference)
    {
        var effective = _effective();
        var items = WebHomeView.Items(effective);
        var live = _homeLive.Snapshot;
        var reach = HomeReach(items, live);
        if (!WebHomeView.Reaches(reference, items, reach) || !HomeBarItems.TryParse(reference, out var kind, out string id))
        {
            return null;
        }

        bool controls = effective.WebUiHomeControls;
        var chip = HomeChips.Build(reference, live.Entities, live.Catalog, HomeChipMark.None, stale: false, effective.HomeBarAliases, effective.HomeBarGlyphs);
        string name = chip.Label is { Length: > 0 } label ? label : chip.Name;
        WebHomeSheet sheet;
        IReadOnlyList<string> watched;
        if (kind == HomeItemKind.Area)
        {
            // Only the members a page may act on (2026-10-19, the review: a room off the home bar listed its Hue group light, which its reach
            // leaves to the room's own switch, and every tap on it was refused).
            var members = live.Catalog?.MembersOf(id, HomeRoomDomains)
                .Where(reach.Contains)
                .OrderBy(m => Array.IndexOf(HomeRoomDomains, HaCatalog.DomainOf(m)))
                .ToList() ?? [];
            var rows = new List<WebHomeSheetRow>
            {
                new(null, HomeChips.AreaGlyph, name, chip.Value, controls ? [new(0, HomeBarText.AllOnAction, false), new(1, HomeBarText.AllOffAction, false)] : []),
            };
            foreach (string member in members)
            {
                rows.Add(SheetRow(member, live, controls));
            }

            sheet = new WebHomeSheet(reference, name, chip.Value, rows);
            watched = members;
        }
        else if (kind == HomeItemKind.Entity)
        {
            var row = SheetRow(id, live, controls);
            // A camera's sheet shows its still (2026-10-27), but for one Home Assistant reports unavailable: there is none to ask for.
            bool camera = HaCatalog.DomainOf(id) == "camera" && live.Entities.GetValueOrDefault(id) is { Unavailable: false };
            sheet = new WebHomeSheet(reference, name, row.State, [row], camera);
            watched = [id];
        }
        else
        {
            return null;
        }

        lock (_homeGate)
        {
            _homeWebUntil = (_time.GetUtcNow() + HomeWebLease).UtcTicks;
            _homeWebSheet.Clear();
            _homeWebSheet.UnionWith(watched);
        }

        HomeWantSync();
        return sheet;
    }

    private static WebHomeSheetRow SheetRow(string entityId, HaLiveSnapshot live, bool controls)
    {
        var entity = live.Entities.GetValueOrDefault(entityId);
        string state = WebHomeView.DeviceState(entity);
        var actions = entity is null || entity.Unavailable || !controls ? [] : HomeActions(entity).Select((a, i) => new WebHomeAction(i, a.Label, a.Ask)).ToList();
        return new WebHomeSheetRow(entityId, HomeChips.GlyphFor(entityId, entity), entity?.Name ?? live.Catalog?.NameOf(entityId) ?? entityId, state, actions);
    }

    // ── Acting ──────────────────────────────────────────────────────────────

    /// <summary>The refusal every action gets while <i>Web UI home controls</i> is off.</summary>
    private static readonly WebReply HomeControlsOff = new(WebOutcome.Forbidden, WebUiText.HomeControlsOff);

    /// <summary>
    /// A tile tapped, or (2026-10-16) a room's switch — an area's under the bar's own <c>area:</c> chip, a zone's group light: the bar's own call (the
    /// PC's chip marked pending too), or the question first for a chip that asks.
    /// </summary>
    private WebReply WebHomeTap(WebHomeTapRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = _effective();
        if (!effective.WebUiHomeControls)
        {
            return HomeControlsOff;
        }

        string reference = request.Ref ?? "";
        var items = WebHomeView.Items(effective);
        var live = _homeLive.Snapshot;
        if (!WebHomeView.Reaches(reference, items, HomeReach(items, live)))
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged);
        }

        var chip = HomeChips.Build(reference, live.Entities, live.Catalog, HomeChipMark.None, live.Status != HaLiveStatus.Live, effective.HomeBarAliases, effective.HomeBarGlyphs);
        if (chip.Call is not { } call || chip.Action is not (HomeChipAction.Call or HomeChipAction.Confirm))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.HomeChanged);
        }

        if (chip.Action == HomeChipAction.Confirm && !request.Confirmed)
        {
            return new WebReply(WebOutcome.Conflict, null, Prompt: HomeBarText.Confirm(call, chip.Name));
        }

        StartHomeCall(reference, chip.Name, call, live.Catalog);
        HomeDone(chip.Name, call, remote);
        return WebReply.Ok;
    }

    /// <summary>
    /// A sheet's action, or (2026-10-16) a room row's quick control (its <c>entity:</c> reference and the action at index 0): checked against the
    /// actions now (a state that moved changes them), asked first where the bar asks; the row itself marked pending.
    /// </summary>
    private WebReply WebHomeAct(WebHomeActRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = _effective();
        if (!effective.WebUiHomeControls)
        {
            return HomeControlsOff;
        }

        string reference = request.Ref ?? "";
        var items = WebHomeView.Items(effective);
        var live = _homeLive.Snapshot;
        var reach = HomeReach(items, live);
        if (!WebHomeView.Reaches(reference, items, reach))
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged);
        }

        HaServiceCall call;
        string name;
        bool ask;
        string target;
        if (request.Entity is not { Length: > 0 } entityId)
        {
            // A room's own row: all its lights on or off.
            if (!HomeBarItems.TryParse(reference, out var kind, out string areaId) || kind != HomeItemKind.Area || request.Index is not (0 or 1))
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged);
            }

            string label = request.Index == 0 ? HomeBarText.AllOnAction : HomeBarText.AllOffAction;
            if (!string.Equals(request.Label, label, StringComparison.Ordinal))
            {
                return new WebReply(WebOutcome.Conflict, WebUiText.HomeChanged);
            }

            call = new HaServiceCall("light", request.Index == 0 ? "turn_on" : "turn_off", [], areaId);
            name = live.Catalog?.Area(areaId)?.Name ?? areaId;
            ask = false;
            target = reference;
        }
        else
        {
            if (!reach.Contains(entityId) || live.Entities.GetValueOrDefault(entityId) is not { Unavailable: false } entity)
            {
                return new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged);
            }

            var actions = HomeActions(entity);
            if (request.Index < 0 || request.Index >= actions.Count || !string.Equals(actions[request.Index].Label, request.Label, StringComparison.Ordinal))
            {
                return new WebReply(WebOutcome.Conflict, WebUiText.HomeChanged);
            }

            (_, call, ask) = actions[request.Index];
            name = entity.Name;
            target = HomeBarItems.Entity(entityId);
        }

        if (ask && !request.Confirmed)
        {
            return new WebReply(WebOutcome.Conflict, null, Prompt: HomeBarText.Confirm(call, name));
        }

        StartHomeCall(target, name, call, live.Catalog);
        HomeDone(name, call, remote);
        return WebReply.Ok;
    }

    /// <summary>A scene run from the tab's row.</summary>
    private WebReply WebHomeScene(WebHomeSceneRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_effective().WebUiHomeControls)
        {
            return HomeControlsOff;
        }

        var live = _homeLive.Snapshot;
        string id = request.Id ?? "";
        if (live.Catalog?.EntitiesOf("scene").Contains(id, StringComparer.Ordinal) != true)
        {
            return new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged);
        }

        string name = live.Entities.GetValueOrDefault(id)?.Name ?? live.Catalog.NameOf(id);
        var call = HaServiceCall.On("scene", "turn_on", id);
        StartHomeCall(HomeBarItems.Entity(id), name, call, live.Catalog);
        HomeDone(name, call, remote);
        return WebReply.Ok;
    }

    /// <summary><i>Ask the house</i>: the words to Assist (<c>/ha say</c>'s call), one at a time; its answer as the reply's message.</summary>
    private async Task<WebReply> WebHomeSayAsync(WebHomeSayRequest request, IPAddress? remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_effective().WebUiHomeControls)
        {
            return HomeControlsOff;
        }

        string text = (request.Text ?? "").Trim();
        if (text.Length == 0)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.EmptyMessage);
        }

        if (text.Length > HomeSayMaxChars)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.HomeSayTooLong(HomeSayMaxChars));
        }

        if (!await _homeSaying.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.HomeSayBusy);
        }

        try
        {
            DiagnosticLog.Info(WebUiCategory, WebUiText.HomeSayLogLine(text.Length, remote));
            _webUi.FlashPc(WebUiText.HomeSayFlash);
            string answer = await HaAssistTool.AskAsync(_ha, text, cancellationToken).ConfigureAwait(false);
            return new WebReply(WebOutcome.Ok, answer);
        }
        finally
        {
            _homeSaying.Release();
        }
    }

    /// <summary>
    /// The video remote: the video told at once, in the PC's window or (2026-10-16) the YouTube tab (no wait: the tab reads it back). A video
    /// still waiting for its tab answers its held words; nothing is open, the place the router plays in now.
    /// </summary>
    private WebReply WebHomeVideo(WebHomeVideoRequest request, IPAddress? remote)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_effective().WebUiHomeControls)
        {
            return HomeControlsOff;
        }

        if (_videoPlayer?.Snapshot is not { State: not VideoState.Failed } video)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.NoVideoIn(_videoPlayer?.Place ?? VideoPlace.Window));
        }

        if (video.Held is { } held)
        {
            return new WebReply(WebOutcome.Conflict, YouTube.YouTubeText.Held(video, held));
        }

        VideoCommand? command = (request.Action ?? "") switch
        {
            "play" => VideoCommand.Play,
            "pause" => VideoCommand.Pause,
            "back" => VideoCommand.Seek(Math.Max(0, video.Position - YouTubeControlTool.DefaultSkip)),
            "forward" => VideoCommand.Seek(video.Duration > 0 ? Math.Min(video.Duration, video.Position + YouTubeControlTool.DefaultSkip) : video.Position + YouTubeControlTool.DefaultSkip),
            "quieter" => VideoCommand.Volume(video.Volume - 10),
            "louder" => VideoCommand.Volume(video.Volume + 10),
            "mute" => VideoCommand.Mute,
            "unmute" => VideoCommand.Unmute,
            "previous" when video.PlaylistCount is > 1 => VideoCommand.Previous,
            "next" when video.PlaylistCount is > 1 => VideoCommand.Next,
            _ => null,
        };
        if (command is null)
        {
            return new WebReply(WebOutcome.Refused, WebUiText.BadRequest);
        }

        if (!_videoPlayer.Send(command))
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.NoVideoIn(video.Place));
        }

        DiagnosticLog.Info(WebUiCategory, WebUiText.HomeVideoLogLine(request.Action!, remote));
        _webUi.FlashPc(WebUiText.HomeVideoFlash(request.Action!));
        return WebReply.Ok;
    }

    /// <summary>
    /// A camera's still for its sheet (2026-10-27, a Kestrel thread): a camera in the tiles' reach, fetched through the stills the chips share
    /// (<see cref="HomeCameraFetches"/>: one request to Home Assistant however many ask, a still under two seconds old answered again), never
    /// saved and never shown at the PC. Each still served is logged with the address; a refusal is not. One Home Assistant reports unavailable is
    /// refused without a request; a fetch forgotten under the wait (a profile switch) is the sheet gone.
    /// </summary>
    private async Task<WebCameraAnswer> WebHomeCameraAsync(string reference, IPAddress? remote, CancellationToken cancellationToken)
    {
        var effective = _effective();
        var items = WebHomeView.Items(effective);
        var live = _homeLive.Snapshot;
        if (!WebHomeView.Reaches(reference, items, HomeReach(items, live)) || !HomeBarItems.TryParse(reference, out var kind, out string id)
            || kind != HomeItemKind.Entity || HaCatalog.DomainOf(id) != "camera")
        {
            return new WebCameraAnswer(null, new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged));
        }

        var chip = HomeChips.Build(reference, live.Entities, live.Catalog, HomeChipMark.None, stale: false, effective.HomeBarAliases, effective.HomeBarGlyphs);
        string name = chip.Label is { Length: > 0 } label ? label : chip.Name;
        if (live.Entities.GetValueOrDefault(id) is { Unavailable: true } entity)
        {
            return new WebCameraAnswer(null, new WebReply(WebOutcome.Refused, HomeBarText.Strip(HaText.CameraUnavailable(name, entity.State))));
        }

        HomeCameraStill still;
        try
        {
            still = await _homeCameras.Get(id).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebCameraAnswer(null, new WebReply(WebOutcome.NotFound, WebUiText.HomeChanged));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new WebCameraAnswer(null, new WebReply(WebOutcome.Refused, HomeBarText.Failed(name, ex.Message)));
        }

        if (still.Image is not { } image)
        {
            return new WebCameraAnswer(null, new WebReply(WebOutcome.Refused, HomeBarText.Failed(name, still.Error ?? "")));
        }

        DiagnosticLog.Info(WebUiCategory, WebUiText.HomeCameraLogLine(name, remote));
        return new WebCameraAnswer(new WebCameraPicture(image.Bytes, image.MediaType), null);
    }

    /// <summary>What the page did, in the log and on the PC's hint row (never under <c>_homeGate</c>).</summary>
    private void HomeDone(string name, HaServiceCall call, IPAddress? remote)
    {
        DiagnosticLog.Info(WebUiCategory, WebUiText.HomeLogLine(name, call.Name, remote));
        _webUi.FlashPc(WebUiText.HomeFlash(name, call.Service));
    }
}
