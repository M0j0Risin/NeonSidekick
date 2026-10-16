using System.Globalization;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// The home bar's words (2026-10-12): the chips' values and notes (what a single click shows; the ⌂ status glyph and its
/// note went later that day), the confirm questions, the live feed's failures, <c>/homebar</c>'s words and usage, and the picker's titles and hints.
/// Pure; pinned by tests.
/// </summary>
public static class HomeBarText
{
    // ─── the command ────────────────────────────────────────────────────────────

    public const string Command = "/homebar";

    public const string OnWord = "on";
    public const string OffWord = "off";
    public const string EditWord = "edit";
    public const string TapWord = "tap";
    public const string ShowWord = "show";
    public const string NameWord = "name";
    public const string EmojiWord = "emoji";

    /// <summary><c>/homebar emoji &lt;item&gt; = default</c>: the item's own glyph again, as a blank does.</summary>
    public const string DefaultWord = "default";

    /// <summary><c>/homebar</c>'s words, as its completion lists them.</summary>
    public static readonly string[] Words = [OnWord, OffWord, EditWord, NameWord, EmojiWord];

    /// <summary><c>/homebar</c>'s completion hint beside a word. Pinned.</summary>
    public static string Describe(string word) => word switch
    {
        OnWord => "show the home bar",
        OffWord => "hide the home bar",
        EditWord => "choose the home bar's rooms and devices, and their order",
        NameWord => "show an item on the bar under a name of your own: name <item> = <alias>; nothing after = for its emoji alone, = default for its own",
        EmojiWord => "show an item on the bar with an emoji of your own: emoji <item> = <emoji>, or = default",
        _ => "",
    };

    /// <summary>A word <c>/homebar</c> does not take. Pinned.</summary>
    public const string Usage = "Usage: /homebar [on|off|edit|name <item> = <alias>|emoji <item> = <emoji>] — show or hide the home bar, choose what is on it, or rename an item on it or change its emoji";

    /// <summary>The bar shown, hidden. Pinned.</summary>
    public const string Shown = "Home bar shown";

    public const string Hidden = "Home bar hidden";

    /// <summary>A show with nothing ever picked where the picker cannot open (under a reply, a chord in a pane). Pinned.</summary>
    public const string NothingPicked = "Nothing is on the home bar yet: /homebar edit chooses its rooms and devices";

    /// <summary>A <c>tap</c> or <c>show</c> naming nothing on the bar.</summary>
    public static string NotOnBar(string item) => $"'{item.Trim()}' is not on the home bar";

    /// <summary><c>/homebar name</c> or the picker's rename: the alias set (2026-10-12). Pinned.</summary>
    public static string Renamed(string name, string alias) => $"The home bar shows {name} as \"{alias}\"";

    /// <summary>An empty alias (later on 2026-10-12): the chip is its emoji alone. Pinned.</summary>
    public static string Bared(string name) => $"The home bar shows {name} as its emoji alone";

    /// <summary>The alias taken off: the chip reads the item's own name again. Pinned.</summary>
    public static string Unnamed(string name) => $"The home bar shows {name} by its own name again";

    /// <summary><c>/homebar emoji</c> or the picker's emoji (later on 2026-10-12): the emoji set. Pinned.</summary>
    public static string GlyphSet(string name, string glyph) => $"The home bar shows {name} as {glyph}";

    /// <summary>The emoji taken off: the chip has its own again. Pinned.</summary>
    public static string GlyphReset(string name, string glyph) => $"The home bar shows {name} with its own emoji again, {glyph}";

    /// <summary><c>/homebar emoji</c> given something that is not one emoji. Pinned.</summary>
    public static string NotEmoji(string text) => $"'{text}' is not one emoji: /homebar emoji <item> = <emoji>, or = default for the item's own";

    /// <summary>The shopping list's item (later on 2026-10-12): its name, where the note, the picker and <c>/homebar</c> need one.</summary>
    public const string ShoppingName = "Shopping list";

    /// <summary>The picker's tab for it, last.</summary>
    public const string ShoppingTab = "Shopping";

    /// <summary>Its row's dim note on that tab.</summary>
    public const string ShoppingRowNote = "🛒 and the lines still to get; no Home Assistant needed";

    /// <summary>Its place on the bar's tab, where a device's room goes.</summary>
    public const string ShoppingWhere = "shopping";

    /// <summary>A divider's name on the picker's bar tab (2026-10-13, the user's ask: a divider on the home bar). Pinned.</summary>
    public const string DividerName = "Divider";

    /// <summary>A divider's dim note on the bar tab (2026-10-13). Pinned.</summary>
    public const string DividerWhere = "between the chips around it";

