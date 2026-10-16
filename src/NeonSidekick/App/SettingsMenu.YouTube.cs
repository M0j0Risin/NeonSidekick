using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

internal sealed partial class SettingsMenu
{
    /// <summary>One row of the YouTube-while-speaking picker: the word and what it does (padded to six: <c>pause</c> is five). Pinned.</summary>
    public static string YouTubeVoiceLabel(string name) =>
        Markup.Escape(name.PadRight(6)) + Theme.DimMarkup(YouTube.YouTubeVoiceMode.Describe(name));

    /// <summary>
    /// <c>/youtube &lt;words&gt;</c>'s picker (2026-10-05, the YouTube plan): a search's hits as rows (<c>YouTubeText.PickRows</c>, escaped
    /// here), Enter plays the highlighted one, ESC none; the <c>YouTubeText.SaveButton</c> on the title row (a click, or
    /// <c>YouTubeText.SaveKey</c>, 2026-10-07) saves it instead. The row and whether it was the save, or null; without the pane (the
    /// Spectre prompt) there is no button.
    /// </summary>
    public async Task<(int Row, bool Save)?> PickVideoAsync(string title, IReadOnlyList<string> rows, int cursor, CancellationToken cancellationToken, IReadOnlyList<MenuFooter>? footers = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        // Each hit whole under the list (2026-10-07): its title, channel, length, views, year and id.
        var page = new MenuPage(title, rows.Select(Markup.Escape).ToList(), YouTube.YouTubeText.PickKeys)
        {
            Footer = footers is null ? null : (_, row) => row < footers.Count ? footers[row] : null,
        };
        if (!_pane.Enabled)
        {
            return await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { } row ? (row, false) : null;
        }

        var buttons = new[] { new MenuButton(YouTube.YouTubeText.SaveButton, YouTube.YouTubeText.SaveKey) };
        return await _pane.PickAsync(page with { Buttons = buttons }, cursor, cancellationToken).ConfigureAwait(false) is { } picked
            ? (picked.Row, picked.Button >= 0)
            : null;
    }

    /// <summary>What each search order does, beside its word on the picker (2026-10-08). Pinned.</summary>
    public static string YouTubeOrderNote(string order) => order switch
    {
        "date" => "newest first",
        "views" => "most watched first",
        "rating" => "best liked first",
        "title" => "A to Z by title",
        _ => "YouTube's own ranking",
    };

    /// <summary>What each safe-search level does, beside its word on the picker (2026-10-08). Pinned.</summary>
    public static string YouTubeSafeSearchNote(string level) => level switch
    {
        "none" => "nothing left out",
        "strict" => "every restricted video left out",
        _ => "YouTube's default: the most restricted left out",
    };

    /// <summary>One row of the search order or safe search picker: the word padded to nine (<c>relevance</c>) and its note. Pinned.</summary>
    public static string YouTubeFilterLabel(string word, string note) => Markup.Escape(word.PadRight(10)) + Theme.DimMarkup(note);

    /// <summary>
    /// The <c>YouTube search order</c> and <c>YouTube safe search</c> pickers (2026-10-08): one <see cref="YouTubeFilterLabel"/> row
    /// per word, the saved one (as <c>YouTubeSearchFilters.Defaults</c> reads it) under the cursor.
    /// </summary>
    private async Task<bool> PickYouTubeFilterAsync(SettingsField field, Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        bool order = field == SettingsField.YouTubeSearchOrder;
        var words = order ? YouTube.YouTubeSearchFilters.Orders : YouTube.YouTubeSearchFilters.SafeSearches;
        var current = YouTube.YouTubeSearchFilters.Defaults(saved);
        var rows = words.Select(w => YouTubeFilterLabel(w, order ? YouTubeOrderNote(w) : YouTubeSafeSearchNote(w))).ToList();
        int cursor = Math.Max(0, words.ToList().IndexOf(order ? current.Order : current.SafeSearch));
        int? picked = await PickAsync(new MenuPage(Crumb(FieldName(field)), rows, PickKeys), cursor, cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string word = words[index];
        Apply(field, d => { if (order) d.YouTubeSearchOrder = word; else d.YouTubeSafeSearch = word; });
        return true;
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

    /// <summary>One row of the YouTube open links in picker: the word padded to sixteen (as <see cref="WebOpenLinksInLabel"/>) and what it does. Pinned.</summary>
    public static string YouTubeOpenLinksInLabel(string name) =>
        Markup.Escape(name.PadRight(16)) + Theme.DimMarkup(YouTube.YouTubeOpenLinksIn.Describe(name));

    /// <summary>The <c>YouTube open links in</c> picker (2026-10-16): one <see cref="YouTubeOpenLinksInLabel"/> row per word, the saved one (as it reads) under the cursor.</summary>
    private async Task<bool> PickYouTubeOpenLinksInAsync(Settings.AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = YouTube.YouTubeOpenLinksIn.Names;
        string current = YouTube.YouTubeOpenLinksIn.TryParse(saved.YouTubeOpenLinksIn, out var kind) ? YouTube.YouTubeOpenLinksIn.Name(kind) : YouTube.YouTubeOpenLinksIn.Default;
        var page = new MenuPage(Crumb(FieldName(SettingsField.YouTubeOpenLinksIn)), names.Select(YouTubeOpenLinksInLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Array.IndexOf(names, current), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.YouTubeOpenLinksIn, d => d.YouTubeOpenLinksIn = name);
        return true;
    }
}
