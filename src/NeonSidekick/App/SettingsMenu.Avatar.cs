using NeonSidekick.Emojis;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The avatar picker: /settings › Client › Chat › Avatar and /avatar (2026-10-10) ──

internal sealed partial class SettingsMenu
{
    /// <summary>The cells of the picker's grid: an emoji's two cells and a blank either side.</summary>
    public const int AvatarCellWidth = 4;

    private enum AvatarCellKind
    {
        Emoji,
        Random,
        None,
    }

    // One cell of a tab: what it holds, and the catalog's entry for an emoji (null for an action, or an emoji the catalog lacks).
    private sealed record AvatarCell(AvatarCellKind Kind, string Emoji, EmojiEntry? Entry);

    // One tab as built: its strip title, rows (markup, "" for a gap), the cell behind each row (null for a heading or a gap),
    // the heading rows, the marked rows and a caption.
    private sealed record AvatarTab(string Title, List<string> Rows, List<AvatarCell?> Cells, HashSet<int> Headings, Dictionary<int, Style> Marks, string? Caption)
    {
        public int FirstEmoji => Math.Max(0, Cells.FindIndex(c => c is { Kind: AvatarCellKind.Emoji }));

        public int RowOf(string emoji) => Cells.FindIndex(c => c is { Kind: AvatarCellKind.Emoji } cell && cell.Emoji == emoji);
    }

    // What one visit of the picker reads once: the catalog, the prefs, the avatar, whose avatars are whose, the suggestions.
    private sealed record AvatarContext(EmojiCatalog Catalog, EmojiPrefs Prefs, string Current, string Profile, IReadOnlyDictionary<string, List<string>> UsedBy, IReadOnlyList<EmojiEntry> Suggested);

    /// <summary>
    /// The avatar picker (2026-10-10, the user's ask: "an intuitive picker … searching by name, a category filter, and any other way
    /// to narrow them down"): a grid of the catalog's emoji (<see cref="MenuGrid"/>) under a strip of tabs — For you (the profile's
    /// name and persona, <see cref="EmojiSuggest"/>), Recent (the avatars in use and <see cref="EmojiPrefs.Recent"/>) and one per
    /// Unicode group, each in its subgroups' sections — with 🎲 (a random emoji of the tab) and 🚫 (no avatar) leading every tab.
    /// Typing searches names and keywords across the whole catalog (<see cref="EmojiSearch"/>), an emoji typed or pasted being one;
    /// Enter on an emoji that takes a skin tone opens its six (<see cref="PickAvatarToneAsync"/>). The current avatar and the ones other
    /// profiles wear are on slabs, the band under the grid names the emoji, its keywords, whose it is and how it will read. Saves
    /// through <see cref="Apply"/>; true when it did. <paramref name="own"/> (<c>/avatar</c>): the pane is this picker's and closes as
    /// it ends; under <c>/settings</c> it stays for the list. Without the pane, a typed emoji or word (<see cref="TypeAvatarAsync"/>).
    /// </summary>
    public async Task<bool> PickAvatarAsync(string filter, bool own, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!_pane.Enabled)
        {
            return await TypeAvatarAsync(cancellationToken).ConfigureAwait(false);
        }

        string? chosen;
        try
        {
            chosen = await ChooseAvatarAsync(filter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // /avatar's own pane closes before the save, so its notice reaches the transcript as /model's does.
            if (own)
            {
                _pane.Close();
            }
        }

        if (chosen is null || chosen == AvatarRules.Normalize(_settings.Current.Avatar))
        {
            return Unchanged();
        }

        Apply(SettingsField.Avatar, d => d.Avatar = chosen);
        return true;
    }