    /// <summary>The bar tab's last row (2026-10-13, the user's pick): Enter or Space puts a new divider at the bar's end (the center's since later that day). Pinned.</summary>
    public const string AddDividerRow = "+ Add divider";

    /// <summary>The 🛒 chip's click note: the lines to get, or the list off. Pinned.</summary>
    public static string ShoppingNote(int? open) => open is { } count
        ? ShoppingName + ": " + count.ToString(CultureInfo.InvariantCulture) + " to get — double-click or right-click for the list"
        : ShoppingName + ": the list is off — /shopping says why";

    /// <summary><c>/homebar name</c> on the 🛒 chip, which shows no name. Pinned.</summary>
    public static string Nameless(string name) => $"The {name}'s chip shows no name to change: /homebar emoji changes its emoji";

    /// <summary>Headless has no bar.</summary>
    public const string Headless = "The home bar is part of the interactive screen; use /ha here";


    /// <summary>The picker could not read the registries.</summary>
    public static string NoCatalog(string error) => "The home bar's rooms and devices could not be read: " + Strip(error);

    // ─── the settings row ───────────────────────────────────────────────────────

    /// <summary>The row's value with nothing on the bar.</summary>
    public const string NoItems = "off";

    /// <summary>The row's value: the count, then the first names. Pinned.</summary>
    public static string ItemsValue(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        const int Shown = 3;
        string first = string.Join(", ", names.Take(Shown));
        return names.Count.ToString(CultureInfo.InvariantCulture) + " · " + first + (names.Count > Shown ? ", …" : "");
    }

    // ─── the bar ────────────────────────────────────────────────────────────────

    /// <summary>The page arrows when the chips do not fit.</summary>
    public const string PrevGlyph = "‹";

    public const string NextGlyph = "›";

    /// <summary>A playing media player's mark before its title; a paused one's.</summary>
    public const string PlayingMark = "▸ ";

    public const string PausedMark = "‖ ";

    /// <summary>The pending mark after a chip clicked whose state has not changed yet; the failed one after the wait ran out.</summary>
    public const string PendingMark = "…";

    public const string FailedMark = "!";

