using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The Claude tab of /settings: the Claude API and the Claude CLI server (2026-10-03) ──────────────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The Claude tab's strip title (2026-10-03, the user's ask: the Claude API's four rows and the Claude CLI server's switch
    /// off <c>/tools</c>' Claude tab, which keeps <c>/claude</c>'s and the advisor's; after Docker, the user's place). Pinned.
    /// </summary>
    public const string ClaudeTabTitle = "Claude";

    /// <summary>
    /// <c>Claude API key</c> and <c>OpenAI API key</c> (2026-10-03, <see cref="SetHomeAssistantTokenAsync"/>'s shape; the Claude key
    /// was typed in the clear until then): a masked slot on the pane, the key never put back on the line — typing replaces it,
    /// empty clears it — then encrypted for this Windows user before it reaches the file (<see cref="SettingsSecrets.Protect"/>);
    /// where DPAPI fails, kept as typed and said so. A reconnect, as before.
    /// </summary>
    private async Task<bool> SetApiKeyAsync(SettingsField field, MenuPage page, int row, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Hint = EditKeys }, row, _input, "", allowEmpty: true, cancellationToken, mask: true).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(FieldName(field), EditKeys));
            result = await _input.ReadAsync("", remember: false, allowEmpty: true, cancellationToken: cancellationToken, escapeCancels: true, mask: true).ConfigureAwait(false);
        }

        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        string stored = SettingsSecrets.Protect(submitted.Text.Trim(), out string? protectError);
        if (field == SettingsField.OpenAIApiKey)
        {
            if (protectError is not null)
            {
                Sink.Warning(OpenAIApiKeyPlainWarning(protectError));
            }

            Apply(field, d => d.OpenAIApiKey = stored);
        }
        else
        {
            if (protectError is not null)
            {
                Sink.Warning(ClaudeApiKeyPlainWarning(protectError));
            }

            Apply(field, d => d.ClaudeApiKey = stored);
        }

        return true;
    }
}
