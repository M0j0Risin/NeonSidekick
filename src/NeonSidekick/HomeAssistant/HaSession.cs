using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// The Home Assistant door the tools and <c>/ha</c> share (2026-09-28): the client for the URL and token in force (made
/// again when either changes, <see cref="Comfy.ComfyStudio"/>'s way), the per-call timeout, and the snapshot of entities and
/// areas — read afresh when it is older than <see cref="SnapshotAge"/> or a service call has changed something since, so
/// "turn off the den, then tell me what's on" reads the new state. The last snapshot is kept for <c>/ha</c>'s completion,
/// which never reaches the network.
/// </summary>
public sealed class HaSession : IDisposable
{
    /// <summary>How long a snapshot is used before it is read again.</summary>
    public static readonly TimeSpan SnapshotAge = TimeSpan.FromSeconds(30);

    private readonly Func<AppSettingsData> _effective;
    private readonly Func<Uri, string, HaClient> _clientFactory;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private HaClient? _client;
    private string? _clientKey;
    private HaSnapshot? _snapshot;
    private bool _stale;

    /// <param name="effective">The settings, read at every call.</param>
    /// <param name="clientFactory">Makes the client for a URL and token; null = <see cref="HaClient"/> over its own transport. Tests pass one over a stub handler.</param>
    /// <param name="time">The clock the snapshot's age is read on; null = the system's.</param>
    public HaSession(Func<AppSettingsData> effective, Func<Uri, string, HaClient>? clientFactory = null, TimeProvider? time = null)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _clientFactory = clientFactory ?? ((url, token) => new HaClient(url, token));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The settings as they stand now.</summary>
    public AppSettingsData Effective => _effective();

    /// <summary>The clock (the history tool's "now").</summary>
    public TimeProvider Time => _time;

