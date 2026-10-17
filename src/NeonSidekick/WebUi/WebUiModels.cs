using System.Text.Json.Serialization;

namespace NeonSidekick.WebUi;

// The Web UI's wire shapes (2026-10-14): what the pages post and what the server sends back or streams. Every one is registered in
// WebUiJsonContext (source-generated; reflection is off). Property names go out camelCase.

/// <summary>
/// A message the page sends: the page's own id for it (a repeat is not sent twice) and the text; <see cref="AsText"/> sends a line that reads
/// as a command as a message (later on 2026-10-14: the page's <i>Send as a message</i> on an unknown command).
/// </summary>
public sealed class WebSendRequest
{
    public string? Id { get; set; }

    public string? Text { get; set; }

    public bool AsText { get; set; }
}

/// <summary>An answer to an open ask: the choice's id, the typed text of a text choice, or one answer per question (<c>ask_user</c>).</summary>
public sealed class WebAnswerRequest
{
    public string? AskId { get; set; }

    public string? Choice { get; set; }

    public string? Text { get; set; }

    public List<WebQuestionAnswer>? Questions { get; set; }
}

/// <summary>One question's answer: the options picked, by index, and a typed answer of the user's own.</summary>
public sealed class WebQuestionAnswer
{
    public List<int>? Picked { get; set; }

    public string? Other { get; set; }
}

/// <summary>A picture the page's 🗑️ deletes (later on 2026-10-15): its id alone.</summary>
public sealed class WebPictureDelete
{
    /// <summary>The picture's id, as its entry or the strip carries it; never a path.</summary>
    public int Id { get; set; }
}

/// <summary>The page's draft for the sheet's ⇪ (a prompt or a memory), and a memory deleted by its text (2026-10-16: memories have no ids).</summary>
public sealed class WebTextBody
{
    public string? Text { get; set; }
}

/// <summary>A saved prompt the sheet's 🗑️ deletes (2026-10-16): its id, as <see cref="WebPrompt.Id"/> gave it.</summary>
public sealed class WebPromptDelete
{
    public string? Id { get; set; }
}

/// <summary>A picture of the strip picked on the page (later on 2026-10-15): the PC's highlight and its open viewer move to it.</summary>
public sealed class WebStripSelect
{
    public int Id { get; set; }
}

/// <summary>A shopping line ticked (<see cref="On"/>) or unticked.</summary>
public sealed class WebCheckRequest
{
    public long Id { get; set; }

    public bool On { get; set; }
}

/// <summary>
/// A post's answer: whether it was taken, and a sentence for the page when it was not; for a refused command, whether the page may send it as a
/// message (<see cref="AsText"/>) and the line it may use instead (<see cref="Use"/>).
/// </summary>
public sealed record WebResult(bool Ok, string? Message = null, bool AsText = false, string? Use = null)
{
    /// <summary>A Home tap that asks first (2026-10-15): the page asks <see cref="Prompt"/>, then posts again confirmed.</summary>
    public bool? NeedsConfirm { get; init; }

    /// <summary>The question the page asks for <see cref="NeedsConfirm"/>, the home bar's own.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// A 404 because the API's tab is off (later on 2026-10-15): the page goes to <c>/</c>, which sends it to a tab that is on. A plain 404 (a sheet
    /// whose device went) carries none.
    /// </summary>
    public bool? TabOff { get; init; }

    /// <summary>What the page opens for a line that opens on the page (later on 2026-10-15): <c>prompts</c>.</summary>
    public string? Open { get; init; }
}

/// <summary>
/// The Web UI's tabs and whether each is on (later on 2026-10-15): <i>Web UI chat controls</i>, <i>home controls</i>, <i>shopping controls</i>, and
/// (2026-10-16) the YouTube tab while <i>Web UI YouTube controls</i> is on and there is a web player.
/// </summary>
public readonly record struct WebTabs(bool Chat, bool Home, bool Shopping, bool YouTube = false)
{
    /// <summary>Every tab on but YouTube: a backend that says nothing of its tabs has no web player for that one.</summary>
    public static WebTabs All => new(true, true, true);

    /// <summary>Whether <paramref name="tab"/> is on; each tab named, an unknown one off.</summary>
    public bool Has(WebTab tab) => tab switch
    {
        WebTab.Chat => Chat,
        WebTab.Home => Home,
        WebTab.Shopping => Shopping,
        WebTab.YouTube => YouTube,
        _ => false,
    };

    /// <summary>The page <c>/</c> sends a browser to with Chat off: the first tab on in the nav's order (Home, then Shopping, then YouTube), or null for none.</summary>
    public string? FirstOtherPage => Home ? "/home" : Shopping ? "/shopping" : YouTube ? "/youtube" : null;
}

/// <summary>A tab of the Web UI's pages.</summary>
public enum WebTab
{
    Chat,
    Home,
    Shopping,

