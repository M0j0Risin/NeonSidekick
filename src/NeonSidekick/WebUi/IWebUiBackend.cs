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
public readonly record struct WebReply(WebOutcome Outcome, string? Message = null, bool AsText = false, string? Use = null, string? Prompt = null)
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

    /// <summary>The Home tab (2026-10-15), or null in a build without one.</summary>
    IWebHome? Home { get; }

    /// <summary>An entry whole while the transcript holds it (2026-10-15: a page opening what its snapshot deferred), or null.</summary>
    WebEntry? Entry(long seq);

    /// <summary>What the Chat badge reads on the other tabs (2026-10-15).</summary>
    WebPulse Pulse();

    /// <summary><c>/theme.css</c> (2026-10-15): the profile's theme, empty while <i>Web UI profile theme</i> is off.</summary>
    string ThemeCss();

    /// <summary><c>/manifest.webmanifest</c> (2026-10-15): an installed page's name, start, icons and colors.</summary>
    string Manifest();
}
