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
/// over what the server decodes for a thumbnail (an <c>img</c> cannot read a status code).
/// </summary>
public sealed record WebPicture(int Id, string Name, string Thumb, string Mid, string Full, int Width, int Height, bool TooLarge);

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
/// <c>off</c>) and its sentence, whether the tab may act (<i>Web UI home controls</i>), the home bar's items as tiles, the scenes, the cards, what
/// the Chat badge reads (<see cref="Pulse"/>), and the tab's words.
/// </summary>
public sealed record WebHome(
    string Status,
    string? StatusText,
    bool Controls,
    IReadOnlyList<WebHomeTile> Tiles,
    IReadOnlyList<WebHomeScene> Scenes,
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

/// <summary>A scene the tab offers: its entity id and its name.</summary>
public sealed record WebHomeScene(string Id, string Name);

/// <summary>The PC's YouTube window as the tab shows it: its state, the video, the clock, the sound, a playlist's place, a failure's sentence.</summary>
public sealed record WebHomeVideo(string State, string? Title, string? Author, double Position, double Duration, int Volume, bool Muted, int? Entry, int? Count, string? Failure);

/// <summary>The PC's meters in whole percents, and the network's rates as the perf bar words them; each missing when the machine cannot read it.</summary>
public sealed record WebHomePc(int? Cpu, int? Ram, int? Gpu, int? Vram, string? Down, string? Up);

/// <summary>The model card: the model, its server, its window, whether it loads or works, and the usage figures.</summary>
public sealed record WebHomeModel(string Model, string Server, int? Context, bool Loading, bool Busy, WebUsage? Usage);

/// <summary>What the Chat badge on the other tabs reads (2026-10-15): a reply running, the asks open, what the PC waits on, the theme's name, the files' tag.</summary>
public sealed record WebPulse(bool Busy, int Asks, string? WaitingAtPc, string Theme, string Assets);

/// <summary>The Home tab's own words, the app's like every other.</summary>
public sealed record WebHomeWords(
    string ControlsOff,
    string Scenes,
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
    string? Current = null);

/// <summary>
/// What the page's header and composer show: the profile and its two avatars, the model, whether a reply runs (Stop) or a model loads,
/// plan mode, the session's title, the phone's lines waiting, what the PC is waiting on, the app's version, and the page's own files' tag
/// (<see cref="WebUiAssets.Tag"/>: a page whose tag differs reloads itself).
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
    string Theme = "");

/// <summary>
/// What a page gets on (re)connecting: the transcript, the open asks, the state, the app's words the page shows of its own, the drafts held for
/// it, and the status row (2026-10-15).
/// </summary>
public sealed record WebSnapshot(IReadOnlyList<WebEntry> Entries, IReadOnlyList<WebAskView> Asks, WebState State, WebWords? Words = null, IReadOnlyList<string>? Drafts = null, WebStatus? Status = null);

/// <summary>
/// The page's status row (2026-10-15): the PC's spinner label (the turn's stage, a fun verb, a tool, a load), the milliseconds since it started
/// and since its step did (the step only while the PC shows that clock), and the usage figures, every one optional. The page runs the clocks
/// itself from the moment the event lands.
/// </summary>
public sealed record WebStatus(string? Label, long? ElapsedMs, long? StepMs, WebUsage? Usage);

/// <summary>The usage figures apart (2026-10-15, <c>UsageText.HintParts</c>): the tokens in use, the share, the speed; a narrow phone drops the first, then the last.</summary>
public sealed record WebUsage(string Tokens, string? Percent, string? Speed);

/// <summary>The full <c>/usage</c> figures for the page's card (2026-10-15): its title and its lines.</summary>
public sealed record WebUsageCard(string Title, IReadOnlyList<string> Lines);

/// <summary>
/// The words the page puts on its own (later on 2026-10-14), the app's like every other: a picture's fallbacks (after the file's name alone),
/// the overlay's link, a refused command's two offers (<see cref="UseLine"/> before the line), and a draft filled by a command with its undo.
/// </summary>
public sealed record WebWords(string PictureGone, string PictureTooLarge, string OpenOriginal, string SendAsMessage = "", string UseLine = "", string DraftFilled = "", string Undo = "", string Working = "", string Close = "", string NoReply = "", string ThinkingLive = "", string Gone = "");

/// <summary>The open asks, whole (an <c>asks</c> event).</summary>
public sealed record WebAsks(IReadOnlyList<WebAskView> Asks);

/// <summary>A shopping line: its id, its text (<c>ShoppingText.Line</c> without the note), the note, a guessed place, ticked.</summary>
public sealed record WebShopItem(long Id, string Text, string Note, bool Guess, bool Checked);

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
}

/// <summary>A store the page's picker offers (2026-10-15): its id and its name.</summary>
public sealed record WebShopStore(long Id, string Name);

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

/// <summary>The source-generated serializer for the Web UI's wire shapes (reflection is off under NativeAOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebSendRequest))]
[JsonSerializable(typeof(WebAnswerRequest))]
[JsonSerializable(typeof(WebCheckRequest))]
[JsonSerializable(typeof(WebStoreRequest))]
[JsonSerializable(typeof(WebSignInRequest))]
[JsonSerializable(typeof(WebHome))]
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
[JsonSerializable(typeof(WebWords))]
[JsonSerializable(typeof(WebStatus))]
[JsonSerializable(typeof(WebUsageCard))]
[JsonSerializable(typeof(WebAsks))]
[JsonSerializable(typeof(WebShopping))]
public sealed partial class WebUiJsonContext : JsonSerializerContext;