    /// <summary>The YouTube tab (2026-10-16).</summary>
    YouTube,
}

/// <summary>
/// One transcript entry as the page shows it (<see cref="TranscriptFeed"/>); a picture entry's pictures, when the page may load them. What the
/// terminal shows besides (2026-10-15), each left out unless set: a reply's stretch after a break once it had spoken (<see cref="Continues"/>, no
/// glyph), a reply that said nothing (<see cref="NoReply"/>), a folded thinking block's <see cref="Summary"/>, a thinking block, an output or a
/// run that is over (<see cref="Ended"/>), a tool line's run and whether it completes the call before it (<see cref="Run"/>, <see cref="Joins"/>),
/// a run's keep count (<see cref="Keep"/>), an edit's rows (<see cref="Diff"/>), and text a snapshot left out (<see cref="Deferred"/>, read from
/// <c>/api/entry/{seq}</c>).
/// </summary>
public sealed record WebEntry(long Seq, string Kind, string Text, string? Glyph, string? Html, bool Failed, IReadOnlyList<WebPicture>? Pictures = null)
{
    public bool? Continues { get; init; }

    public bool? NoReply { get; init; }

    public string? Summary { get; init; }

    public bool? Ended { get; init; }

    public long? Run { get; init; }

    public bool? Joins { get; init; }

    public int? Keep { get; init; }

    public WebDiff? Diff { get; init; }

    public bool? Deferred { get; init; }
}

/// <summary>
/// An edit's diff on the page (2026-10-15, <see cref="WebDiffs"/>): the head, the rows (null between two hunks), the line saying how many rows the cut
/// left out (<c>FileText.DiffMore</c>), the row count past which it folds (0: never) and the fold's summary.
/// </summary>
public sealed record WebDiff(string Head, IReadOnlyList<WebDiffRow?> Rows, string? More, int Fold, string FoldSummary);

/// <summary>One row of a diff: its sign (<c>+</c>, <c>-</c>, a blank), its line number, its text.</summary>
public sealed record WebDiffRow(string Sign, int Number, string Text);

/// <summary>A run's summary rewritten (2026-10-15, a <c>replace</c> event): its text, and whether a result failed.</summary>
public sealed record WebReplaced(long Seq, string Text, bool Failed);

/// <summary>
/// A picture the page may load (2026-10-14): the screen's id for it, its name, the three sizes' addresses (built by the server, never by the
/// page), its width and height as the PC holds it (the grid reserves the room before it loads), and <see cref="TooLarge"/> when its file is
/// over what the server decodes for a thumbnail (an <c>img</c> cannot read a status code). <see cref="Key"/> (later on 2026-10-15) names its file
/// without its path (<see cref="WebPictureKey"/>; none for a picture with bytes only), so a <c>point</c> from the PC finds it in either view;
/// <see cref="Deletable"/> says it has a file of its own the page's 🗑️ may delete.
/// </summary>
public sealed record WebPicture(int Id, string Name, string Thumb, string Mid, string Full, int Width, int Height, bool TooLarge, string? Key = null, bool Deletable = false);

/// <summary>
/// The picture the PC's own hand moved to (later on 2026-10-15, the user's ask): its file's key (<see cref="WebPictureKey"/>) and a sequence that
/// only rises, so the page follows each point once and a reconnect follows only a newer one.
/// </summary>
public sealed record WebPoint(string Key, long Seq);

/// <summary>An entry taken out of the transcript (the splash cycled to another picture).</summary>
public sealed record WebRemoved(long Seq);

/// <summary>Text growing on an entry: <see cref="Text"/> belongs at character <see cref="At"/> of entry <see cref="Seq"/>.</summary>
public sealed record WebDelta(long Seq, int At, string Text);

/// <summary>An entry's end: a reply's Markdown as HTML; a thinking block's summary (2026-10-15); a run or an output just over, with neither.</summary>
public sealed record WebEnd(long Seq, string? Html, string? Summary = null);

/// <summary>A text handed back to the page's draft: a message withdrawn or dropped.</summary>
public sealed record WebTextBack(string Text);

/// <summary>
/// The Home tab (2026-10-15, the plan's Part D): Home Assistant's state (<c>live</c>, <c>connecting</c>, <c>retrying</c>, <c>unauthorized</c>,
/// <c>off</c>) and its sentence, whether the tab may act (<i>Web UI home controls</i>), the home bar's items as tiles, the rooms (2026-10-16, the
/// scenes' boxes until then), the cards, what the Chat badge reads (<see cref="Pulse"/>), and the tab's words.
/// </summary>
public sealed record WebHome(
    string Status,
    string? StatusText,
    bool Controls,
    IReadOnlyList<WebHomeTile> Tiles,
    IReadOnlyList<WebHomeRoom> Rooms,
    WebHomeVideo? Video,
    WebHomePc? Pc,
    WebHomeModel Model,
    WebPulse Pulse,
    WebHomeWords Words);

