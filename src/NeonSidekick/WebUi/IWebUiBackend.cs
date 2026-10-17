using System.Net;

namespace NeonSidekick.WebUi;

/// <summary>What a post did, for the HTTP status: taken, refused (with a sentence), a conflict (gone or not now), or too many; forbidden by a setting (2026-10-15).</summary>
public enum WebOutcome
{
    Ok,
    Refused,
    Conflict,
    NotFound,
    TooMany,
    Forbidden,
}

/// <summary>
/// A post's outcome and its sentence for the page; for a refused command, its offers (<see cref="WebResult"/>); for a Home tap that asks first
/// (2026-10-15), the question.
/// </summary>
public readonly record struct WebReply(WebOutcome Outcome, string? Message = null, bool AsText = false, string? Use = null, string? Prompt = null, string? Open = null)
{
    public static WebReply Ok => new(WebOutcome.Ok);
}

/// <summary>
/// The Home tab's side of the app (2026-10-15, the plan's Part D): called on Kestrel threads, never waiting on the screen; the screen implements it.
/// Each read renews the tab's hold on Home Assistant's live states and the PC's meters.
/// </summary>
public interface IWebHome
{
    /// <summary>The tab now.</summary>
    WebHome Read();

    /// <summary>A tile's sheet, or null for a reference outside the tiles' reach.</summary>
    WebHomeSheet? Sheet(string reference);

    WebReply Tap(WebHomeTapRequest request, IPAddress? remote);

    WebReply Act(WebHomeActRequest request, IPAddress? remote);

    WebReply Scene(WebHomeSceneRequest request, IPAddress? remote);

    /// <summary>Assist's answer as the reply's message.</summary>
    Task<WebReply> SayAsync(WebHomeSayRequest request, IPAddress? remote, CancellationToken cancellationToken);

    WebReply Video(WebHomeVideoRequest request, IPAddress? remote);
}

/// <summary>
/// What the Web UI's server asks of the app (2026-10-14): the host (<see cref="WebUiHost"/>) only turns HTTP into these calls and their
/// answers back into HTTP. The screen's <see cref="WebUiSession"/> implements it; the host tests use a fake. Every member is called on
/// a Kestrel thread and must neither block on the screen nor write to the transcript (the turn task is its only writer).
/// </summary>
public interface IWebUiBackend
{
    /// <summary>The fan-out every page's event stream reads.</summary>
    WebEventHub Events { get; }

    /// <summary>What a page gets as it (re)connects: the transcript, the open asks, the state.</summary>
    WebSnapshot Snapshot();

    /// <summary>A browser signed in from <paramref name="remote"/> (a notice at the PC and an audit line).</summary>
    void SignedIn(IPAddress? remote);

    /// <summary>A message from the page.</summary>
    WebReply Send(WebSendRequest request, IPAddress? remote);

    /// <summary>The page's Stop.</summary>
    WebReply Stop(IPAddress? remote);

    /// <summary>An answer to an open ask.</summary>
    WebReply Answer(WebAnswerRequest request, IPAddress? remote);

    /// <summary>The shopping list as the page shows it.</summary>
    WebShopping Shopping();

    /// <summary>A shopping line ticked or unticked.</summary>
    WebReply Check(WebCheckRequest request, IPAddress? remote);

    /// <summary>The store the shopping list is walked through, picked on the page (2026-10-15).</summary>
    WebReply UseStore(WebStoreRequest request, IPAddress? remote);

    // The Shopping tab's list controls (2026-10-16, the user's ask): defaults that find nothing, for a backend without a list.

    /// <summary>What was bought before and is not on the list now, for the 🕘 sheet; null where there is no list (a 404).</summary>
    WebBought? ShopBought() => null;

