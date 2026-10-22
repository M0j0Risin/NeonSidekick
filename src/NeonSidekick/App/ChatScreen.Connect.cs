using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the <c>Connect a model</c> page offers (2026-10-04, <see cref="ChatScreen.ConnectRows"/>).</summary>
public enum ConnectChoice
{
    /// <summary>
    /// Scan for local servers (the wording the user's, 2026-10-05): <c>Settings › Servers › Http › LLM server scan mode</c> first, then a
    /// look under what it says (2026-10-21, the user's pick: the scan mode is off by default, so a look under it found nothing).
    /// </summary>
    Scan,

    /// <summary>Type the server's URL (<c>Settings › Servers › Common › LLM URL</c>).</summary>
    Url,

    /// <summary>
    /// Download or pick an embedded model (<c>Settings › Embedded › Models</c>), <c>Embedded servers enabled</c> switched on for it
    /// (2026-10-21, the user's ask: off by default since, and switched back off when the visit picked nothing).
    /// </summary>
    Embedded,

    /// <summary>
    /// Choose Docker containers as servers (<c>Settings › Docker › Docker server containers</c>, 2026-10-05, the user's ask), and
    /// Docker servers switched on when any is chosen.
    /// </summary>
    Docker,

    /// <summary>The Anthropic API: its key, then the API switched on.</summary>
    Anthropic,

    /// <summary>The OpenAI API: its key, then the API switched on.</summary>
    OpenAI,

    /// <summary>Claude Code as the server: the Claude Code server switched on.</summary>
    ClaudeCli,

    /// <summary>Leave it: the app runs without a model until <c>/server</c>.</summary>
    NotNow,
}

// ── The first run with no server: "Connect a model" (2026-10-04, the UI review) ──

internal sealed partial class ChatScreen
{
    /// <summary>The page's title. Pinned.</summary>
    public const string ConnectTitle = "🖥️ Connect a model";

    /// <summary>The page's hint row. Pinned.</summary>
    public const string ConnectKeys = "Enter = choose · Esc = not now";

    /// <summary>
    /// The page's rows for this screen: every way to a model that is offered here — a scan, a URL, the embedded catalog (where the
    /// embedded model can run), a Docker container (2026-10-05; only where Docker servers can run since 2026-10-14, the user's ask:
    /// a Mac offered it and led to a checklist whose containers could never serve), the two APIs, Claude Code — each with what it
    /// does, then <c>Not now</c>. Pinned.
    /// </summary>
    public static IReadOnlyList<(ConnectChoice Choice, string Label, string Note)> ConnectRows(bool embedded, bool docker)
    {
        var rows = new List<(ConnectChoice, string, string)>
        {
            (ConnectChoice.Scan, "Scan for local servers", "LM Studio, Ollama, llama.cpp, vLLM… choose where to look"),
            (ConnectChoice.Url, "Enter a server's URL", "any OpenAI-compatible server"),
        };
        if (embedded)
        {
            rows.Add((ConnectChoice.Embedded, "Download an embedded model", "selected GGUF models from Hugging Face"));
        }

        if (docker)
        {
            rows.Add((ConnectChoice.Docker, "Connect Docker container", "a vLLM or SGLang container in Docker Desktop"));
        }

        rows.Add((ConnectChoice.Anthropic, "Use the Anthropic API", "your API key, billed per message"));
        rows.Add((ConnectChoice.OpenAI, "Use the OpenAI API", "your API key, billed per message"));
        rows.Add((ConnectChoice.ClaudeCli, "Use Claude Code", "the claude CLI on this machine, with your Claude subscription"));
        rows.Add((ConnectChoice.NotNow, "Not now", "/server connects later"));
        return rows;
    }

    /// <summary>One row of the page: the label padded to the longest, the note dim. Pure.</summary>
    public static string ConnectRow(string label, string note, int width) =>
        Markup.Escape(label.PadRight(width)) + Theme.DimMarkup(note);

