using System.Globalization;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

// ── The OpenAI tab of /settings: the OpenAI API (2026-10-03) ──────────────

internal sealed partial class SettingsMenu
{
    /// <summary>The OpenAI tab's strip title (2026-10-03, the user's ask: after Claude). Pinned.</summary>
    public const string OpenAITabTitle = "OpenAI";

    /// <summary>How the menu shows a 0 <see cref="AppSettingsData.OpenAIApiMaxTokens"/>: no cap sent, the model's own. Pinned.</summary>
    public const string OpenAIApiMaxTokensNoneLabel = "the model's own";

    /// <summary>How the menu shows an empty organization or project: no header sent. Pinned.</summary>
    public const string OpenAIApiHeaderNoneLabel = "(none)";

    /// <summary>The settings-menu wording for a bad <see cref="SettingsField.OpenAIApiMaxTokens"/>. Pinned.</summary>
    public static readonly string OpenAIApiMaxTokensRangeError =
        "must be 0 (the model's own) or " + AppSettingsData.MinOpenAIApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxOpenAIApiMaxTokens.ToString(CultureInfo.InvariantCulture) + " tokens";

    /// <summary>The warning when DPAPI could not encrypt the OpenAI API key and it was saved as typed. Pinned.</summary>
    public static string OpenAIApiKeyPlainWarning(string reason) => $"OpenAI API key saved unencrypted: {reason}.";
}