/// <summary>
/// One tile, a home bar chip on the page: its reference, glyph, name (the alias when there is one), value, note, what a tap does (<c>none</c>,
/// <c>call</c>, <c>confirm</c>, <c>detail</c>), its mark (<c>pending</c>, <c>failed</c>), its tone (<c>on</c>, <c>off</c>, <c>alert</c>, <c>plain</c>,
/// <c>stale</c>) and the chip's own color for a lit or alert one, its group (a divider starts the next), and whether it has a sheet.
/// </summary>
public sealed record WebHomeTile(string Ref, string Glyph, string Name, string Value, string Note, string Action, string? Mark, string Tone, string? Color, int Group, bool Sheet);

/// <summary>
/// A scene the tab offers: its entity id, its whole name (the button's title, what a screen reader hears) and (2026-10-16) its label in its box,
/// the name without the box's in front.
/// </summary>
public sealed record WebHomeScene(string Id, string Name, string Label);

/// <summary>
/// A room on the Home tab (2026-10-16, the user's ask: the scenes' boxes became rooms): its key (the area's id, <c>device:&lt;id&gt;</c> or
/// <c>none</c>, stable across renames for the page's remembered folds), its switch's reference (<c>area:&lt;id&gt;</c>, a zone's
/// <c>entity:&lt;group light&gt;</c>, or none for a box with no lights), its name, whether its room is on the home bar (open by default), the
/// lights on of all (<c>3 of 5 on</c>), the switch's tone, mark, tint and state as a tile's, its devices' rows, and its scenes by name.
/// </summary>
public sealed record WebHomeRoom(string Key, string? Ref, string Name, bool OnBar, string? Value, string Tone, string? Mark, string? Color, bool? On, IReadOnlyList<WebHomeDevice> Devices, IReadOnlyList<WebHomeScene> Scenes);

/// <summary>
/// A device's row in a room (2026-10-16): its entity, glyph, name, state in words (a lit light's brightness after it), its chip's note, its quick
/// control (<c>switch</c>, <c>button</c> or <c>none</c>), a switch's state, the button's (or the switch's) action label and whether it asks first
/// (the action at index 0 of its sheet's), its mark, and its tone and tint as a tile's.
/// </summary>
public sealed record WebHomeDevice(string Entity, string Glyph, string Name, string State, string? Note, string Control, bool? On, string? Label, bool Ask, string? Mark, string Tone, string? Color);

/// <summary>
/// The video playing, as the tab shows it: its state, the video, the clock, the sound, a playlist's place, a failure's sentence, and (2026-10-16)
/// where it plays, the PC's window or the YouTube tab.
/// </summary>
public sealed record WebHomeVideo(string State, string? Title, string? Author, double Position, double Duration, int Volume, bool Muted, int? Entry, int? Count, string? Failure, string? Where = null);

/// <summary>The PC's meters in whole percents, and the network's rates as the perf bar words them; each missing when the machine cannot read it.</summary>
public sealed record WebHomePc(int? Cpu, int? Ram, int? Gpu, int? Vram, string? Down, string? Up);

/// <summary>The model card: the model, its server, its window, whether it loads or works, and the usage figures.</summary>
public sealed record WebHomeModel(string Model, string Server, int? Context, bool Loading, bool Busy, WebUsage? Usage);

/// <summary>
/// What the Chat badge on the other tabs reads (2026-10-15): a reply running, the asks open, what the PC waits on, the theme's name, the files' tag;
/// the tabs on; and (2026-10-16) whether the YouTube tab is, and the banner's words while a video waits for it (<see cref="Watch"/>, null for none).
/// </summary>
public sealed record WebPulse(bool Busy, int Asks, string? WaitingAtPc, string Theme, string Assets, bool ChatTab = true, bool HomeTab = true, bool ShoppingTab = true, bool YouTubeTab = false, string? Watch = null);

/// <summary>The Home tab's own words, the app's like every other.</summary>
public sealed record WebHomeWords(
    string ControlsOff,
    string Rooms,
    string Scenes,
    string AllLights,
    string Actions,
    string AskTheHouse,
    string Ask,
    string NowPlaying,
    string ThePc,
    string Model,
    string Yes,
    string No,
    string Close,
    string Loading,
    string Busy,
    string Window,
    string Down,
    string Up);

/// <summary>A tile's sheet: a room's devices (its first row the room's all on and all off), or one device; the title and the state under it.</summary>
public sealed record WebHomeSheet(string Ref, string Title, string? State, IReadOnlyList<WebHomeSheetRow> Rows);

