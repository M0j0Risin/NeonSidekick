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
public sealed record WebResult(bool Ok, string? Message = null, bool AsText = false, string? Use = null);

/// <summary>One transcript entry as the page shows it (<see cref="TranscriptFeed"/>); a picture entry's pictures, when the page may load them.</summary>
public sealed record WebEntry(long Seq, string Kind, string Text, string? Glyph, string? Html, bool Failed, IReadOnlyList<WebPicture>? Pictures = null);

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

/// <summary>An entry's end: the reply's Markdown as HTML.</summary>
public sealed record WebEnd(long Seq, string Html);

/// <summary>A text handed back to the page's draft: a message withdrawn or dropped.</summary>
public sealed record WebTextBack(string Text);

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
    string Assets = "");

/// <summary>What a page gets on (re)connecting: the transcript, the open asks, the state, and the app's words the page shows of its own.</summary>
public sealed record WebSnapshot(IReadOnlyList<WebEntry> Entries, IReadOnlyList<WebAskView> Asks, WebState State, WebWords? Words = null, IReadOnlyList<string>? Drafts = null);

/// <summary>
/// The words the page puts on its own (later on 2026-10-14), the app's like every other: a picture's fallbacks (after the file's name alone),
/// the overlay's link, a refused command's two offers (<see cref="UseLine"/> before the line), and a draft filled by a command with its undo.
/// </summary>
public sealed record WebWords(string PictureGone, string PictureTooLarge, string OpenOriginal, string SendAsMessage = "", string UseLine = "", string DraftFilled = "", string Undo = "");

/// <summary>The open asks, whole (an <c>asks</c> event).</summary>
public sealed record WebAsks(IReadOnlyList<WebAskView> Asks);

/// <summary>A shopping line: its id, its text (<c>ShoppingText.Line</c> without the note), the note, a guessed place, ticked.</summary>
public sealed record WebShopItem(long Id, string Text, string Note, bool Guess, bool Checked);

/// <summary>A heading of the list and its lines.</summary>
public sealed record WebShopGroup(string Heading, IReadOnlyList<WebShopItem> Items);

/// <summary>
/// The shopping list as the page shows it: the header (the store and the counts), a sentence instead of the list when there is none,
/// the groups in the store's walk, then the ticked lines under <see cref="TickedHeading"/>. Every word the app's own.
/// </summary>
public sealed record WebShopping(string Header, string? Note, IReadOnlyList<WebShopGroup> Groups, string TickedHeading, IReadOnlyList<WebShopItem> Ticked, string GuessMark);

/// <summary>The source-generated serializer for the Web UI's wire shapes (reflection is off under NativeAOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebSendRequest))]
[JsonSerializable(typeof(WebAnswerRequest))]
[JsonSerializable(typeof(WebCheckRequest))]
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
[JsonSerializable(typeof(WebTextBack))]
[JsonSerializable(typeof(WebState))]
[JsonSerializable(typeof(WebSnapshot))]
[JsonSerializable(typeof(WebWords))]
[JsonSerializable(typeof(WebAsks))]
[JsonSerializable(typeof(WebShopping))]
public sealed partial class WebUiJsonContext : JsonSerializerContext;