    // The grid's visit: the emoji chosen ("" for 🚫), or null when ESC backed out. A pick goes on the Recent list here.
    private async Task<string?> ChooseAvatarAsync(string filter, CancellationToken cancellationToken)
    {
        var context = AvatarContextNow();
        var tabs = AvatarTabs(context);
        int tab = OpeningAvatarTab(tabs, context);
        int cursor = context.Current.Length > 0 && tabs[tab].RowOf(context.Current) is >= 0 and var at ? at : tabs[tab].FirstEmoji;
        filter = filter.Trim();
        int searchCursor = -1;
        while (true)
        {
            MenuPage page;
            AvatarTab shown;
            if (filter.Length > 0)
            {
                // A search: one page of results across the whole catalog, the strip gone until ESC clears it.
                shown = AvatarResults(context, filter);
                page = new MenuPage(Crumb(FieldName(SettingsField.Avatar)), shown.Rows, MenuFilter.Hint(EmojiText.Keys, filter))
                {
                    Grid = new MenuGrid(AvatarCellWidth),
                    Headings = shown.Headings,
                    Marks = shown.Marks,
                    Caption = shown.Caption,
                    Filter = filter,
                    Footer = (_, row) => AvatarFooter(context, shown, row),
                };
                cursor = searchCursor >= 0 ? searchCursor : shown.FirstEmoji;
            }
            else
            {
                var built = tabs;
                var menuTabs = built.Select(t => new MenuTab(t.Title, t.Rows) { Headings = t.Headings, Marks = t.Marks, Caption = t.Caption, Filter = "" }).ToList();
                page = MenuPage.Tabbed(Crumb(FieldName(SettingsField.Avatar)), menuTabs, tab, EmojiText.Keys) with
                {
                    Grid = new MenuGrid(AvatarCellWidth),
                    Footer = (t, row) => AvatarFooter(context, built[t], row),
                };
                shown = built[tab];
            }

            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            if (pick.Filter is { } typed)
            {
                // Typed, erased or cleared: the results again from their first emoji, or the tab it left.
                if (filter.Length == 0)
                {
                    tab = pick.Tab;
                    cursor = pick.Row;
                }

                filter = typed;
                searchCursor = -1;
                continue;
            }

            if (filter.Length == 0)
            {
                tab = pick.Tab;
                shown = tabs[tab];
            }

            if (pick.Row < 0 || pick.Row >= shown.Cells.Count || shown.Cells[pick.Row] is not { } cell)
            {
                continue;
            }

            switch (cell.Kind)
            {
                case AvatarCellKind.Random:
                    // A reroll: the cursor onto a random emoji of what is shown; Enter there picks it.
                    var emojiRows = Enumerable.Range(0, shown.Cells.Count).Where(r => shown.Cells[r] is { Kind: AvatarCellKind.Emoji }).ToList();
                    int landed = emojiRows.Count > 0 ? emojiRows[Random.Shared.Next(emojiRows.Count)] : pick.Row;
                    if (filter.Length > 0)
                    {
                        searchCursor = landed;
                    }
                    else
                    {
                        cursor = landed;
                    }

                    continue;
                case AvatarCellKind.None:
                    return "";
            }

            string chosen = cell.Emoji;
            int? tone = null;
            if (cell.Entry is { HasTones: true } toned)
            {
                if (await PickAvatarToneAsync(context, toned, cancellationToken).ConfigureAwait(false) is not { } choice)
                {
                    // ESC on the tones: back on the grid where it was.
                    if (filter.Length > 0)
                    {
                        searchCursor = pick.Row;
                    }
                    else
                    {
                        cursor = pick.Row;
                    }

                    continue;
                }

                tone = choice;
                chosen = toned.InTone(choice);
            }

            context.Prefs.Picked(chosen, tone);
            return chosen;
        }
    }

    /// <summary>
    /// <c>/avatar &lt;emoji&gt;</c> and <c>/avatar clear</c> (2026-10-10): <paramref name="avatar"/> saved as the avatar (empty for none;
    /// the caller has it from <see cref="AvatarCommand.Parse"/>, so it is one emoji), the notice the settings row's, the emoji first in
    /// the picker's Recent. True when it changed.
    /// </summary>
    public bool SetAvatar(string avatar)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        if (avatar == AvatarRules.Normalize(_settings.Current.Avatar))
        {
            return Unchanged();
        }

