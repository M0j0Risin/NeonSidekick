using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

internal sealed partial class SettingsMenu
{
    /// <summary>One row of the YouTube-while-speaking picker: the word and what it does (padded to six: <c>pause</c> is five). Pinned.</summary>
    public static string YouTubeVoiceLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(YouTube.YouTubeVoiceMode.Describe(name));

    /// <summary>
    /// <c>/youtube &lt;words&gt;</c>'s picker (2026-10-05, the YouTube plan): a search's hits as rows (<c>YouTubeText.PickRow</c>, escaped
    /// here), Enter plays the highlighted one, ESC none. The row's index, or null.
    /// </summary>
    public Task<int?> PickVideoAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var page = new MenuPage(title, rows.Select(Markup.Escape).ToList(), PickKeys);
        return PickAsync(page, 0, cancellationToken);
    }

    /// <summary>The <c>YouTube while speaking</c> picker (2026-10-05): one <see cref="YouTubeVoiceLabel"/> row per word, the saved one under the cursor.</summary>
    private async Task<bool> PickYouTubeVoiceAsync(Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = YouTube.YouTubeVoiceMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.YouTubeVoice)), names.Select(YouTubeVoiceLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(names, YouTube.YouTubeVoiceMode.Resolve(saved.YouTubeVoice)), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.YouTubeVoice, d => d.YouTubeVoice = name);
        return true;
    }
}