/// <summary>A row of a sheet: its entity (none for a room's own row), glyph, name, state, and the actions a tap offers.</summary>
public sealed record WebHomeSheetRow(string? Entity, string Glyph, string Name, string State, IReadOnlyList<WebHomeAction> Actions);

/// <summary>An action of a sheet's row: its index among the row's actions, its label, and whether it asks first.</summary>
public sealed record WebHomeAction(int Index, string Label, bool Ask);

/// <summary>A tile tapped; <see cref="Confirmed"/> once the page asked.</summary>
public sealed class WebHomeTapRequest
{
    public string? Ref { get; set; }

    public bool Confirmed { get; set; }
}

/// <summary>A sheet's action: the tile, the row's entity (none for a room's own row), the action's index and label (checked against now's), asked or not.</summary>
public sealed class WebHomeActRequest
{
    public string? Ref { get; set; }

    public string? Entity { get; set; }

    public int Index { get; set; }

    public string? Label { get; set; }

    public bool Confirmed { get; set; }
}

/// <summary>A scene to run.</summary>
public sealed class WebHomeSceneRequest
{
    public string? Id { get; set; }
}

/// <summary>A question for Home Assistant's Assist.</summary>
public sealed class WebHomeSayRequest
{
    public string? Text { get; set; }
}

/// <summary>The video remote: <c>play</c>, <c>pause</c>, <c>back</c>, <c>forward</c>, <c>quieter</c>, <c>louder</c>, <c>mute</c>, <c>unmute</c>, <c>previous</c>, <c>next</c>.</summary>
public sealed class WebHomeVideoRequest
{
    public string? Action { get; set; }
}

/// <summary>A choice of an ask: its id and its label, the PC's own wording.</summary>
public sealed record WebChoice(string Id, string Label);

/// <summary>One row of a pick card (later on 2026-10-14): a made-up id (<c>i0</c>, <c>i1</c>…), the row's words and a note under them.</summary>
public sealed record WebPickItem(string Id, string Label, string? Note = null);

/// <summary>One <c>ask_user</c> question: the text, its short title, single or multi-choice, its options.</summary>
public sealed record WebQuestion(string Question, string? Title, bool Multi, IReadOnlyList<string> Options);

/// <summary>
/// An open ask as the page shows it: <see cref="Body"/> plain text, <see cref="Lines"/> a command or a statement in monospace,
/// <see cref="BodyHtml"/> a plan as HTML; <see cref="TextChoice"/> the choice that takes typed text (Refine); <see cref="Questions"/>
/// for <c>ask_user</c>, answered with <see cref="SubmitLabel"/>.
/// </summary>
public sealed record WebAskView(
    string Id,
    string Kind,
    string Title,
    string? Body,
    IReadOnlyList<string>? Lines,
    string? BodyHtml,
    IReadOnlyList<WebChoice> Choices,
    string? TextChoice,
    IReadOnlyList<WebQuestion>? Questions,
    string? SubmitLabel,
    IReadOnlyList<WebPickItem>? Items = null,
    string? Current = null,
    bool Live = false);

/// <summary>
/// What the page's header and composer show: the profile and its two avatars, the model, whether a reply runs (Stop) or a model loads,
/// plan mode, the session's title, the phone's lines waiting, what the PC is waiting on, the app's version, and the page's own files' tag
/// (<see cref="WebUiAssets.Tag"/>: a page whose tag differs reloads itself). The tabs on; and (2026-10-16) the banner's words while a video
/// waits for a YouTube tab (<see cref="Watch"/>: the flag and the text in one, null for none).
/// </summary>
public sealed record WebState(
    string Profile,
    string ModelAvatar,
    string UserAvatar,
    string Model,
    bool Busy,
    bool Loading,
    bool PlanMode,
    string Title,
    IReadOnlyList<string> Queued,
    string? WaitingAtPc,
    string Version,
    string Assets = "",
    string Theme = "",
    bool ChatTab = true,
    bool HomeTab = true,
    bool ShoppingTab = true,
    bool YouTubeTab = false,
    string? Watch = null);

/// <summary>
/// What a page gets on (re)connecting: the transcript, the open asks, the state, the app's words the page shows of its own, the drafts held for
/// it, and the status row (2026-10-15).
/// </summary>
public sealed record WebSnapshot(IReadOnlyList<WebEntry> Entries, IReadOnlyList<WebAskView> Asks, WebState State, WebWords? Words = null, IReadOnlyList<string>? Drafts = null, WebStatus? Status = null, WebStrip? Strip = null, WebPoint? Point = null);

/// <summary>
/// The PC's picture strip as the page's drawer shows it (later on 2026-10-15): its ComfyUI pictures newest first and the one the PC has
/// highlighted. Always whole, so an event sent twice or out of order with the snapshot changes nothing.
/// </summary>
public sealed record WebStrip(IReadOnlyList<WebPicture> Pictures, int? Selected);