    /// <summary>
    /// The page that stands where no server answered (2026-10-04, the UI review: a red line, or an unexplained jump to the embedded
    /// catalog): each way to a model opens the editor it needs, then the app connects again; the page comes back until a model is
    /// connected or the user says not now (ESC too). On the pane only: elsewhere the no-server line is all.
    /// </summary>
    /// <remarks>
    /// Since 2026-10-21 (every server off by default, the user's call) it also stands after a profile switch with nothing to connect to
    /// and after a bare <c>/server</c> with nothing offered; the scan row opens the scan mode first, and the embedded row switches
    /// <c>Embedded servers enabled</c> on for its visit (<see cref="EmbeddedRowAsync"/>). A profile switched during a row's visit ends
    /// the page: that switch connected the new profile and offered its own.
    /// </remarks>
    private async Task ConnectPageAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled || !_menu.CanShowMenus())
        {
            return;
        }

        string profile = _settings.ProfileName;
        while (_session.Endpoint is null)
        {
            var rows = ConnectRows(EmbeddedAvailable, DockerEndpoint.Here);
            int width = rows.Max(r => r.Label.Length) + 2;
            var page = new MenuPage(ConnectTitle, rows.Select(r => ConnectRow(r.Label, r.Note, width)).ToList(), ConnectKeys);
            MenuPick? pick;
            try
            {
                pick = await _menuPane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _menuPane.Close();
            }

            if (pick is not { } picked || rows[picked.Row].Choice == ConnectChoice.NotNow)
            {
                return;
            }

            switch (rows[picked.Row].Choice)
            {
                case ConnectChoice.Scan:
                    // Where to look first (2026-10-21, the user's pick): the scan mode is off by default, so a look under it found
                    // nothing. A visit that set a URL already reconnected; a mode that looks somewhere looks now; off, the page again.
                    await OpenSettingsAsync(cancellationToken, SettingsField.LlmScanMode).ConfigureAwait(false);
                    if (ProfileChanged(profile))
                    {
                        return;
                    }

                    if (_session.Endpoint is null && Llm.LlmScanMode.Scans(Llm.LlmScanMode.Resolve(_effective())))
                    {
                        await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.Url:
                    await OpenSettingsAsync(cancellationToken, SettingsField.LlmUrl).ConfigureAwait(false);
                    if (ProfileChanged(profile))
                    {
                        return;
                    }

                    break;

                case ConnectChoice.Embedded:
                    if (await EmbeddedRowAsync(profile, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case ConnectChoice.Docker:
                    // The Docker tab's checklist; the containers chosen count only with the switch on, and /server starts one.
                    // With some chosen the page is left (2026-10-06, the code review's catch): no connect was tried, so the loop
                    // would show the page again over the hint, and /server, which the hint names, is typed on the input row.
                    await OpenSettingsAsync(cancellationToken, SettingsField.DockerServerContainers).ConfigureAwait(false);
                    if (ProfileChanged(profile))
                    {
                        return;
                    }

                    if (_settings.Current.DockerServerContainers is { Count: > 0 })
                    {
                        if (!_settings.Current.DockerServers)
                        {
                            _settings.Update(d => d.DockerServers = true);
                        }

                        _transcript.Notice(DockerServerText.ConnectHint);
                        return;
                    }

                    break;

                case ConnectChoice.Anthropic:
                    await OpenSettingsAsync(cancellationToken, SettingsField.AnthropicApiKey).ConfigureAwait(false);
                    if (ProfileChanged(profile))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(_settings.Current.AnthropicApiKey))
                    {
                        _settings.Update(d => d.AnthropicApi = true);
                        await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.OpenAI:
                    await OpenSettingsAsync(cancellationToken, SettingsField.OpenAIApiKey).ConfigureAwait(false);
                    if (ProfileChanged(profile))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(_settings.Current.OpenAIApiKey))
                    {
                        _settings.Update(d => d.OpenAIApi = true);
                        await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.ClaudeCli:
                    _settings.Update(d => d.ClaudeCodeServer = true);
                    await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Whether the profile loaded is no longer <paramref name="profile"/>, the one the page opened on (2026-10-21): a row's settings visit
    /// switched profiles, which connected the new one and offered its own page, so this page ends with nothing more done.
    /// </summary>
    private bool ProfileChanged(string profile) => !string.Equals(_settings.ProfileName, profile, StringComparison.Ordinal);

    /// <summary>
    /// The embedded row (2026-10-21, the user's ask: <c>Embedded servers enabled</c> is off by default, and the catalog refuses a pick
    /// while it is): the switch on, then the catalog. True when the page should end — a profile switched during the visit, or a
    /// download left running behind the line, whose end connects (<see cref="StartEmbeddedDownloadAsync"/>); the page would stand
    /// over it otherwise. The switch goes back off when the visit changed nothing — it was off, the same profile is loaded, no
    /// download runs (a switch off cancels one) and no embedded model is saved — in a <c>finally</c>, so an exit mid-visit
    /// leaves it as it was too.
    /// </summary>
    private async Task<bool> EmbeddedRowAsync(string profile, CancellationToken cancellationToken)
    {
        bool wasOn = _settings.Current.EmbeddedLlmServer;
        if (!wasOn)
        {
            // Before the visit, so the pane counts no change and fires no reconnect; a catalog pick then passes UseEmbeddedModelAsync.
            _settings.Update(d => d.EmbeddedLlmServer = true);
        }

        try
        {
            await OpenSettingsAsync(cancellationToken, SettingsField.EmbeddedModels).ConfigureAwait(false);
            return ProfileChanged(profile) || _jobs.Running(BackgroundJobKind.EmbeddedDownload);
        }
        finally
        {
            if (!wasOn && !_jobs.Running(BackgroundJobKind.EmbeddedDownload))
            {
                if (!ProfileChanged(profile))
                {
                    if (!EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(_settings.Current.LlmUrl) && _settings.Current.EmbeddedLlmServer)
                    {
                        _settings.Update(d => d.EmbeddedLlmServer = false);
                    }
                }
                else
                {
                    EmbeddedSwitchBackOff(profile);
                }
            }
        }
    }

    /// <summary>
    /// The embedded switch put back off in <paramref name="profile"/>'s saved file (2026-10-21, a review): a visit that switched profiles left the
    /// page's profile, saved by the switch with the switch on though the visit picked nothing there. Disk only, as a reset of another profile is;
    /// left as it is when that profile now names an embedded model, and a file that can't be read or written is only logged.
    /// </summary>
    private void EmbeddedSwitchBackOff(string profile)
    {
        string path = Profiles.ProfileFile(_settings.StorageDirectory, profile);
        try
        {
            var data = Profiles.ReadProfileFile(path);
            if (data.EmbeddedLlmServer && !EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(data.LlmUrl))
            {
                data.EmbeddedLlmServer = false;
                Profiles.WriteProfileFile(path, data);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            DiagnosticLog.Warn(AppCategory, $"Connect a model: the embedded switch stayed on in profile \"{profile}\" ({ex.Message}).");
        }
    }
}
