using NeonSidekick.Emojis;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The avatar picker: /settings › Client › Chat › Model avatar and /avatar (2026-10-10), User avatar (2026-10-12) ──

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
    // ForDraft (2026-10-11): the draft line's emoji picker, not the avatar's — no avatar, owners, suggestions or preview. HomeDefault
    // (later on 2026-10-12): a home bar item's emoji, Current its own one, 🚫 back to this glyph, no preview and no avatar words.
    // ForUser (2026-10-12): the user avatar's, Current the user's own, no owners or suggestions, 🚫 back to ›, a sent line's preview.
    private sealed record AvatarContext(EmojiCatalog Catalog, EmojiPrefs Prefs, string Current, string Profile, IReadOnlyDictionary<string, List<string>> UsedBy, IReadOnlyList<EmojiEntry> Suggested, bool ForDraft = false, string? HomeDefault = null, bool ForUser = false);

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
            return await TypeAvatarAsync(SettingsField.ModelAvatar, AvatarRules.Normalize(_settings.Current.ModelAvatar), (d, avatar) => d.ModelAvatar = avatar, cancellationToken).ConfigureAwait(false);
        }

        string? chosen;
        try
        {
            chosen = await ChooseEmojiAsync(AvatarContextNow(), Crumb(FieldName(SettingsField.ModelAvatar)), filter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // /avatar's own pane closes before the save, so its notice reaches the transcript as /model's does.
            if (own)
            {
                _pane.Close();
            }
        }

        if (chosen is null || chosen == AvatarRules.Normalize(_settings.Current.ModelAvatar))
        {
            return Unchanged();
        }

        Apply(SettingsField.ModelAvatar, d => d.ModelAvatar = chosen);
        return true;
    }

    /// <summary>
    /// The user avatar's picker (2026-10-12, the user's ask: <see cref="SettingsField.UserAvatar"/>, the user's own emoji opening their
    /// lines in place of <c>›</c>): the model avatar's grid, tabs, search and skin tones without what is a profile's — no For you, no
    /// other profiles' slabs — its 🚫 back to <c>›</c> (stored as null, the default), its preview a sent line. Without the pane, typed
    /// (<see cref="TypeAvatarAsync"/>). True when it saved. <c>/avatar user [words]</c> (2026-10-12, the user's ask) opens it as
    /// <c>/avatar</c> opens the model's: <paramref name="filter"/> the search, <paramref name="own"/> the pane closed as it ends.
    /// </summary>
    public async Task<bool> PickUserAvatarAsync(string filter, bool own, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string saved = AvatarRules.Normalize(_settings.Current.UserAvatar);
        if (!_pane.Enabled)
        {
            return await TypeAvatarAsync(SettingsField.UserAvatar, saved, SetUser, cancellationToken).ConfigureAwait(false);
        }

        var context = new AvatarContext(EmojiCatalog.Shipped, EmojiPrefs.Load(_settings.StorageDirectory), saved, _settings.ProfileName,
            new Dictionary<string, List<string>>(StringComparer.Ordinal), [], ForUser: true);
        string? chosen;
        try
        {
            chosen = await ChooseEmojiAsync(context, Crumb(FieldName(SettingsField.UserAvatar)), filter, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (own)
            {
                _pane.Close();
            }
        }

        if (chosen is null || chosen == saved)
        {
            return Unchanged();
        }

        Apply(SettingsField.UserAvatar, d => SetUser(d, chosen));
        return true;
    }

    /// <summary>The user avatar saved: empty for none (null until 2026-10-13, when null became the 🙊 default).</summary>
    private static void SetUser(AppSettingsData d, string avatar) => d.UserAvatar = avatar;

    /// <summary>
    /// The draft line's emoji picker (2026-10-11, the user's ask: the <c>Emoji picker</c> setting's button at the draft row's end): the
    /// avatar picker's grid, tabs, search and skin tones under <see cref="EmojiText.PickerTitle"/>, without what is the avatar's — no 🚫,
    /// no avatars in use or For you, no preview — opening on Recent when it has any (the avatar's Recent, one list in
    /// <see cref="EmojiPrefs"/>). The emoji picked, or null on ESC (and without the pane, where no button is drawn). The caller closes
    /// the pane; nothing is saved but the Recent list.
    /// </summary>
    public async Task<string?> PickEmojiAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var context = new AvatarContext(EmojiCatalog.Shipped, EmojiPrefs.Load(_settings.StorageDirectory), "", _settings.ProfileName,
            new Dictionary<string, List<string>>(StringComparer.Ordinal), [], ForDraft: true);
        string? chosen = await ChooseEmojiAsync(context, EmojiText.PickerTitle, "", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(chosen) ? null : chosen;
    }

    // The grid's visit under title: the emoji chosen ("" for 🚫), or null when ESC backed out. A pick goes on the Recent list here.
    private async Task<string?> ChooseEmojiAsync(AvatarContext context, string title, string filter, CancellationToken cancellationToken)
    {
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
                page = new MenuPage(title, shown.Rows, MenuFilter.Hint(EmojiText.Keys, filter))
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
                page = MenuPage.Tabbed(title, menuTabs, tab, EmojiText.Keys) with
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
                if (await PickAvatarToneAsync(context, title, toned, cancellationToken).ConfigureAwait(false) is not { } choice)
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
    public bool SetAvatar(string avatar) => SetAvatar(avatar, AvatarTarget.Model);

    /// <summary><see cref="SetAvatar(string)"/> for either avatar (2026-10-12, <c>/avatar user &lt;emoji&gt;|clear</c>).</summary>
    public bool SetAvatar(string avatar, AvatarTarget target)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        bool user = target == AvatarTarget.User;
        if (avatar == AvatarRules.Normalize(user ? _settings.Current.UserAvatar : _settings.Current.ModelAvatar))
        {
            return Unchanged();
        }

        if (avatar.Length > 0)
        {
            EmojiPrefs.Load(_settings.StorageDirectory).Picked(avatar);
        }

        if (user)
        {
            Apply(SettingsField.UserAvatar, d => SetUser(d, avatar));
        }
        else
        {
            Apply(SettingsField.ModelAvatar, d => d.ModelAvatar = avatar);
        }

        return true;
    }

    /// <summary>
    /// The skin tones of <paramref name="entry"/> as a grid of six: as it is, then light to dark, the cursor on the tone last chosen
    /// (<see cref="EmojiPrefs.Tone"/>). The tone picked (0 for none), or null on ESC.
    /// </summary>
    private async Task<int?> PickAvatarToneAsync(AvatarContext context, string title, EmojiEntry entry, CancellationToken cancellationToken)
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

        var page = new MenuPage(title + " › " + EmojiText.ToneTitle, forms.Select(Markup.Escape).ToList(), EmojiText.ToneKeys)
        {
            Grid = new MenuGrid(AvatarCellWidth),
            Marks = marks,
            Footer = (_, row) => row >= 0 && row < forms.Count
                ? new MenuFooter(string.Join('\n', new[] { EmojiText.NameLine(forms[row], entry) + " · " + EmojiText.ToneNames[row], EmojiText.CodeLine(forms[row], entry, forms[row] == context.Current, UsersOf(context, forms[row])), AvatarPreview(context, forms[row]) }.Where(l => l.Length > 0))) { KeepLines = true }
                : null,
        };
        return await _pane.PickAsync(page, Math.Clamp(context.Prefs.Tone, 0, EmojiCatalog.ToneCount), cancellationToken).ConfigureAwait(false) is { } pick ? pick.Row : null;
    }

    /// <summary>
    /// An avatar typed where no pane can show the grid (<paramref name="field"/>'s, <paramref name="saved"/> now, <paramref name="set"/>
    /// storing it): an emoji is taken as it is, a word or two as the best of a search (<see cref="EmojiSearch.Rank"/>), an empty line
    /// as none. True when it saved.
    /// </summary>
    private async Task<bool> TypeAvatarAsync(SettingsField field, string saved, Action<AppSettingsData, string> set, CancellationToken cancellationToken)
    {
        var result = await EditTextAsync(field, new MenuPage(Crumb(FieldName(field)), [], EditKeys), 0, saved, allowEmpty: true, cancellationToken).ConfigureAwait(false);
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

        Apply(field, d => set(d, chosen));
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

            string avatar = SavedAvatar(home, other);
            if (avatar.Length > 0)
            {
                (usedBy.TryGetValue(avatar, out var names) ? names : usedBy[avatar] = []).Add(other);
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
        return new AvatarContext(catalog, EmojiPrefs.Load(home), AvatarRules.Normalize(_settings.Current.ModelAvatar), profile, usedBy,
            EmojiSuggest.For(profile, persona, catalog.Entries));
    }

    /// <summary>
    /// Profile <paramref name="name"/>'s saved avatar under <paramref name="home"/>, normalised (<see cref="AvatarRules.Normalize"/>): ""
    /// for none, and for a profile file that cannot be read (/profile says what is wrong with it). The loaded profile's is
    /// <see cref="AppSettings.Current"/>'s, unsaved edits and all (<see cref="ProfileAvatar"/>).
    /// </summary>
    private static string SavedAvatar(string home, string name)
    {
        try
        {
            return AvatarRules.Normalize(Profiles.ReadProfileFile(Profiles.ProfileFile(home, name)).ModelAvatar);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Profile <paramref name="name"/>'s avatar for a picker: the loaded one's from the store, another's from its file (<see cref="SavedAvatar"/>).</summary>
    private string ProfileAvatar(string name) =>
        Profiles.NameEquals(name, _settings.ProfileName) ? AvatarRules.Normalize(_settings.Current.ModelAvatar) : SavedAvatar(_settings.StorageDirectory, name);

    /// <summary>
    /// A row of a profile picker (2026-10-10, the user's ask: <c>/profile</c>'s list and the advisor's): the profile's avatar, a space,
    /// its name escaped; a profile with none is led by three blanks, so the names stand in one column. Pure.
    /// </summary>
    public static string ProfilePickerRow(string name, string avatar)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(avatar);
        string lead = AvatarRules.Normalize(avatar) is { Length: > 0 } face ? face + " " : new string(' ', ProfileAvatarCells + 1);
        return Markup.Escape(lead + name);
    }

    /// <summary>The cells an avatar takes in a profile picker's row: one two-cell emoji (<see cref="AvatarRules"/>).</summary>
    public const int ProfileAvatarCells = 2;

    /// <summary>The rows of a profile picker over <paramref name="names"/>, each led by its avatar (<see cref="ProfilePickerRow"/>).</summary>
    private List<string> ProfilePickerRows(IReadOnlyList<string> names) => names.Select(name => ProfilePickerRow(name, ProfileAvatar(name))).ToList();

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

    // The tab the picker opens on: the current avatar's group, else For you, else the first group. The draft's: Recent when it has any.
    private static int OpeningAvatarTab(List<AvatarTab> tabs, AvatarContext context)
    {
        if (context.ForDraft && tabs.FindIndex(t => t.Title == EmojiText.RecentTab) is >= 0 and var recentFirst
            && tabs[recentFirst].Cells.Any(c => c is { Kind: AvatarCellKind.Emoji }))
        {
            return recentFirst;
        }

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
            return new MenuFooter(context.HomeDefault is { } own ? HomeAssistant.HomeBarText.DefaultGlyphFooter(own) : context.ForUser ? EmojiText.UserNoneFooter : EmojiText.NoneFooter);
        }

        var lines = new List<string>
        {
            EmojiText.NameLine(cell.Emoji, cell.Entry),
            EmojiText.KeywordLine(cell.Entry),
            EmojiText.CodeLine(cell.Emoji, cell.Entry, context.HomeDefault is null && cell.Emoji == context.Current, UsersOf(context, cell.Emoji)),
            AvatarPreview(context, cell.Emoji),
        };
        if (cell.Entry is { HasTones: true })
        {
            lines.Add(EmojiText.ToneLine);
        }

        return new MenuFooter(string.Join('\n', lines.Where(l => l.Length > 0))) { KeepLines = true };
    }

    // The footer's preview of emoji: the title and a reply for the model avatar, a sent line for the user's, none for the draft or a home bar item.
    private static string AvatarPreview(AvatarContext context, string emoji) =>
        context.ForDraft || context.HomeDefault is not null ? "" : context.ForUser ? EmojiText.UserPreviewLine(emoji) : EmojiText.PreviewLine(emoji, context.Profile);

    private static IReadOnlyList<string> UsersOf(AvatarContext context, string emoji) =>
        context.UsedBy.TryGetValue(emoji, out var names) ? names : [];
}