/// <summary>
/// The page's status row (2026-10-15): the PC's spinner label (the turn's stage, a fun verb, a tool, a load), the milliseconds since it started
/// and since its step did (the step only while the PC shows that clock), and the usage figures, every one optional. The page runs the clocks
/// itself from the moment the event lands.
/// </summary>
public sealed record WebStatus(string? Label, long? ElapsedMs, long? StepMs, WebUsage? Usage);

/// <summary>The usage figures apart (2026-10-15, <c>UsageText.HintParts</c>): the tokens in use, the share, the speed; a narrow phone drops the first, then the last.</summary>
public sealed record WebUsage(string Tokens, string? Percent, string? Speed);

/// <summary>
/// The full <c>/usage</c> figures for the page's card (2026-10-15; a meter and tiles since 2026-10-16, the user's ask: the lines in a text box
/// read as amateurish): the PC pane's own title, the context (null while nothing is known of it), the PC pane's sections in its order, and
/// <see cref="Empty"/> while nothing was counted. Built from <c>UsageText.Sections</c>, the pane's own source.
/// </summary>
public sealed record WebUsageCard(string Title, WebUsageContext? Context, IReadOnlyList<WebUsageSection> Sections, string? Empty);

/// <summary>
/// The card's meter (2026-10-16): its heading, the tokens in use and the window as the status row writes them (<c>38.4k</c>), the share
/// (past 100 after a server-side trim), and where the window's size came from (or <c>unknown</c>).
/// </summary>
public sealed record WebUsageContext(string Heading, string? InUse, string? Window, int? Percent, string Source);

/// <summary>One of the card's tiles (2026-10-16): the pane's section heading, its note, its rows.</summary>
public sealed record WebUsageSection(string Heading, string Note, IReadOnlyList<WebUsageRow> Rows);

/// <summary>A tile's row (2026-10-16): the pane's label and value, as the PC shows them.</summary>
public sealed record WebUsageRow(string Label, string Value);

/// <summary>
/// The words the page puts on its own (later on 2026-10-14), the app's like every other: a picture's fallbacks (after the file's name alone),
/// the overlay's link, a refused command's two offers (<see cref="UseLine"/> before the line), and a draft filled by a command with its undo.
/// </summary>
public sealed record WebWords(string PictureGone, string PictureTooLarge, string OpenOriginal, string SendAsMessage = "", string UseLine = "", string DraftFilled = "", string Undo = "", string Working = "", string Close = "", string NoReply = "", string ThinkingLive = "", string Gone = "",
    string ClearDraft = "", string DraftCleared = "", string ExpandAll = "", string CollapseAll = "", string ToTop = "", string ToBottom = "", string StripButton = "", string StripOne = "", string StripMany = "", string CloseStrip = "",
    string Live = "", string LiveLabel = "", string DeletePicture = "", string DeleteArmed = "",
    string PromptsButton = "", string PromptsNone = "", string PromptsSend = "", string PromptsToDraft = "", string Filter = "", string ClosePrompts = "",
    string PromptsTab = "", string MemoryTab = "", string HistoryTab = "", string MemoryNone = "", string HistoryNone = "", string SaveDraft = "",
    string DraftEmpty = "", string DeleteRow = "", string DeleteRowArmed = "", string ClearAll = "", string ClearAllArmed = "", string MemoryOff = "",
    string MemoryModeOff = "",
    string BotCheckHint = "", string BotCheckPicture = "", string BotCheckZoom = "", string BotCheckScrollUp = "", string BotCheckScrollDown = "",
    string BotCheckTypeHere = "", string BotCheckSend = "", string BotCheckEnter = "", string BotCheckBackspace = "", string BotCheckTab = "",
    string BotCheckStalled = "");

/// <summary>
/// The page's saved prompts, the sheet's 📣 tab (later on 2026-10-15): the profile's prompts, numbered as at the PC. The title went on
/// 2026-10-16, when the sheet took tabs that carry the names.
/// </summary>
public sealed record WebPrompts(IReadOnlyList<WebPrompt> Items);

/// <summary>One saved prompt: its number at the PC (from 1, the user's order), its text, and its id (2026-10-16, what a delete names).</summary>
public sealed record WebPrompt(int Number, string Text, string Id);

/// <summary>
/// The sheet's 💾 tab (2026-10-16): the profile's memories in the PC pane's order, and whether <i>Memory mode</i> is off (the tab says so,
/// and a save is refused).
/// </summary>
public sealed record WebMemories(IReadOnlyList<WebMemory> Items, bool ModeOff);

/// <summary>A memory: its text, and whether it is turned off (kept, but the model doesn't see it).</summary>
public sealed record WebMemory(string Text, bool Off);