    /// <summary>An area's lights on, of all: <c>2/5</c>. Pinned.</summary>
    public static string Count(int on, int total) => on.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);

    /// <summary>A state's words: underscores as spaces.</summary>
    public static string StateWord(string state) => (state ?? "").Replace('_', ' ');

    /// <summary>An alarm's state, short: <c>armed_home</c> → <c>home</c>, <c>disarmed</c> → <c>off</c>. Pinned.</summary>
    public static string AlarmWord(string state) => state switch
    {
        "disarmed" => "off",
        "armed_home" => "home",
        "armed_away" => "away",
        "armed_night" => "night",
        "armed_vacation" => "vacation",
        "armed_custom_bypass" => "custom",
        _ => StateWord(state),
    };

    /// <summary>A binary sensor's state in its class's words: a door is open or closed, a leak sensor wet or dry. Pinned.</summary>
    public static string BinaryWord(string deviceClass, bool on) => deviceClass switch
    {
        "door" or "window" or "garage_door" or "opening" => on ? "open" : "closed",
        "moisture" => on ? "wet" : "dry",
        "motion" or "occupancy" or "presence" => on ? "detected" : "clear",
        "smoke" or "gas" or "carbon_monoxide" => on ? "detected" : "clear",
        "lock" => on ? "unlocked" : "locked",
        "battery" => on ? "low" : "ok",
        "plug" or "power" => on ? "on" : "off",
        "connectivity" => on ? "connected" : "disconnected",
        "problem" => on ? "problem" : "ok",
        _ => on ? "on" : "off",
    };

    /// <summary>A thermostat's value: current → target. Pinned.</summary>
    public static string ClimateValue(string current, string target) =>
        current.Length > 0 && target.Length > 0 ? current + "→" + target + "°"
        : current.Length > 0 ? current + "°"
        : target.Length > 0 ? "→" + target + "°"
        : "";

    // ─── notes (a single click) ────────────────────────────────────────────────

    private const string More = "; right-click for more";

    public static string NotYet(string name) => name + ": waiting for Home Assistant";

    public static string Unavailable(string name) => name + ": unavailable" + More;

    public static string ToggleNote(string name, bool on, string value) =>
        name + (on ? ": on" + (value.Length > 0 ? " " + value : "") + " — double-click to switch off" : ": off — double-click to switch on") + More;

    public static string AreaNote(string name, int on, int total) =>
        $"{name}: {Count(on, total)} lights on — double-click to switch them {(on > 0 ? "off" : "on")}" + "; right-click for the room";

    public static string AreaNoLights(string name) => name + ": no lights — right-click for the room";

    public static string MediaNote(string name, string state, string title, bool off) =>
        name + ": " + StateWord(state) + (title.Length > 0 ? " · " + title : "") + (off ? " — double-click to switch on" : " — double-click to play or pause") + More;

    public static string SceneNote(string name) => name + " — double-click to run the scene" + More;

    public static string ScriptNote(string name, bool running) => name + (running ? ": running — double-click to stop it" : " — double-click to run the script") + More;

    public static string CoverNote(string name, string state, bool open) =>
        name + ": " + StateWord(state) + (open ? " — double-click to close" : " — double-click to open") + More;

    public static string LockNote(string name, string state) => state switch
    {
        "locked" => name + ": locked — double-click to unlock (asks first)" + More,
        "unlocked" or "open" => name + ": unlocked — double-click to lock" + More,
        _ => name + ": " + StateWord(state) + " — right-click for more",
    };

    public static string AlarmNote(string name, string value, bool armed, bool needsCode) =>
        name + ": " + value + (armed ? needsCode ? " — it needs a code: disarm it in Home Assistant" : " — double-click to disarm (asks first)" : "") + More;

    public static string ClimateNote(string name, string state, string action, string value) =>
        name + ": " + StateWord(state) + (action.Length > 0 && action != state ? ", " + StateWord(action) : "") + (value.Length > 0 ? " · " + value : "") + " — double-click for more";

    /// <summary>A zone's chip: how many people Home Assistant places in it (the zone's state, 2026-10-12).</summary>
    public static string ZoneNote(string name, int people) =>
        name + ": " + (people == 0 ? "nobody" : people == 1 ? "1 person" : people.ToString(CultureInfo.InvariantCulture) + " people") + " here — double-click for more";

    public static string ValueNote(string name, string value) => name + ": " + value + " — double-click for more";

    // ─── confirm and outcomes ───────────────────────────────────────────────────

    /// <summary>The Yes/No question before an unlock, a guarded opening or a disarm. Pinned.</summary>
    public static string Confirm(HaServiceCall call, string name) => call.Service switch
    {
        "unlock" => $"Unlock {name}?",
        "open_cover" => $"Open {name}?",
        "alarm_disarm" => $"Disarm {name}?",
        _ => $"{call.Name} on {name}?",
    };

    /// <summary>The note after a click whose call failed.</summary>
    public static string Failed(string name, string error) => name + ": " + Strip(error);

    /// <summary>The note after a pane's No.</summary>
    public static string Declined(string name) => name + ": left as it is";

    /// <summary>The log line when a pending chip's state never changed.</summary>
    public static string NoChange(string name) => $"The home bar's {name} did not change after the click";

    // ─── the live feed ──────────────────────────────────────────────────────────

    /// <summary>A command the server answered unsuccessfully.</summary>
    public static string LiveRefused(string what, string message) => $"Error: Home Assistant refused {what}" + (message.Trim().Length > 0 ? ": " + message.Trim() : "");

    /// <summary>The token was refused over the socket.</summary>
    public static string Refused(string message) => HaText.Unauthorized + (message.Trim().Length > 0 ? " (" + message.Trim() + ")" : "");

    /// <summary>The one warning when the socket's sign-in is refused (never the token).</summary>
    public const string RefusedLog = "Home Assistant refused the API key on the home bar's connection; it will not try again until the URL or the key changes";

    /// <summary>The connection failed.</summary>
    public static string Lost(string detail) => "Error: the Home Assistant connection failed: " + detail.Trim();

    /// <summary>A call sent over the socket that got no answer: not sent again, since it may have run.</summary>
    public static string Unconfirmed(string what) => $"Error: Home Assistant did not confirm {what}; it may have run, so it was not sent again";

    /// <summary>The other side closed.</summary>
    public const string Dropped = "Home Assistant closed the connection";

    /// <summary>A message where the handshake wanted another.</summary>
    public static string Unexpected(string kind) => "Home Assistant answered the sign-in with " + kind;

    // ─── the picker ─────────────────────────────────────────────────────────────

    /// <summary>The picker's title.</summary>
    public const string PickerTitle = "Home bar";

    /// <summary>The tab of what is on the bar, in order.</summary>
    public const string BarTab = "On the bar";

    public const string RoomsTab = "Rooms";
    public const string LightsTab = "Lights";
    public const string SwitchesTab = "Switches";
    public const string SensorsTab = "Sensors";
    public const string ClimateTab = "Climate";
    public const string MediaTab = "Media";
    public const string ScenesTab = "Scenes";
    public const string SafetyTab = "Security";
    public const string ZonesTab = "Zones";
    public const string OtherTab = "Other";

    /// <summary>The bar tab's hint: the move keys.</summary>
    public const string BarHint = "Shift+↑↓ = move · Shift+←→ = side · Shift+Home/End = top/bottom · Del = remove · Enter = more · Esc = close";

    /// <summary>The other tabs' hint, before the filter's keys.</summary>
    public const string PickHint = "Space / Enter = add or remove";

    /// <summary>A tab with nothing of its kind.</summary>
    public const string NothingHere = "Home Assistant has nothing of this kind";

    /// <summary>A room's lights in the picker. Pinned.</summary>
    public static string Lights(int count) => count == 1 ? "1 light" : count.ToString(CultureInfo.InvariantCulture) + " lights";

    /// <summary>The bar tab with nothing on it.</summary>
    public const string EmptyBar = "Nothing yet: add rooms and devices from the other tabs";

    /// <summary>A heading for entities that have no area.</summary>
    public const string NoArea = "No area";

    /// <summary>A heading for configuration and diagnostic entities.</summary>
    public const string Extra = "Configuration and diagnostics";

    /// <summary>The bar item's actions page.</summary>
    public const string MoveTop = "Move to the top";
    public const string MoveUp = "Move up";
    public const string MoveDown = "Move down";
    public const string MoveBottom = "Move to the bottom";

    /// <summary>The actions page's pins (later on 2026-10-13, the user's ask: the home bar pinned left, center or right), for a terminal that keeps Shift+←/→.</summary>
    public const string PinLeftItem = "Pin left";
    public const string PinCenterItem = "Pin center";
    public const string PinRightItem = "Pin right";
    public const string RenameItem = "Rename on the bar…";
    public const string EmojiItem = "Change emoji…";
    public const string DefaultEmojiItem = "Default emoji";
    public const string DefaultNameItem = "Default name";

    /// <summary>An empty alias where a row shows the alias: the chip's emoji alone (later on 2026-10-12).</summary>
    public const string EmojiAlone = "emoji alone";
    public const string RemoveItem = "Remove from the bar";

    /// <summary>The rename's input row: its crumb's last step and its keys (2026-10-12).</summary>
    public const string RenameTitle = "Rename on the bar";

    public const string RenameKeys = "Enter = save · blank = the emoji alone · Esc = keep";

    /// <summary>The emoji grid's crumb's last step (later on 2026-10-12).</summary>
    public const string EmojiTitle = "Emoji";

    /// <summary>The emoji grid's 🚫 for a home bar item: the band under the grid. Pinned.</summary>
    public static string DefaultGlyphFooter(string glyph) => "🚫  The default emoji again: " + glyph + ", the one its kind of device gets, following its state.";

    /// <summary>The bar tab's note for an item with an emoji of its own: the glyph it would have.</summary>
    public static string DefaultGlyphNote(string glyph) => "default " + glyph;

    /// <summary>A kind's or a room's row note for an item with an alias: the alias quoted ahead of the id or the lights.</summary>
    public static string AliasNote(string alias, string note) => (alias.Length == 0 ? EmojiAlone : "\"" + alias + "\"") + " · " + note;

    /// <summary>The detail pane's actions.</summary>
    public const string ToggleAction = "Toggle";
    public const string TurnOnAction = "Turn on";
    public const string TurnOffAction = "Turn off";
    public const string RunAction = "Run";
    public const string OpenAction = "Open";
    public const string CloseAction = "Close";
    public const string StopAction = "Stop";
    public const string LockAction = "Lock";
    public const string UnlockAction = "Unlock";
    public const string DisarmAction = "Disarm";
    public const string PlayPauseAction = "Play / pause";
    public const string VolumeUpAction = "Volume up";
    public const string VolumeDownAction = "Volume down";
    public const string MuteAction = "Mute / unmute";
    public const string AllOnAction = "All lights on";
    public const string AllOffAction = "All lights off";

    public const string Warmer = "Warmer";
    public const string Cooler = "Cooler";

    /// <summary>A room pane's hint. Pinned.</summary>
    public const string RoomKeys = "Space = switch · Enter = its actions · Esc = close";

    /// <summary>A brightness step's label.</summary>
    public static string Brightness(int percent) => "Brightness " + percent.ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>A detail pane's title: the name and the state.</summary>
    public static string DetailTitle(string name, string state) => name + (state.Length > 0 ? " · " + state : "");

    /// <summary>A room pane's row for one of its entities.</summary>
    public static string RoomRow(string name, string state) => name + " · " + StateWord(state);

    /// <summary>A detail pane with nothing to do.</summary>
    public const string NothingToDo = "Nothing to switch here; its state is in the title";

    private static string Strip(string error) => error.StartsWith("Error: ", StringComparison.Ordinal) ? error["Error: ".Length..] : error;
}
