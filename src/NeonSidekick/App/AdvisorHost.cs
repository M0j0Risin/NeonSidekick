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
        lock (_gate)
        {
            if (_links.TryGetValue(profile.Name, out var kept))
            {
                if (ReferenceEquals(kept.Main, main) && kept.Written == written && kept.Mode == mode)
                {
                    return (Wrap(kept.Link), null);
                }

                _links.Remove(profile.Name);
                kept.Link.Dispose();
            }
        }

        var (link, problem) = await _session.LinkAsync(profile.Settings, mode, cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            return (null, problem);
        }

        lock (_gate)
        {
            if (_links.Remove(profile.Name, out var raced))
            {
                raced.Link.Dispose();
            }

            _links[profile.Name] = new Kept(link, main, written, mode);
        }

        return (Wrap(link), null);
    }

    public void Forget(string profile)
    {
        lock (_gate)
        {
            if (_links.Remove(profile, out var kept))
            {
                kept.Link.Dispose();
            }
        }
    }

    /// <summary>Every link dropped: a profile switch, or the end. The host stays usable; the next call links anew.</summary>
    public void Dispose() => Reset();

    /// <summary>Every link dropped (2026-10-10): the session forgotten — <c>/new</c>, <c>/clear</c>, a profile switch.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var kept in _links.Values)
            {
                kept.Link.Dispose();
            }

            _links.Clear();
        }
    }

    private AdvisorLink Wrap(BotLink link)
    {
        string? warning = link.SharedEmbedded is var (wanted, running) ? AdvisorText.SharedEmbeddedWarning(wanted, running) : null;
        return new AdvisorLink(history => _session.CreateAssistant(history, link), ModelOf(link.Endpoint), warning);
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
