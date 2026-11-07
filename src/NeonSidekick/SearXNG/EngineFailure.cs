// SPDX-License-Identifier: AGPL-3.0-or-later

namespace SearXNG.Net;

/// <summary>Why an engine gave no results.</summary>
public enum EngineFailureKind
{
    /// <summary>The engine didn't answer in time.</summary>
    Timeout,

    /// <summary>The connection failed.</summary>
    Network,

    /// <summary>An HTTP error with no more specific meaning.</summary>
    HttpError,

    /// <summary>The site refused access.</summary>
    AccessDenied,

    /// <summary>The site asked for a CAPTCHA.</summary>
    Captcha,

    /// <summary>The site is rate limiting.</summary>
    TooManyRequests,

    /// <summary>The answer couldn't be understood, usually because the site changed.</summary>
    UnexpectedResponse,

    /// <summary>An earlier failure has this engine resting, so it wasn't asked.</summary>
    Suspended,
}

/// <summary>An engine that gave no results for this search.</summary>
/// <param name="Engine">The engine's name.</param>
/// <param name="Kind">The kind of failure. For <see cref="EngineFailureKind.Suspended"/>, see <paramref name="Message"/>.</param>
/// <param name="Message">A short description for logs and people.</param>
/// <param name="RefusedUrl">The page that refused the request, which a host could open for a person to get past a check; null when there is none.</param>
/// <param name="RestCause">For <see cref="EngineFailureKind.Suspended"/>, the kind of failure that started the rest; otherwise null.</param>
public sealed record EngineFailure(string Engine, EngineFailureKind Kind, string Message, Uri? RefusedUrl = null, EngineFailureKind? RestCause = null);

/// <summary>Raised by an engine that was refused or got an answer it couldn't use.</summary>
public sealed class EngineException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">The kind of failure.</param>
    /// <param name="message">A short description.</param>
    /// <param name="suspendFor">How long the engine should rest; <see cref="TimeSpan.Zero"/> for none, and null to go by <paramref name="kind"/>: none for <see cref="EngineFailureKind.UnexpectedResponse"/>, otherwise the default rest.</param>
    /// <param name="refusedUrl">The page that refused the request, if any.</param>
    public EngineException(EngineFailureKind kind, string message, TimeSpan? suspendFor = null, Uri? refusedUrl = null)
        : base(message)
    {
        Kind = kind;
        SuspendFor = suspendFor;
        RefusedUrl = refusedUrl;
    }

    /// <summary>The kind of failure.</summary>
    public EngineFailureKind Kind { get; }

    /// <summary>How long the engine should rest; <see cref="TimeSpan.Zero"/> for none, and null to go by <see cref="Kind"/>: none for <see cref="EngineFailureKind.UnexpectedResponse"/>, otherwise the default rest.</summary>
    public TimeSpan? SuspendFor { get; }

    /// <summary>The page that refused the request, if any.</summary>
    public Uri? RefusedUrl { get; }
}