/// <summary>The profile's message history for the page (2026-10-16): its lines oldest first, the PC's drafts set aside and paste tokens left out.</summary>
public sealed record WebHistory(IReadOnlyList<string> Lines);

/// <summary>The open asks, whole (an <c>asks</c> event).</summary>
public sealed record WebAsks(IReadOnlyList<WebAskView> Asks);

/// <summary>A shopping line: its id, its text (<c>ShoppingText.Line</c> without the note), the note, a guessed place, ticked.</summary>
public sealed record WebShopItem(long Id, string Text, string Note, bool Guess, bool Checked)
{
    /// <summary>The line's name alone (2026-10-16, the line's sheet: its title and the rename box).</summary>
    public string? Name { get; init; }

    /// <summary>The line's quantity (2026-10-16, the sheet's − qty +).</summary>
    public int Qty { get; init; }
}

/// <summary>A heading of the list and its lines.</summary>
public sealed record WebShopGroup(string Heading, IReadOnlyList<WebShopItem> Items);

/// <summary>
/// The shopping list as the page shows it: the header (the store and the counts), a sentence instead of the list when there is none,
/// the groups in the store's walk, then the ticked lines under <see cref="TickedHeading"/>. Every word the app's own. <see cref="Assets"/>
/// (2026-10-15) is the files' tag, so the shopping page reloads itself after an upgrade as the chat does.
/// </summary>
public sealed record WebShopping(string Header, string? Note, IReadOnlyList<WebShopGroup> Groups, string TickedHeading, IReadOnlyList<WebShopItem> Ticked, string GuessMark, string Assets = "")
{
    /// <summary>The stores by name (2026-10-15, the page's store picker), none while the list is unavailable.</summary>
    public IReadOnlyList<WebShopStore>? Stores { get; init; }

    /// <summary>The active store's id, or none.</summary>
    public long? Active { get; init; }

    /// <summary>The picker's label.</summary>
    public string? StoreLabel { get; init; }

    /// <summary>
    /// The list can be changed (2026-10-16, the list's own controls): false on the unavailable list, when the page hides the add row, 🕘, ☑ and ⊠
    /// and every post is refused. An empty but available list is writable: an empty list is when adding matters most.
    /// </summary>
    public bool Writable { get; init; }

    /// <summary>A line is still open (2026-10-16): the page offers ☑ all, else ☐ none.</summary>
    public bool AnyOpen { get; init; }

    /// <summary>The page's words for the list's controls (2026-10-16), the page having none of its own.</summary>
    public WebShopWords? Words { get; init; }
}

/// <summary>A store the page's picker offers (2026-10-15): its id and its name.</summary>
public sealed record WebShopStore(long Id, string Name);

/// <summary>
/// The Shopping tab's words for the list's controls (2026-10-16, <see cref="WebUiText.ShopWords"/>): the add box's placeholder and its ⊕, the
/// 🕘 past items and their sheet (title, filter, no-match line with <c>{0}</c> the filter, the forget 🗑️ and its armed words), ☑ all / ☐ none,
/// ⊠ clear ticked and its armed form, a row's › (<c>{0}</c> the line's name), the line's sheet (− and + quantity labels, the quantity's
/// name, rename and Save, the remove 🗑️ and its armed form), the sheets' close, and the sentence for a line gone while its sheet was open
/// (<see cref="WebUiText.NoSuchItem"/>). <c>ShoppingText</c>'s button words where they exist, the
/// spacing that Windows Terminal's two-cell glyphs need collapsed.
/// </summary>
public sealed record WebShopWords(
    string AddPlaceholder,
    string Add,
    string Past,
    string PastTitle,
    string Filter,
    string NoMatch,
    string Forget,
    string ForgetArmed,
    string TickAll,
    string UntickAll,
    string Clear,
    string ClearArmed,
    string Options,
    string Less,
    string More,
    string Quantity,
    string Rename,
    string Save,
    string Remove,
    string RemoveArmed,
    string Close,
    string Gone);

/// <summary>
/// What was bought before and is not on the list now (2026-10-16, the 🕘 sheet), the most often first, and the caption the PC's add page
/// shows over them (or its sentence for none).
/// </summary>
public sealed record WebBought(IReadOnlyList<WebBoughtItem> Items, string Caption);

/// <summary>A past item: its id, its row as the PC's add page reads it (name, last quantity, how often, when last), its name and last quantity.</summary>
public sealed record WebBoughtItem(long Id, string Text, string Name, int Qty);

/// <summary>A line or a past item named by its id (2026-10-16): re-add, forget, remove.</summary>
public sealed class WebShopId
{
    public long Id { get; set; }
}

/// <summary>A line's quantity, absolute (2026-10-16: the page's −/+ taps post once they stop; a repeat changes nothing).</summary>
public sealed class WebShopQuantity
{
    public long Id { get; set; }