    /// <summary>The server as the settings stand, or null when <c>Home Assistant URL</c> is empty or no http(s) URL.</summary>
    public static Uri? ServerOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Uri.TryCreate(effective.HomeAssistantUrl?.Trim(), UriKind.Absolute, out var url) && Web.WebFetcher.IsHttp(url) ? url : null;
    }

    /// <summary>
    /// The token, readable: a <c>dpapi:</c> value decrypted (this Windows user on this machine), a plain one (a variable, a
    /// hand edit) as it is. Null when none is set or the stored one cannot be read — another user's or machine's profile.
    /// </summary>
    public static string? TokenOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return SettingsSecrets.Reveal(effective.HomeAssistantToken);
    }

    /// <summary>
    /// The token as the settings file keeps it: encrypted with DPAPI for this Windows user; empty for empty. Where DPAPI is
    /// unavailable the plain token is kept and <paramref name="error"/> says why, so the caller can warn.
    /// </summary>
    public static string Protect(string plain, out string? error) => SettingsSecrets.Protect(plain, out error);

    /// <summary>Whether a URL and a readable token are both set.</summary>
    public static bool Configured(AppSettingsData effective) => ServerOf(effective) is not null && TokenOf(effective) is not null;

    /// <summary>The per-call ceiling: <c>Home Assistant timeout (s)</c>, clamped.</summary>
    public static TimeSpan TimeoutOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return TimeSpan.FromSeconds(Math.Clamp(effective.HomeAssistantTimeoutSeconds, AppSettingsData.MinHomeAssistantTimeoutSeconds, AppSettingsData.MaxHomeAssistantTimeoutSeconds));
    }

    /// <summary>The per-call ceiling as the settings stand now.</summary>
    public TimeSpan Timeout => TimeoutOf(_effective());

    /// <summary>The client for the URL and token in force, made again when either changed; null without both.</summary>
    public HaClient? Client()
    {
        var effective = _effective();
        if (ServerOf(effective) is not { } url || TokenOf(effective) is not { } token)
        {
            return null;
        }

        string key = url.AbsoluteUri + "\n" + token;
        lock (_gate)
        {
            if (_client is null || !string.Equals(_clientKey, key, StringComparison.Ordinal))
            {
                _client?.Dispose();
                _client = _clientFactory(url, token);
                _clientKey = key;
                _snapshot = null;
            }

            return _client;
        }
    }

    /// <summary>The last snapshot read, however old; null before the first. For completion, which never waits on the network.</summary>
    public HaSnapshot? Last
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>Marks the snapshot stale: the next <see cref="SnapshotAsync"/> reads again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _stale = true;
        }
    }

    /// <summary>
    /// The entities and areas: the kept snapshot while it is young and nothing changed, else read afresh (<c>/api/states</c>
    /// and the areas template; a failed template reads as no areas). The failure as an <c>Error:</c> sentence.
    /// </summary>
    public async Task<(HaSnapshot? Snapshot, string? Error)> SnapshotAsync(CancellationToken cancellationToken, bool fresh = false)
    {
        if (Client() is not { } client)
        {
            return (null, HaText.NotConfigured);
        }

        lock (_gate)
        {
            if (!fresh && !_stale && _snapshot is { } kept && _time.GetUtcNow() - kept.Taken < SnapshotAge)
            {
                return (kept, null);
            }
        }

        var timeout = Timeout;
        var states = await client.StatesAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!states.Ok)
        {
            return (null, states.Error);
        }

        var areas = await client.TemplateAsync(HaSnapshot.AreasTemplate, timeout, cancellationToken).ConfigureAwait(false);
        if (!areas.Ok)
        {
            DiagnosticLog.Debug(HaText.Category, "The areas template failed: " + areas.Error);
        }

        var snapshot = HaSnapshot.Parse(states.Body, areas.Ok ? areas.Body : null, _time.GetUtcNow());
        if (snapshot is null)
        {
            return (null, HaText.BadAnswer("/api/states"));
        }

        lock (_gate)
        {
            _snapshot = snapshot;
            _stale = false;
        }

        return (snapshot, null);
    }

    /// <summary>
    /// Calls <paramref name="domain"/>.<paramref name="service"/> on <paramref name="ids"/> with <paramref name="data"/> (a JSON
    /// object's text, or null) — no policy here, the caller judged it. The snapshot is marked stale after, whatever came back.
    /// </summary>
    public async Task<HaReply> CallAsync(string domain, string service, IReadOnlyList<string> ids, string? data, CancellationToken cancellationToken, bool returnResponse = false)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(ids);
        if (Client() is not { } client)
        {
            return HaReply.Failed(HaText.NotConfigured);
        }

        string? body = HaJson.ServiceBody(ids, data, out string? error);
        if (body is null)
        {
            return HaReply.Failed(error ?? HaText.BadData);
        }

        DiagnosticLog.Info(HaText.Category, HaText.CallLog(domain, service, ids));
        try
        {
            return await client.CallServiceAsync(domain, service, body, returnResponse, Timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!returnResponse || !HaPolicy.ReadServices.Contains(domain + "." + service))
            {
                Invalidate();
            }
        }
    }

    /// <summary>
    /// The connection as the settings stand (the settings menu's <c>Home Assistant test connection</c>): <c>/api/config</c>'s
    /// version and place as one line, or the failure. Never throws but the caller's cancellation.
    /// </summary>
    public async Task<(bool Ok, string Text)> TestAsync(CancellationToken cancellationToken)
    {
        if (Client() is not { } client)
        {
            return (false, HaText.NotConfigured);
        }

        var config = await client.ConfigAsync(Timeout, cancellationToken).ConfigureAwait(false);
        return config.Ok ? (true, HaText.Server(client.BaseUrl, config.Body)) : (false, config.Error!);
    }

    /// <summary><see cref="TestAsync(CancellationToken)"/> over a session of its own, for a caller that has none.</summary>
    public static async Task<(bool Ok, string Text)> TestAsync(Func<AppSettingsData> effective, CancellationToken cancellationToken)
    {
        using var session = new HaSession(effective);
        return await session.TestAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
