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
    /// Scan for local servers (the wording the user's, 2026-10-05): <c>Settings › Servers › Http › LLM server scan mode</c> first when it is off
    /// (2026-10-21, the user's pick: the scan mode is off by default, so a look under it found nothing; only when off since 2026-10-22, a review),
    /// then a look under what it says, past any URL saved (2026-10-22, a review: the saved, dead URL was tried again instead).
    /// </summary>
    Scan,

    /// <summary>Type the server's URL (<c>Settings › Servers › Common › LLM URL</c>).</summary>
    Url,

    /// <summary>
    /// Download or pick an embedded model (<c>Settings › Embedded › Models</c>), <c>Embedded servers enabled</c> switched on as the model is put
    /// to use (2026-10-21, the user's ask: off by default since; switched on for the visit and back off after until 2026-10-22, a review: a crash,
    /// a failed download or a download already running left it on).
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
            (ConnectChoice.Scan, ConnectText.ScanLabel, ConnectText.ScanNote),
            (ConnectChoice.Url, ConnectText.UrlLabel, ConnectText.UrlNote),
        };
        if (embedded)
        {
            rows.Add((ConnectChoice.Embedded, ConnectText.EmbeddedLabel, ConnectText.EmbeddedNote));
        }

        if (docker)
        {
            rows.Add((ConnectChoice.Docker, ConnectText.DockerLabel, ConnectText.DockerNote));
        }

        rows.Add((ConnectChoice.Anthropic, ConnectText.AnthropicLabel, ConnectText.ApiNote));
        rows.Add((ConnectChoice.OpenAI, ConnectText.OpenAILabel, ConnectText.ApiNote));
        rows.Add((ConnectChoice.ClaudeCli, ConnectText.ClaudeCliLabel, ConnectText.ClaudeCliNote));
        rows.Add((ConnectChoice.NotNow, ConnectText.NotNowLabel, ConnectText.NotNowNote));
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
    /// and after a bare <c>/server</c> with nothing offered; the scan row opens the scan mode first when it is off, and the embedded row lets
    /// the catalog put a model to use with <c>Embedded servers enabled</c> off, switching it on then (<see cref="EmbeddedRowAsync"/>). A profile
    /// switched during a row's visit ends the page: that switch connected the new profile and offered its own (one check for every row,
    /// <see cref="VisitAsync"/>, 2026-10-22, a review: each row had its own copy).
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
                    // nothing — asked only while it is off (2026-10-22, a review: a mode that already looked opened the setting every time).
                    // A visit that set a URL already reconnected; a mode that looks somewhere looks now, past a saved URL that is not
                    // answering (2026-10-22, a review: the connect tried that one again); off, the page again.
                    if (!Llm.LlmScanMode.Scans(Llm.LlmScanMode.Resolve(_effective())) && !await VisitAsync(SettingsField.LlmScanMode, profile, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (_session.Endpoint is null && Llm.LlmScanMode.Scans(Llm.LlmScanMode.Resolve(_effective())))
                    {
                        await ConnectLlmCoreAsync(cancellationToken, quiet: false, startup: false, scan: true).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.Url:
                    if (!await VisitAsync(SettingsField.LlmUrl, profile, cancellationToken).ConfigureAwait(false))
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
                    if (!await VisitAsync(SettingsField.DockerServerContainers, profile, cancellationToken).ConfigureAwait(false))
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
                    if (!await VisitAsync(SettingsField.AnthropicApiKey, profile, cancellationToken).ConfigureAwait(false))
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
                    if (!await VisitAsync(SettingsField.OpenAIApiKey, profile, cancellationToken).ConfigureAwait(false))
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
    /// A row's settings visit on <paramref name="field"/>: true to go on, false when it switched profiles and the page ends (<see cref="ProfileChanged"/>;
    /// 2026-10-22, a review: every row had the check after its visit, and a row added without it would act on the new profile).
    /// </summary>
    private async Task<bool> VisitAsync(SettingsField field, string profile, CancellationToken cancellationToken)
    {
        await OpenSettingsAsync(cancellationToken, field).ConfigureAwait(false);
        return !ProfileChanged(profile);
    }

    /// <summary>
    /// Whether the catalog may put an embedded model to use with <c>Embedded servers enabled</c> off: true only during the Connect page's embedded
    /// row (<see cref="EmbeddedRowAsync"/>), where <see cref="UseEmbeddedModelAsync"/> switches it on as the model is used.
    /// </summary>
    private bool _embeddedFromConnect;

    /// <summary>How many embedded downloads have started: the embedded row tells by it whether its visit started one.</summary>
    private long _embeddedDownloads;

    /// <summary>
    /// The embedded row (2026-10-21, the user's ask: <c>Embedded servers enabled</c> is off by default, and the catalog refuses a pick
    /// while it is): the catalog, its pick put to use with the switch off, which goes on as the model is used — an installed one at once, a
    /// download at its end (2026-10-22, the user's call after a review: switched on for the visit and back off after, a crash, a failed or
    /// cancelled download, or a download already running before the row left it on with no model). True when the page should end — a profile
    /// switched during the visit, or a download this visit started left running behind the line, whose end connects
    /// (<see cref="StartEmbeddedDownloadAsync"/>); the page would stand over it otherwise.
    /// </summary>
    private async Task<bool> EmbeddedRowAsync(string profile, CancellationToken cancellationToken)
    {
        long downloads = _embeddedDownloads;
        _embeddedFromConnect = true;
        try
        {
            await OpenSettingsAsync(cancellationToken, SettingsField.EmbeddedModels).ConfigureAwait(false);
        }
        finally
        {
            _embeddedFromConnect = false;
        }

        return ProfileChanged(profile) || (_embeddedDownloads != downloads && _jobs.Running(BackgroundJobKind.EmbeddedDownload));
    }
}