        if (avatar.Length > 0)
        {
            EmojiPrefs.Load(_settings.StorageDirectory).Picked(avatar);
        }

        Apply(SettingsField.Avatar, d => d.Avatar = avatar);
        return true;
    }

    /// <summary>
    /// The skin tones of <paramref name="entry"/> as a grid of six: as it is, then light to dark, the cursor on the tone last chosen
    /// (<see cref="EmojiPrefs.Tone"/>). The tone picked (0 for none), or null on ESC.
    /// </summary>
    private async Task<int?> PickAvatarToneAsync(AvatarContext context, EmojiEntry entry, CancellationToken cancellationToken)
    {
        var forms = Enumerable.Range(0, EmojiCatalog.ToneCount + 1).Select(entry.InTone).ToList();
        var marks = new Dictionary<int, Style>();
        for (int i = 0; i < forms.Count; i++)
        {
            if (forms[i] == context.Current)
            {
                marks[i] = Theme.MenuHighlightDim;
            }
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.Avatar)) + " › " + EmojiText.ToneTitle, forms.Select(Markup.Escape).ToList(), EmojiText.ToneKeys)
        {
            Grid = new MenuGrid(AvatarCellWidth),
            Marks = marks,
            Footer = (_, row) => row >= 0 && row < forms.Count
                ? new MenuFooter(string.Join('\n', EmojiText.NameLine(forms[row], entry) + " · " + EmojiText.ToneNames[row], EmojiText.CodeLine(forms[row], entry, forms[row] == context.Current, UsersOf(context, forms[row])), EmojiText.PreviewLine(forms[row], context.Profile))) { KeepLines = true }
                : null,
        };
        return await _pane.PickAsync(page, Math.Clamp(context.Prefs.Tone, 0, EmojiCatalog.ToneCount), cancellationToken).ConfigureAwait(false) is { } pick ? pick.Row : null;
    }

    /// <summary>
    /// The avatar typed where no pane can show the grid: an emoji is taken as it is, a word or two as the best of a search
    /// (<see cref="EmojiSearch.Rank"/>), an empty line as none. True when it saved.
    /// </summary>
    private async Task<bool> TypeAvatarAsync(CancellationToken cancellationToken)
    {
        string saved = AvatarRules.Normalize(_settings.Current.Avatar);
        var result = await EditTextAsync(SettingsField.Avatar, new MenuPage(Crumb(FieldName(SettingsField.Avatar)), [], EditKeys), 0, saved, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        if (result is not InputResult.Submitted submitted)
        {
            return Unchanged();
        }

        string text = submitted.Text.Trim();
        string? chosen = text.Length == 0 ? "" : EmojiSearch.AsEmoji(text) ?? EmojiSearch.Rank(text, EmojiCatalog.Shipped.Entries).FirstOrDefault()?.Emoji;
        if (chosen is null)
        {
            Sink.Error(EmojiText.NotAnEmoji(text));
            return false;
        }

        if (chosen == saved)
        {
            return Unchanged();
        }

        Apply(SettingsField.Avatar, d => d.Avatar = chosen);
        return true;
    }

    private AvatarContext AvatarContextNow()
    {
        string home = _settings.StorageDirectory;
        string profile = _settings.ProfileName;
        var usedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string other in Profiles.List(home))
        {
            if (string.Equals(other, profile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string avatar = AvatarRules.Normalize(Profiles.ReadProfileFile(Profiles.ProfileFile(home, other)).Avatar);
                if (avatar.Length > 0)
                {
                    (usedBy.TryGetValue(avatar, out var names) ? names : usedBy[avatar] = []).Add(other);
                }
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable profile has no avatar to mark; /profile says what is wrong with it.
            }
        }

        string? persona = null;
        try
        {
            persona = new PersonaFile(_settings.ProfileDirectory).Read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Suggestions from the name alone.
        }

        var catalog = EmojiCatalog.Shipped;
        return new AvatarContext(catalog, EmojiPrefs.Load(home), AvatarRules.Normalize(_settings.Current.Avatar), profile, usedBy,
            EmojiSuggest.For(profile, persona, catalog.Entries));
    }

    // The tabs: For you (when it has any), Recent, then a tab per group.
    private static List<AvatarTab> AvatarTabs(AvatarContext context)
    {
        var tabs = new List<AvatarTab>();
        if (context.Suggested.Count > 0)
        {
            var forYou = NewAvatarTab(EmojiText.ForYouTab, context);
            AddAvatarSection(forYou, context, EmojiText.ForYouHeading, context.Suggested.Select(e => (e.Emoji, (EmojiEntry?)e)));
            tabs.Add(forYou);
        }

        var recent = NewAvatarTab(EmojiText.RecentTab, context);
        var inUse = new List<string>();
        if (context.Current.Length > 0)
        {
            inUse.Add(context.Current);
        }

        inUse.AddRange(context.UsedBy.Keys.Where(e => !inUse.Contains(e)).OrderBy(e => context.Catalog.Find(e)?.Entry.Order ?? int.MaxValue));
        AddAvatarSection(recent, context, EmojiText.InUseHeading, inUse.Select(e => (e, context.Catalog.Find(e)?.Entry)));
        AddAvatarSection(recent, context, EmojiText.RecentHeading, context.Prefs.Recent.Select(e => (e, context.Catalog.Find(e)?.Entry)));
        tabs.Add(recent.Cells.Any(c => c is { Kind: AvatarCellKind.Emoji }) ? recent : recent with { Caption = EmojiText.NothingRecent });

        foreach (var group in context.Catalog.Groups)
        {
            var tab = NewAvatarTab(EmojiText.GroupTab(group.Name), context);
            foreach (string subgroup in group.Subgroups)
            {
                AddAvatarSection(tab, context, EmojiText.Heading(subgroup),
                    context.Catalog.Entries.Where(e => e.Group == group.Name && e.Subgroup == subgroup).Select(e => (EmojiFor(context, e), (EmojiEntry?)e)));
            }

            tabs.Add(tab);
        }

        return tabs;
    }

    // A search's page: the actions, then every emoji that matches, best first; an emoji typed or pasted leads.
    private static AvatarTab AvatarResults(AvatarContext context, string filter)
    {
        var tab = NewAvatarTab("", context);
        var found = new List<(string, EmojiEntry?)>();
        if (EmojiSearch.AsEmoji(filter) is { } typed)
        {
            found.Add((typed, context.Catalog.Find(typed)?.Entry));
        }

        var ranked = EmojiSearch.Rank(filter, context.Catalog.Entries);
        found.AddRange(ranked.Where(e => found.Count == 0 || found[0].Item2 != e).Select(e => (EmojiFor(context, e), (EmojiEntry?)e)));
        AddAvatarSection(tab, context, EmojiText.ResultsHeading, found);
        return tab with { Caption = found.Count > 0 ? EmojiText.Caption(filter, found.Count, context.Catalog.Entries.Count) : EmojiText.Caption(filter, 0, context.Catalog.Entries.Count) + " · " + EmojiText.NoMatch };
    }

    // An emoji as the grid shows it: in the tone last chosen when it takes one, so a toned avatar's family reads in its tone.
    private static string EmojiFor(AvatarContext context, EmojiEntry entry) => entry.InTone(context.Prefs.Tone);

    // A tab with its actions: 🎲 always, 🚫 while there is an avatar to clear.
    private static AvatarTab NewAvatarTab(string title, AvatarContext context)
    {
        var tab = new AvatarTab(title, [], [], [], [], null);
        tab.Headings.Add(0);
        tab.Rows.Add(SectionRule.Markup(EmojiText.ActionsHeading));
        tab.Cells.Add(null);
        tab.Rows.Add(Markup.Escape(EmojiText.RandomGlyph));
        tab.Cells.Add(new AvatarCell(AvatarCellKind.Random, EmojiText.RandomGlyph, null));
        if (context.Current.Length > 0)
        {
            tab.Rows.Add(Markup.Escape(EmojiText.NoneGlyph));
            tab.Cells.Add(new AvatarCell(AvatarCellKind.None, EmojiText.NoneGlyph, null));
        }

        return tab;
    }

    // A section: a gap, its heading with the count, its emoji; the avatar on the accent slab, another profile's on the quiet one.
    private static void AddAvatarSection(AvatarTab tab, AvatarContext context, string heading, IEnumerable<(string Emoji, EmojiEntry? Entry)> emoji)
    {
        var list = emoji.ToList();
        if (list.Count == 0)
        {
            return;
        }

        tab.Rows.Add("");
        tab.Cells.Add(null);
        tab.Headings.Add(tab.Rows.Count);
        tab.Rows.Add(SectionRule.Markup(heading, list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        tab.Cells.Add(null);
        foreach (var (shown, entry) in list)
        {
            if (shown == context.Current)
            {
                tab.Marks[tab.Rows.Count] = Theme.MenuHighlightDim;
            }
            else if (context.UsedBy.ContainsKey(shown))
            {
                tab.Marks[tab.Rows.Count] = Theme.ToolbarOff;
            }

            tab.Rows.Add(Markup.Escape(shown));
            tab.Cells.Add(new AvatarCell(AvatarCellKind.Emoji, shown, entry));
        }
    }

    // The tab the picker opens on: the current avatar's group, else For you, else the first group.
    private static int OpeningAvatarTab(List<AvatarTab> tabs, AvatarContext context)
    {
        if (context.Current.Length > 0 && context.Catalog.Find(context.Current) is { } found)
        {
            int group = tabs.FindIndex(t => t.Title == EmojiText.GroupTab(found.Entry.Group));
            if (group >= 0 && tabs[group].RowOf(context.Current) >= 0)
            {
                return group;
            }
        }

        if (context.Current.Length > 0 && tabs.FindIndex(t => t.Title == EmojiText.RecentTab) is >= 0 and var recent && tabs[recent].RowOf(context.Current) >= 0)
        {
            return recent;
        }

        return context.Suggested.Count > 0 ? 0 : tabs.FindIndex(t => t.Title == EmojiText.GroupTab(context.Catalog.Groups.FirstOrDefault()?.Name ?? ""))
            is >= 0 and var first ? first : 0;
    }

    // The band under the grid for the cell at row: an action's line, or the emoji's name, keywords, code, owners and preview.
    private static MenuFooter? AvatarFooter(AvatarContext context, AvatarTab tab, int row)
    {
        if (row < 0 || row >= tab.Cells.Count || tab.Cells[row] is not { } cell)
        {
            return null;
        }

        if (cell.Kind == AvatarCellKind.Random)
        {
            return new MenuFooter(EmojiText.RandomFooter);
        }

        if (cell.Kind == AvatarCellKind.None)
        {
            return new MenuFooter(EmojiText.NoneFooter);
        }

        var lines = new List<string>
        {
            EmojiText.NameLine(cell.Emoji, cell.Entry),
            EmojiText.KeywordLine(cell.Entry),
            EmojiText.CodeLine(cell.Emoji, cell.Entry, cell.Emoji == context.Current, UsersOf(context, cell.Emoji)),
            EmojiText.PreviewLine(cell.Emoji, context.Profile),
        };
        if (cell.Entry is { HasTones: true })
        {
            lines.Add(EmojiText.ToneLine);
        }

        return new MenuFooter(string.Join('\n', lines.Where(l => l.Length > 0))) { KeepLines = true };
    }

    private static IReadOnlyList<string> UsersOf(AvatarContext context, string emoji) =>
        context.UsedBy.TryGetValue(emoji, out var names) ? names : [];
}
