using NeonSidekick.Docker;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>What the <c>Connect a model</c> page offers (2026-10-04, <see cref="ChatScreen.ConnectRows"/>).</summary>
public enum ConnectChoice
{
    /// <summary>Scan for local servers again, as the start did (the wording the user's, 2026-10-05).</summary>
    Scan,

    /// <summary>Type the server's URL (<c>Settings › LLM › URL</c>).</summary>
    Url,

    /// <summary>Download or pick an embedded model (<c>Settings › Embedded › Models</c>).</summary>
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

    /// <summary>Claude Code as the server: the Claude CLI server switched on.</summary>
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
    public const string ConnectKeys = "Enter = choose · ESC = not now";

    /// <summary>
    /// The page's rows for this screen: every way to a model that is offered here — a scan, a URL, the embedded catalog (where the
    /// embedded model can run), a Docker container (2026-10-05), the two APIs, Claude Code — each with what it does, then <c>Not now</c>. Pinned.
    /// </summary>
    public static IReadOnlyList<(ConnectChoice Choice, string Label, string Note)> ConnectRows(bool embedded)
    {
        var rows = new List<(ConnectChoice, string, string)>
        {
            (ConnectChoice.Scan, "Scan for local servers", "LM Studio, Ollama, llama.cpp, vLLM… on this machine or the network"),
            (ConnectChoice.Url, "Enter a server's URL", "any OpenAI-compatible server"),
        };
        if (embedded)
        {
            rows.Add((ConnectChoice.Embedded, "Download an embedded model", "selected GGUF models from Hugging Face"));
        }

        rows.Add((ConnectChoice.Docker, "Connect Docker container", "a vLLM or SGLang container in Docker Desktop"));

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
    private async Task ConnectPageAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled || !_menu.CanShowMenus())
        {
            return;
        }

        while (_session.Endpoint is null)
        {
            var rows = ConnectRows(EmbeddedAvailable);
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
                    await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    break;

                case ConnectChoice.Url:
                    await OpenSettingsAsync(cancellationToken, SettingsField.LlmUrl).ConfigureAwait(false);
                    break;

                case ConnectChoice.Embedded:
                    await OpenSettingsAsync(cancellationToken, SettingsField.EmbeddedModels).ConfigureAwait(false);
                    break;

                case ConnectChoice.Docker:
                    // The Docker tab's checklist; the containers chosen count only with the switch on, and /server starts one.
                    await OpenSettingsAsync(cancellationToken, SettingsField.DockerServerContainers).ConfigureAwait(false);
                    if (_settings.Current.DockerServerContainers is { Count: > 0 })
                    {
                        if (!_settings.Current.DockerServers)
                        {
                            _settings.Update(d => d.DockerServers = true);
                        }

                        _transcript.Notice(DockerServerText.ConnectHint);
                    }

                    break;

                case ConnectChoice.Anthropic:
                    await OpenSettingsAsync(cancellationToken, SettingsField.AnthropicApiKey).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(_settings.Current.AnthropicApiKey))
                    {
                        _settings.Update(d => d.AnthropicApi = true);
                        await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.OpenAI:
                    await OpenSettingsAsync(cancellationToken, SettingsField.OpenAIApiKey).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(_settings.Current.OpenAIApiKey))
                    {
                        _settings.Update(d => d.OpenAIApi = true);
                        await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    }

                    break;

                case ConnectChoice.ClaudeCli:
                    _settings.Update(d => d.ClaudeCliServer = true);
                    await ConnectLlmAsync(cancellationToken, quiet: false, startup: false).ConfigureAwait(false);
                    break;
            }
        }
    }
}
