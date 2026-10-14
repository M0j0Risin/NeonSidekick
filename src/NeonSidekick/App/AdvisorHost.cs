using NeonSidekick.Advisors;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

/// <summary>
/// The app's <see cref="IAdvisorHost"/> (2026-10-10, the profile advisors): an advisor's profile read off disk without loading it
/// (<see cref="Profiles.ReadProfileFile"/>, its <c>persona.md</c>), and its link made by the session the way a <c>/botchat</c> bot's is
/// (<see cref="LlmSession.LinkAsync(AppSettingsData, BotEmbeddedMode, CancellationToken)"/>) under <c>Advisor embedded mode</c>.
/// A link is kept for the advisor's later calls while the session's server, the advisor's <c>profile.json</c> and the mode are
/// as they were; otherwise, or after <see cref="Forget"/>, it is disposed and made again. Disposed with the screen (or headless).
/// An advisor on the Claude Code server runs its turns with the tools under its thread's conversation, on a <c>claude</c> process
/// of its own beside the chat's (2026-10-13, the user's ask; until then one advisor held the chat's process, and only while the
/// chat was elsewhere): disposing its link stops that process, so the drops dispose outside the lock and side by side.
/// </summary>
internal sealed class AdvisorHost : IAdvisorHost, IDisposable
{
    private readonly LlmSession _session;
    private readonly Func<string> _home;
    private readonly Func<AppSettingsData> _effective;
    private readonly object _gate = new();
    private readonly Dictionary<string, Kept> _links = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Kept(BotLink Link, LlmEndpoint? Main, DateTime Written, BotEmbeddedMode Mode);

    /// <param name="session">The session the links are made by (and whose server a blank URL borrows).</param>
    /// <param name="home">The home the profiles live under.</param>
    /// <param name="effective">The loaded profile's settings, for <c>Advisor embedded mode</c>.</param>
    public AdvisorHost(LlmSession session, Func<string> home, Func<AppSettingsData> effective)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _home = home ?? throw new ArgumentNullException(nameof(home));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public AdvisorProfile? Read(string name)
    {
        string home = _home();
        if (Profiles.Resolve(home, name) is not { } onDisk)
        {
            return null;
        }

        var settings = Profiles.ReadProfileFile(Profiles.ProfileFile(home, onDisk));
        return new AdvisorProfile(onDisk, settings, new PersonaFile(Profiles.Directory(home, onDisk)).Read());
    }

    public async Task<(AdvisorLink? Link, string? Problem)> LinkAsync(AdvisorProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        BotChatMultiEmbedded.TryParse(_effective().AdvisorEmbedded, out var mode);
        DateTime written = WrittenAt(profile.Name);
        var main = _session.Endpoint;
        Kept? stale = null;
        lock (_gate)
        {
            if (_links.TryGetValue(profile.Name, out var kept))
            {
                if (ReferenceEquals(kept.Main, main) && kept.Written == written && kept.Mode == mode)
                {
                    return (Wrap(kept.Link), null);
                }

                _links.Remove(profile.Name);
                stale = kept;
            }
        }

        stale?.Link.Dispose();
        var (link, problem) = await _session.LinkAsync(profile.Settings, mode, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return (null, problem);
        }

        Kept? raced;
        lock (_gate)
        {
            _links.Remove(profile.Name, out raced);
            _links[profile.Name] = new Kept(link, main, written, mode);
        }

        raced?.Link.Dispose();
        return (Wrap(link), null);
    }

    public void Forget(string profile)
    {
        Kept? kept;
        lock (_gate)
        {
            _links.Remove(profile, out kept);
        }

        kept?.Link.Dispose();
    }

    /// <summary>Every link dropped: a profile switch, or the end. The host stays usable; the next call links anew.</summary>
    public void Dispose() => Reset();

    /// <summary>
    /// Every link dropped (2026-10-10): the session forgotten — <c>/new</c>, <c>/clear</c>, a profile switch. Disposed outside the
    /// lock and side by side (2026-10-13): a Claude Code advisor's link stops its process, which may take up to
    /// <see cref="Claude.ClaudeServerHost.StopGrace"/>, so the wait is the slowest one's, not the sum. Each still stops before this
    /// returns, so a later link resuming the same session never meets the old process.
    /// </summary>
    public void Reset()
    {
        List<Kept> dropped;
        lock (_gate)
        {
            dropped = [.. _links.Values];
            _links.Clear();
        }

        if (dropped.Count == 1)
        {
            dropped[0].Link.Dispose();
        }
        else if (dropped.Count > 1)
        {
            Task.WhenAll(dropped.Select(kept => Task.Run(kept.Link.Dispose))).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// <paramref name="link"/> as an advisor's: on the Claude Code server (2026-10-10; every advisor there since 2026-10-13, the user's
    /// ask) its turns run with the tools under its thread's conversation, on its link's own process (<see cref="SidekickApp.ClaudeHostFor"/>).
    /// </summary>
    private AdvisorLink Wrap(BotLink link)
    {
        string? warning = link.SharedEmbedded is var (wanted, running) ? AdvisorText.SharedEmbeddedWarning(wanted, running) : null;
        return new AdvisorLink(history => _session.CreateAssistant(history, link), ModelOf(link.Endpoint), warning,
            Session: Claude.ClaudeCliEndpoint.IsClaudeCli(link.Endpoint.BaseUrl));
    }

    /// <summary>The model a link answers on as the footer names it: the embedded catalog's name, else the id.</summary>
    private string ModelOf(LlmEndpoint endpoint) =>
        EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl) && _session.Embedded is { } embedded && EmbeddedModelCatalog.Find(endpoint.ModelId, embedded.Catalog) is { } model
            ? model.Display
            : endpoint.ModelId;

    private DateTime WrittenAt(string profile)
    {
        try
        {
            return File.GetLastWriteTimeUtc(Profiles.ProfileFile(_home(), profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTime.MinValue;
        }
    }
}
