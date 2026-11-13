using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>The Video menu's own picker (2026-11-12, the video-files plan): here rather than beside YouTube's, since a file isn't YouTube.</summary>
internal sealed partial class SettingsMenu
{
    /// <summary>One row of the Video files play in picker: the word padded to sixteen (as <see cref="YouTubeOpenLinksInLabel"/>) and what it does. Pinned.</summary>
    public static string VideoFilesPlayInLabel(string name) =>
        Markup.Escape(name.PadRight(16)) + Theme.DimMarkup(Viewer.VideoFilesPlayIn.Describe(name));

    /// <summary>The <c>Video files play in</c> picker: one <see cref="VideoFilesPlayInLabel"/> row per word, the saved one (as it reads) under the cursor.</summary>
    private async Task<bool> PickVideoFilesPlayInAsync(Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = Viewer.VideoFilesPlayIn.Names;
        string current = Viewer.VideoFilesPlayIn.TryParse(saved.VideoFilesPlayIn, out var kind) ? Viewer.VideoFilesPlayIn.Name(kind) : Viewer.VideoFilesPlayIn.Default;
        var page = new MenuPage(Crumb(FieldName(SettingsField.VideoFilesPlayIn)), names.Select(VideoFilesPlayInLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(names, current), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.VideoFilesPlayIn, d => d.VideoFilesPlayIn = name);
        return true;
    }
}
