using System.Text.Json.Serialization;

namespace NeonSidekick.WebUi;

// The Web UI's wire shapes (2026-10-14): what the pages post and what the server sends back or streams. Every one is registered in
// WebUiJsonContext (source-generated; reflection is off). Property names go out camelCase.

/// <summary>A message the page sends: the page's own id for it (a repeat is not sent twice) and the text.</summary>
public sealed class WebSendRequest
{
    public string? Id { get; set; }

    public string? Text { get; set; }
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

/// <summary>A post's answer: whether it was taken, and a sentence for the page when it was not.</summary>
public sealed record WebResult(bool Ok, string? Message = null);

/// <summary>One transcript entry as the page shows it (<see cref="TranscriptFeed"/>).</summary>
public sealed record WebEntry(long Seq, string Kind, string Text, string? Glyph, string? Html, bool Failed);

/// <summary>Text growing on an entry: <see cref="Text"/> belongs at character <see cref="At"/> of entry <see cref="Seq"/>.</summary>
public sealed record WebDelta(long Seq, int At, string Text);

/// <summary>An entry's end: the reply's Markdown as HTML.</summary>
public sealed record WebEnd(long Seq, string Html);

/// <summary>A text handed back to the page's draft: a message withdrawn or dropped.</summary>
public sealed record WebTextBack(string Text);

/// <summary>A choice of an ask: its id and its label, the PC's own wording.</summary>
public sealed record WebChoice(string Id, string Label);

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
    string? SubmitLabel);

/// <summary>
/// What the page's header and composer show: the profile and its two avatars, the model, whether a reply runs (Stop) or a model loads,
/// plan mode, the session's title, the phone's lines waiting, what the PC is waiting on, the app's version.
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
    string Version);

/// <summary>What a page gets on (re)connecting: the transcript, the open asks and the state.</summary>
public sealed record WebSnapshot(IReadOnlyList<WebEntry> Entries, IReadOnlyList<WebAskView> Asks, WebState State);

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
[JsonSerializable(typeof(WebEnd))]
[JsonSerializable(typeof(WebTextBack))]
[JsonSerializable(typeof(WebState))]
[JsonSerializable(typeof(WebSnapshot))]
[JsonSerializable(typeof(WebAsks))]
[JsonSerializable(typeof(WebShopping))]
public sealed partial class WebUiJsonContext : JsonSerializerContext;
