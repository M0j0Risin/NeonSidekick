using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// One setting button on a tool group's on/off page (2026-10-09): the setting it opens, the word its title and the page's hint
/// name it by, and its key.
/// </summary>
internal readonly record struct SettingButton(SettingsField Target, string Word, char Key);

/// <summary>
/// The setting buttons on the File, Print, Screen capture, Obsidian, YouTube and Claude CLI advisor tool pages (2026-10-09, the
/// user's ask: actions at the top of each page to reach the group's own settings, the offered button's shape). Each opens its setting
/// as the tab's row does (its picker, its on/off page, the vault's folder picker or a typed value under a one-row page of its own),
/// and ESC there comes back to the on/off page, the buttons read again.
/// </summary>
internal sealed partial class SettingsMenu
{
    /// <summary>The glyph before every setting button's word, the policy picker's <c>≡ police</c> one. Pinned.</summary>
    public const string SettingButtonGlyph = "≡";

    /// <summary>
    /// The buttons a tool switch's page carries, in their order (2026-10-09); null for every other switch. The words are short so a
    /// page's four (YouTube's) fit the title row with their values. Pinned.
    /// </summary>
    public static IReadOnlyList<SettingButton>? SettingButtonsFor(SettingsField toolSwitch) => toolSwitch switch
    {
        SettingsField.FileTools => [new(SettingsField.FileBrowserMode, "browser", 'b'), new(SettingsField.ImageEditMode, "edit mode", 'e')],
        SettingsField.PrintTools => [new(SettingsField.PrintDefaultPrinter, "printer", 'p')],
        SettingsField.ScreenTools => [new(SettingsField.ScreenAsk, "ask", 'a'), new(SettingsField.ScreenPreview, "preview", 'p')],
        SettingsField.ObsidianTools => [new(SettingsField.ObsidianVault, "vault", 'v')],
        SettingsField.YouTubeTools =>
        [
            new(SettingsField.YouTubeSearchMaxResults, "max", 'm'), new(SettingsField.YouTubeVoice, "voice", 'v'),
            new(SettingsField.YouTubeSearchOrder, "order", 'o'), new(SettingsField.YouTubeSafeSearch, "safe", 's'),
        ],
        SettingsField.ClaudeCliAdvisor => [new(SettingsField.ClaudeCliAdvisorModel, "model", 'm'), new(SettingsField.ClaudeCliAdvisorEffort, "effort", 'e')],
        _ => null,
    };

    /// <summary>A setting button's title: <c>≡ browser (default)</c>. Pinned.</summary>
    public static string SettingButtonTitle(string word, string value) => SettingButtonGlyph + " " + word + " (" + value + ")";

    /// <summary>
    /// The value a setting button shows, short: the row's own value, except the vault's folder name alone (<c>none</c> while none
    /// is set), the result count's number, the advisor's blank model and effort as <c>as /claude</c>, and a printer left to the
    /// system's default without the row's parentheses. Pinned.
    /// </summary>
    public static string SettingButtonValue(SettingsField field, AppSettingsData data, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (field)
        {
            case SettingsField.ObsidianVault:
                string vault = data.ObsidianVault?.Trim().TrimEnd('\\', '/') ?? "";
                return vault.Length == 0 ? "none" : Path.GetFileName(vault) is { Length: > 0 } name ? name : vault;
            case SettingsField.YouTubeSearchMaxResults:
                return data.YouTubeSearchMaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case SettingsField.ClaudeCliAdvisorModel when string.IsNullOrWhiteSpace(data.ClaudeCliAdvisorModel):
            case SettingsField.ClaudeCliAdvisorEffort when string.IsNullOrWhiteSpace(data.ClaudeCliAdvisorEffort):
                return "as /claude";
            case SettingsField.PrintDefaultPrinter when string.IsNullOrWhiteSpace(data.PrintDefaultPrinter):
                return WindowsDefaultPrinterLabel.Trim('(', ')');
            default:
                return FieldValue(field, data, profileDirectory);
        }
    }

    /// <summary>
    /// The page's buttons over <paramref name="data"/>: <see cref="SettingButtonTitle"/> with <see cref="SettingButtonValue"/>, each lit
    /// while its setting is not the default (<see cref="IsChanged"/>, the accent the tab's row wears). Pinned.
    /// </summary>
    public static IReadOnlyList<MenuButton> SettingButtons(IReadOnlyList<SettingButton> buttons, AppSettingsData data, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        return buttons.Select(b => new MenuButton(SettingButtonTitle(b.Word, SettingButtonValue(b.Target, data, profileDirectory)), b.Key, IsChanged(b.Target, data, profileDirectory))).ToList();
    }

    /// <summary>The page's hint: <see cref="PickKeys"/> with each button's key and word, <c>Enter = choose · b = browser · e = edit mode · ESC = back</c>. Pinned.</summary>
    public static string SettingButtonKeys(IReadOnlyList<SettingButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        return "Enter = choose · " + string.Join(" · ", buttons.Select(b => b.Key + " = " + b.Word)) + " · ESC = back";
    }

    /// <summary>
    /// A setting button pressed: <paramref name="target"/> edited as its tab's row would be, over a one-row page of its own under the
    /// crumb, so a typed value's input sits under its own row rather than under the on/off rows. True when something was saved.
    /// </summary>
    private Task<bool> EditFromButtonAsync(SettingsField target, CancellationToken cancellationToken)
    {
        var saved = _settings.Current;
        var page = new MenuPage(Crumb(FieldName(target)), [LiveLabel(target, saved, LabelWidthOf([target]), _locateBrowser(""))], PickKeys);
        return EditAsync(target, saved, page, 0, cancellationToken);
    }
}