    public int Qty { get; set; }
}

/// <summary>A line renamed (2026-10-16).</summary>
public sealed class WebShopRename
{
    public long Id { get; set; }

    public string? Name { get; set; }
}

/// <summary>Every line ticked (<see cref="On"/>) or unticked (2026-10-16), explicit.</summary>
public sealed class WebShopCheckAll
{
    public bool On { get; set; }
}

/// <summary>A sign-in pasted on the page (2026-10-15): the whole link or the bare token.</summary>
public sealed class WebSignInRequest
{
    public string? Link { get; set; }
}

/// <summary>The store the page picked (2026-10-15).</summary>
public sealed class WebStoreRequest
{
    public long Id { get; set; }
}

// ── The YouTube tab (2026-10-16) ─────────────────────────────────────────

/// <summary>
/// The YouTube tab's words, made on the server (<see cref="WebUiText.VideoWords"/>), with (later on 2026-10-16) the saved list's
/// (<see cref="Saved"/>).
/// </summary>
public sealed record WebVideoWords(string Waiting, string PlayHere, string TapForSound, string TapToStart, string Idle, string Loading, string Failed, string Closed)
{
    public WebSavedWords? Saved { get; init; }
}

/// <summary>
/// The saved list's words on the YouTube tab (later on 2026-10-16): the filter's placeholder and its no-match line (<c>{0}</c> the filter), the
/// row's details toggle, the moves, the two-tap delete and clear all (each with its armed words), the save button's two faces, and the line
/// while titles are looked up.
/// </summary>
public sealed record WebSavedWords(
    string Filter,
    string NoMatch,
    string Details,
    string MoveUp,
    string MoveDown,
    string MoveTop,
    string MoveBottom,
    string Delete,
    string DeleteArmed,
    string ClearAll,
    string ClearArmed,
    string SavePlaying,
    string Saved,
    string Looking);

/// <summary>
/// The YouTube tab's saved list (later on 2026-10-16, the user's ask: <c>/youtube list</c> under the player): the videos in the user's order, the
/// count's caption, the empty list's sentence, whether titles are being looked up, and the video the player has open (null for none).
/// </summary>
public sealed record WebSavedVideos(IReadOnlyList<WebSavedVideo> Items, string Caption, string Empty, bool Looking, WebSavedPlaying? Playing);

/// <summary>
/// One saved video: its id (every post names it; the page builds its thumbnail from it), title and channel (null until known), where it was
/// left, whether it was watched to its end, and the PC pane's footer as two lines (in the PC's time zone).
/// </summary>
public sealed record WebSavedVideo(string Id, string? Title, string? Channel, string Place, bool Watched, string Heading, string Details);

/// <summary>The video the player has open, and whether it is saved (the ☆/★).</summary>
public sealed record WebSavedPlaying(string Id, string? Title, bool Saved);

/// <summary>A saved video played on the tapping page (later on 2026-10-16): that page's id and the video's.</summary>
public sealed class WebSavedPlay
{
    public string? Page { get; set; }

    public string? Id { get; set; }
}

/// <summary>
/// An input from a bot check card's live view (2026-10-17): the ask it was sent from, its kind (<c>tap</c>, <c>down</c>, <c>up</c>, <c>scroll</c>,
/// <c>text</c>, <c>key</c>), the place as fractions of the picture, a scroll's distance in CSS pixels, a text, a key's name.
/// </summary>
public sealed class WebBotCheckInput
{
    public string? AskId { get; set; }

    public string? Kind { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double DeltaY { get; set; }

    public string? Text { get; set; }

    public string? Key { get; set; }
}

/// <summary>A picture of the Browser window for a bot check card (2026-10-17): the JPEG and its tag (the ETag the page sends back).</summary>
public sealed record WebBotCheckFrame(byte[] Jpeg, string Tag);

/// <summary>A saved video named by its id (later on 2026-10-16): the remove.</summary>
public sealed class WebSavedId
{
    public string? Id { get; set; }
}

/// <summary>A saved video moved (later on 2026-10-16): <c>up</c>, <c>down</c>, <c>top</c> or <c>bottom</c>, so no extreme numbers go over JSON.</summary>
public sealed class WebSavedMove
{
    public string? Id { get; set; }

    public string? To { get; set; }
}

/// <summary>
/// The Resume offer (2026-10-16): the last video or playlist, the entry, where to start (the saved videos' rewind applied) and the button's
/// ready words.
/// </summary>
public sealed record WebVideoResume(string? VideoId, string? PlaylistId, int? Entry, double Start, string Label);

/// <summary>
/// A YouTube stream's first event: the page id to use from now on (a duplicated tab gets a fresh one), the tab's words, whether this page plays
/// (else it waits, with Play here), the Resume offer, how long the page waits for YouTube's player script before it reports a failure, and
/// (later on 2026-10-16) <see cref="Play"/>: a Play here took the video with it, so the page resumes the offer by itself.
/// </summary>
public sealed record WebVideoHello(string Page, WebVideoWords Words, bool Owner, WebVideoResume? Resume, int ApiWaitMs, bool Play = false);

/// <summary>A YouTube page in view or not (later on 2026-10-16): <c>POST /api/youtube/seen</c>, on every change and every reconnect.</summary>
public sealed class WebVideoSeen
{
    public string? Page { get; set; }