    /// <summary>The add box's items on the list (<c>2 milk, eggs</c>).</summary>
    WebReply ShopAdd(WebTextBody request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A past item added again at its last quantity.</summary>
    WebReply ShopReAdd(WebShopId request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A past item forgotten, the page's two taps having been the yes.</summary>
    WebReply ShopForget(WebShopId request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A line's quantity, absolute, floored at 1.</summary>
    WebReply ShopQuantity(WebShopQuantity request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A line renamed (a name another line has merges the two, as at the PC).</summary>
    WebReply ShopRename(WebShopRename request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A line removed, the page's two taps having been the yes.</summary>
    WebReply ShopRemove(WebShopId request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>Every line ticked or unticked, explicit.</summary>
    WebReply ShopCheckAll(WebShopCheckAll request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>The ticked lines cleared, the page's two taps having been the yes.</summary>
    WebReply ShopClearTicked(IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>
    /// This run's tag in every picture's address (2026-10-14): 128 random bits made once per process, so an address from another run of the app
    /// (a phone's old tab, a cached page) names nothing.
    /// </summary>
    string PictureRun { get; }

    /// <summary>Picture <paramref name="id"/> while the transcript holds it, or null (never drawn, trimmed, a conversation since).</summary>
    WebPictureSource? Picture(int id);

    /// <summary>The page's dropdown for the text it sent (later on 2026-10-14); quick, and an empty answer when busy.</summary>
    WebCompletion Complete(WebCompleteRequest request);

    /// <summary>The full <c>/usage</c> figures for the page's card (2026-10-15); reads only.</summary>
    WebUsageCard Usage();

    /// <summary>The profile's saved prompts for the page's sheet (later on 2026-10-15); reads only.</summary>
    WebPrompts Prompts();

    /// <summary>The page's draft saved as a prompt (2026-10-16, the sheet's ⇪): the <c>/prompts save</c> answer; refused for an empty or too long one.</summary>
    WebReply SavePrompt(WebTextBody request, IPAddress? remote);

    /// <summary>A saved prompt deleted by its id (2026-10-16, the sheet's 🗑️); NotFound when it is gone.</summary>
    WebReply DeletePrompt(WebPromptDelete request, IPAddress? remote);

    /// <summary>The profile's memories for the sheet's 💾 tab (2026-10-16); reads only.</summary>
    WebMemories Memories();

    /// <summary>The page's draft saved as a memory (2026-10-16): <c>/remember</c>'s answer, refused while <i>Memory mode</i> is off.</summary>
    WebReply SaveMemory(WebTextBody request, IPAddress? remote);

    /// <summary>A memory deleted by its text (2026-10-16; memories have no ids), in any mode; NotFound when it is gone.</summary>
    WebReply DeleteMemory(WebTextBody request, IPAddress? remote);

    /// <summary>The profile's message history for ↑/↓ and the sheet's 🕘 tab (2026-10-16): no drafts set aside, no paste tokens; reads only.</summary>
    WebHistory History();

    /// <summary>The message history cleared from the 🕘 tab (2026-10-16): what <c>/msgclear</c>'s yes does, the two taps having been the yes.</summary>
    WebReply ClearHistory(IPAddress? remote);

    /// <summary>The running reflection canceled from the status row's 🧠 (2026-10-17), the two taps having been the yes; NotFound without a screen.</summary>
    WebReply CancelLearning(IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>The queue held after a cancel released from the page's banner (2026-10-17); NotFound without a screen.</summary>
    WebReply ResumeQueue(IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>The Home tab (2026-10-15), or null in a build without one.</summary>
    IWebHome? Home { get; }

    /// <summary>
    /// The YouTube tab's player (2026-10-16), or null where there is none (no video window to route beside, headless, a test's backend): the
    /// host then answers <c>/api/youtube/*</c> with the tab-off 404 whatever the tabs say.
    /// </summary>
    IWebVideo? Video => null;

    /// <summary>The YouTube tab's saved list (later on 2026-10-16), or null where there is none; reads only (a missing title's lookup may start).</summary>
    WebSavedVideos? SavedVideos() => null;

    /// <summary>A saved video played on the tapping page (later on 2026-10-16): that page takes the player, the video resumes where it was left.</summary>
    WebReply PlaySaved(WebSavedPlay request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A saved video removed (later on 2026-10-16), the page's two taps having been the yes.</summary>
    WebReply RemoveSaved(WebSavedId request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>Every saved video removed (later on 2026-10-16), the page's two taps having been the yes.</summary>
    WebReply ClearSaved(IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>A saved video moved up, down, to the top or the bottom (later on 2026-10-16).</summary>
    WebReply MoveSaved(WebSavedMove request, IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>The video playing saved (later on 2026-10-16, the ☆).</summary>
    WebReply SaveCurrent(IPAddress? remote) => new(WebOutcome.NotFound);

    /// <summary>
    /// A bot check card's picture of the Browser window (2026-10-17), at most <paramref name="width"/> pixels wide: Forbidden with <i>Web UI bot
    /// check control</i> off, NotFound unless <paramref name="askId"/> is the live check's, the picture else (null when the window didn't draw).
    /// </summary>
    Task<(WebOutcome Outcome, WebBotCheckFrame? Frame)> BotCheckFrameAsync(string askId, int width, CancellationToken cancellationToken) =>
        Task.FromResult<(WebOutcome, WebBotCheckFrame?)>((WebOutcome.NotFound, null));

    /// <summary>A bot check card's input sent into the Browser window (2026-10-17): Forbidden, NotFound, Refused (an input it can't send) or Ok.</summary>
    Task<WebReply> BotCheckInputAsync(WebBotCheckInput input, IPAddress? remote, CancellationToken cancellationToken) =>
        Task.FromResult(new WebReply(WebOutcome.NotFound));

    /// <summary>An entry whole while the transcript holds it (2026-10-15: a page opening what its snapshot deferred), or null.</summary>
    WebEntry? Entry(long seq);

    /// <summary>What the Chat badge reads on the other tabs (2026-10-15).</summary>
    WebPulse Pulse();

    /// <summary>Which tabs are on (later on 2026-10-15), read at each request: a tab that is off has no page and its APIs answer 404.</summary>
    WebTabs Tabs();

    /// <summary>A strip picture picked on the page (later on 2026-10-15): the PC's highlight and open viewer move to it; not found when gone.</summary>
    WebReply SelectStripPicture(WebStripSelect request, IPAddress? remote);

    /// <summary>
    /// The page's 🗑️ (later on 2026-10-15): picture <c>Id</c>'s file deleted as <i>File deletes to Recycle bin</i> says, by the screen's own path for
    /// that id, never one from the page. NotFound for an id the transcript or the strip doesn't hold, or one with no file.
    /// </summary>
    WebReply DeletePicture(WebPictureDelete request, IPAddress? remote);

    /// <summary><c>/theme.css</c> (2026-10-15): the profile's theme, empty while <i>Web UI profile theme</i> is off.</summary>
    string ThemeCss();

    /// <summary><c>/manifest.webmanifest</c> (2026-10-15): an installed page's name, start, icons and colors.</summary>
    string Manifest();
}
