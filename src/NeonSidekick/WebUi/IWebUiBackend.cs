using System.Net;

namespace NeonSidekick.WebUi;

/// <summary>What a post did, for the HTTP status: taken, refused (with a sentence), a conflict (gone or not now), or too many.</summary>
public enum WebOutcome
{
    Ok,
    Refused,
    Conflict,
    NotFound,
    TooMany,
}

/// <summary>A post's outcome and its sentence for the page.</summary>
public readonly record struct WebReply(WebOutcome Outcome, string? Message = null)
{
    public static WebReply Ok => new(WebOutcome.Ok);
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
}