    public bool Visible { get; set; }
}

/// <summary>An <c>owner</c> event: this page plays now (or waits), with the Resume offer a promoted page draws.</summary>
public sealed record WebVideoOwner(bool Owner, WebVideoResume? Resume);

/// <summary>A report from the YouTube tab's player: the page's id and its message, the PC page's own JSON as a string inside this one.</summary>
public sealed class WebVideoReport
{
    public string? Page { get; set; }

    public string? Message { get; set; }
}

/// <summary>The source-generated serializer for the Web UI's wire shapes (reflection is off under NativeAOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebSendRequest))]
[JsonSerializable(typeof(WebAnswerRequest))]
[JsonSerializable(typeof(WebCheckRequest))]
[JsonSerializable(typeof(WebStoreRequest))]
[JsonSerializable(typeof(WebSignInRequest))]
[JsonSerializable(typeof(WebHome))]
[JsonSerializable(typeof(WebHomeRoom))]
[JsonSerializable(typeof(WebHomeSheet))]
[JsonSerializable(typeof(WebHomeTapRequest))]
[JsonSerializable(typeof(WebHomeActRequest))]
[JsonSerializable(typeof(WebHomeSceneRequest))]
[JsonSerializable(typeof(WebHomeSayRequest))]
[JsonSerializable(typeof(WebHomeVideoRequest))]
[JsonSerializable(typeof(WebPulse))]
[JsonSerializable(typeof(WebResult))]
[JsonSerializable(typeof(WebEntry))]
[JsonSerializable(typeof(WebDelta))]
[JsonSerializable(typeof(WebPicture))]
[JsonSerializable(typeof(WebPickItem))]
[JsonSerializable(typeof(WebCompleteRequest))]
[JsonSerializable(typeof(WebCompletion))]
[JsonSerializable(typeof(WebCompletionItem))]
[JsonSerializable(typeof(WebRemoved))]
[JsonSerializable(typeof(WebEnd))]
[JsonSerializable(typeof(WebDiff))]
[JsonSerializable(typeof(WebReplaced))]
[JsonSerializable(typeof(WebTextBack))]
[JsonSerializable(typeof(WebState))]
[JsonSerializable(typeof(WebSnapshot))]
[JsonSerializable(typeof(WebStrip))]
[JsonSerializable(typeof(WebPoint))]
[JsonSerializable(typeof(WebPictureDelete))]
[JsonSerializable(typeof(WebStripSelect))]
[JsonSerializable(typeof(WebWords))]
[JsonSerializable(typeof(WebStatus))]
[JsonSerializable(typeof(WebUsageCard))]
[JsonSerializable(typeof(WebPrompts))]
[JsonSerializable(typeof(WebTextBody))]
[JsonSerializable(typeof(WebPromptDelete))]
[JsonSerializable(typeof(WebMemories))]
[JsonSerializable(typeof(WebHistory))]
[JsonSerializable(typeof(WebAsks))]
[JsonSerializable(typeof(WebShopping))]
[JsonSerializable(typeof(WebShopWords))]
[JsonSerializable(typeof(WebBought))]
[JsonSerializable(typeof(WebBoughtItem))]
[JsonSerializable(typeof(WebShopId))]
[JsonSerializable(typeof(WebShopQuantity))]
[JsonSerializable(typeof(WebShopRename))]
[JsonSerializable(typeof(WebShopCheckAll))]
[JsonSerializable(typeof(WebVideoHello))]
[JsonSerializable(typeof(WebVideoWords))]
[JsonSerializable(typeof(WebVideoSeen))]
[JsonSerializable(typeof(WebSavedWords))]
[JsonSerializable(typeof(WebSavedVideos))]
[JsonSerializable(typeof(WebSavedVideo))]
[JsonSerializable(typeof(WebSavedPlaying))]
[JsonSerializable(typeof(WebSavedPlay))]
[JsonSerializable(typeof(WebSavedId))]
[JsonSerializable(typeof(WebBotCheckInput))]
[JsonSerializable(typeof(WebSavedMove))]
[JsonSerializable(typeof(WebVideoResume))]
[JsonSerializable(typeof(WebVideoOwner))]
[JsonSerializable(typeof(WebVideoReport))]
public sealed partial class WebUiJsonContext : JsonSerializerContext;
