using System.Globalization;
using NeonSidekick.Advisors;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The Advisors tab of /settings: the profile advisors (2026-10-10) ──────────

internal sealed partial class SettingsMenu
{
    /// <summary>The Advisors tab's strip title (2026-10-10, the user's ask: after Botchat). Pinned.</summary>
    public const string AdvisorsTabTitle = "Advisors";

    /// <summary>The list editor's first row. Pinned.</summary>
    public const string AddAdvisorRow = "+ Add an advisor…";

    /// <summary>The list editor's keys. Pinned.</summary>
    public const string AdvisorListKeys = "Enter = add, or change the advisor · Esc = back";

    /// <summary>
    /// The list editor's keys when <c>/advisor list</c> opened it alone (2026-11-23, the user's pick: one Esc closes the pane, so the
    /// hint says close, not back). Pinned.
    /// </summary>
    public const string AdvisorListCloseKeys = "Enter = add, or change the advisor · Esc = close";

    /// <summary>The list editor's second row (2026-11-22, the advisor templates). Pinned.</summary>
    public const string NewAdvisorRow = "+ New advisor from a template…";

    /// <summary>The add row's band: how it differs from the new row (2026-11-22, the user asked what adding an existing profile meant). Pinned.</summary>
    public const string AddAdvisorBand = "Makes one of your other profiles an advisor: it answers on its own server and model, with its own avatar.";

    /// <summary>The new row's band. Pinned.</summary>
    public const string NewAdvisorBand = "Makes a new profile from a template, with its persona, role, description and avatar, and adds it as an advisor.";

    /// <summary>An advisor row's band with no description. Pinned.</summary>
    public const string NoDescriptionBand = "No description: Change the description adds one, so the model knows when to ask it.";

    /// <summary>
    /// An entry's choices, in order (the description and the persona since 2026-11-22; the server and the switch later that day, from UI
    /// testing: <see cref="ChangeAdvisorServerChoice"/>, <see cref="SwitchToAdvisorChoice"/>; then the model and the reasoning alone, the
    /// user's ask: <see cref="ChangeAdvisorModelChoice"/>, <see cref="ChangeAdvisorReasoningChoice"/>). <see cref="RemoveAdvisorChoice"/> is Delete's. Pinned.
    /// </summary>
    public static readonly IReadOnlyList<string> AdvisorEntryRows =
        ["Change the role", "Change the description", "Change the persona", "Change the server", "Change the model", "Change the reasoning", "Switch to profile", "Remove"];

    /// <summary>The row of <see cref="AdvisorEntryRows"/> a Delete on an advisor goes to.</summary>
    public const int RemoveAdvisorChoice = 7;

    /// <summary>The profile picker's title when adding. Pinned.</summary>
    public const string AdvisorProfileTitle = "Which profile advises?";

    /// <summary>The notice when every other profile is already an advisor (or there is none). Pinned.</summary>
    public const string NoAdvisorProfilesNotice = "(no profile left to add: + New advisor from a template… or /profile add makes one)";

    /// <summary>A missing profile's mark on its row and in the value. Pinned.</summary>
    public const string MissingAdvisorMark = AdvisorText.MissingMark;

    /// <summary>The widest the editor pads a role to before its server; a longer one is cut (2026-10-10). Pinned.</summary>
    public const int AdvisorRoleWidth = 32;

    /// <summary>The role prompt's label. Pinned.</summary>
    public static string AdvisorRolePrompt(string profile) => $"{profile}'s role (e.g. database architect, UX designer)";

    /// <summary>The removal's question. Pinned.</summary>
    public static string RemoveAdvisorQuestion(string profile) => $"Remove {profile} from the advisors?";

    /// <summary>The notices after a change. Pinned.</summary>
    public static string AdvisorAddedNotice(AdvisorEntry entry) => FieldName(SettingsField.AdvisorList) + ": " + AdvisorRoster.Label(entry) + " added";

    public static string AdvisorChangedNotice(AdvisorEntry entry) => FieldName(SettingsField.AdvisorList) + ": " + AdvisorRoster.Label(entry);

    public static string AdvisorRemovedNotice(string profile) => FieldName(SettingsField.AdvisorList) + ": " + profile + " removed";

    /// <summary>How the menu shows <see cref="AppSettingsData.AdvisorMaxToolIterations"/>: <c>none</c> for 0. Pinned.</summary>
    public static string AdvisorIterations(int value) =>
        value <= 0 ? "none (text only)" : value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " round trip" : " round trips");

    /// <summary>How the menu shows <see cref="AppSettingsData.AdvisorThreadPairs"/>. Pinned.</summary>
    public static string AdvisorPairs(int value) => value.ToString(CultureInfo.InvariantCulture) + (value == 1 ? " question" : " questions");

    public static readonly string AdvisorCallsRangeError =
        "must be " + AppSettingsData.MinAdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAdvisorCallsPerTurn.ToString(CultureInfo.InvariantCulture) + " calls";

    public static readonly string AdvisorIterationsRangeError =
        "must be 0 to " + AppSettingsData.MaxAdvisorToolIterations.ToString(CultureInfo.InvariantCulture) + " round trips";

    public static readonly string AdvisorPairsRangeError =
        "must be " + AppSettingsData.MinAdvisorThreadPairs.ToString(CultureInfo.InvariantCulture) + " to " + AppSettingsData.MaxAdvisorThreadPairs.ToString(CultureInfo.InvariantCulture) + " questions";

    /// <summary>
    /// One row of the list editor: the profile padded, then dim its role and what it runs on (2026-10-10, the user's ask): the role
    /// padded to <paramref name="roleWidth"/> (cut past it), <c>· docker · Qwen3-32B</c> from <paramref name="facts"/>, then
    /// <paramref name="mark"/> — <see cref="AdvisorText.UnreadableMark"/> (and until 2026-10-13 a Claude Code advisor's <c>tools</c> or
    /// <c>text only</c>). A profile no longer in the home says so in their place. With <paramref name="avatar"/>
    /// (later on 2026-10-10, the user's ask) the profile's avatar leads the row as it leads a profile picker's
    /// (<see cref="ProfilePickerRow"/>): the emoji and a space, or three blanks for "" so the names stand in one column; null leads with nothing.
    /// Since 2026-11-22 (the user's ask: "line up in neat columns") the kind is padded to <paramref name="kindWidth"/> so the models start in
    /// one column, and every width is in cells (<see cref="AdvisorText.PadCells"/>), so a role with an emoji lines up. Pinned.
    /// </summary>
    public static string AdvisorRow(AdvisorEntry entry, bool exists, int width, AdvisorServerFacts? facts = null, int roleWidth = 0, string? mark = null, string? avatar = null, int kindWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string role = entry.Role.Length == 0 ? "(no role)" : entry.Role;
        string padded = AdvisorText.PadCells(entry.Profile, width);
        string name = avatar is null ? Markup.Escape(padded) : ProfilePickerRow(padded, avatar);
        if (!exists)
        {
            return name + Theme.DimMarkup(Markup.Escape(role + "  " + MissingAdvisorMark));
        }

        // The model ends on its reasoning disc (2026-11-23, the user's pick: the status bar's, ○ for none).
        string tail = (facts is null ? "" : " · " + AdvisorText.PadCells(facts.Kind, kindWidth) + " · " + facts.Model + AdvisorText.ReasoningMark(facts)) + (mark is null ? "" : " · " + mark);
        if (tail.Length > 0 && roleWidth > 0)
        {
            role = AdvisorText.PadCells(ScreenPane.Fit(role, roleWidth), roleWidth);
        }

        return name + Theme.DimMarkup(Markup.Escape(role + tail));
    }

    /// <summary>The editor's role column: the longest role (or <c>(no role)</c>) in cells, at most <see cref="AdvisorRoleWidth"/>.</summary>
    public static int AdvisorRoleColumn(IReadOnlyList<AdvisorEntry> entries) =>
        entries.Count == 0 ? 0 : Math.Min(AdvisorRoleWidth, entries.Max(e => TextCells.Width(e.Role.Length == 0 ? "(no role)" : e.Role)));

    /// <summary>
    /// The mark of a listed advisor (2026-10-10): an unreadable profile said so; null for the rest. (Until 2026-10-13 a Claude Code
    /// advisor was marked <c>tools</c> or <c>text only</c>, one holding the chat's process; every one has its own since.)
    /// </summary>
    private string? AdvisorMark(AdvisorEntry entry) =>
        AdvisorFactsOf(entry.Profile) is { Exists: true, Facts: null } ? AdvisorText.UnreadableMark : null;

    /// <summary>
    /// Each advisor's profile as last read, with the write time it was read at (2026-10-10). Its saved settings, not its facts, since
    /// 2026-11-22: the model it answers on (<see cref="AdvisorLinkedModel"/>) moves with no write, as a container starts or stops.
    /// </summary>
    private readonly Dictionary<string, (DateTime Written, bool Exists, AppSettingsData? Saved)> _advisorFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether advisor <paramref name="profile"/> is in the home and what it runs on (null when its file cannot be read), its file read
    /// again only when its <c>profile.json</c> was written since: <c>Advisor list</c>'s band asks on every move of the cursor.
    /// </summary>
    private (bool Exists, AdvisorServerFacts? Facts) AdvisorFactsOf(string profile)
    {
        string home = _settings.StorageDirectory;
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(Profiles.ProfileFile(home, profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            written = DateTime.MinValue;
        }

        (bool Exists, AppSettingsData? Saved) read = default;
        bool cached;
        lock (_advisorFacts)
        {
            cached = _advisorFacts.TryGetValue(profile, out var kept) && kept.Written == written;
            if (cached)
            {
                read = (kept.Exists, kept.Saved);
            }
        }

        if (!cached)
        {
            bool exists = Profiles.Exists(home, profile);
            read = (exists, exists ? AdvisorServer.ReadSaved(home, profile) : null);
            lock (_advisorFacts)
            {
                _advisorFacts[profile] = (written, read.Exists, read.Saved);
            }
        }

        return (read.Exists, read.Saved is { } saved ? AdvisorServer.Of(saved, AdvisorLinkedModel) : null);
    }

    /// <summary>The listed advisors' lines' columns (2026-11-22, <see cref="AdvisorText.Columns"/>), with each one's facts.</summary>
    private (List<(AdvisorEntry Advisor, AdvisorServerFacts? Facts, bool Exists)> Lines, AdvisorColumns Columns) AdvisorLines(IReadOnlyList<AdvisorEntry> entries)
    {
        var lines = entries.Select(e =>
        {
            var (exists, facts) = AdvisorFactsOf(e.Profile);
            return (e, facts, exists);
        }).ToList();
        return (lines, AdvisorText.Columns(lines));
    }

    /// <summary>
    /// <c>Advisor list</c>'s band (2026-10-10, the user's ask): an advisor a line (<see cref="AdvisorText.Detail"/>) over the field's
    /// <c>Default:</c> line, in place of its description; null with none listed, when the description shows as on every row.
    /// </summary>
    private MenuFooter? AdvisorListFooter(AppSettingsData saved)
    {
        var entries = AdvisorRoster.Entries(saved.AdvisorList);
        if (entries.Count == 0)
        {
            return null;
        }

        var (lines, columns) = AdvisorLines(entries);   // in columns (2026-11-22, the user's ask)
        return new MenuFooter(string.Join("\n", lines.Select(l => AdvisorText.Detail(l.Advisor, l.Facts, l.Exists, columns))), FieldFooter(SettingsField.AdvisorList).Last) { KeepLines = true };
    }

    /// <summary>The advisor-context picker (2026-10-10): the Claude Code advisor's rows, this setting's value under the cursor.</summary>
    private async Task<bool> PickAdvisorContextAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = AdvisorContextMode.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.AdvisorContext)), names.Select(AdvisorContextLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.IndexOf(names, saved.AdvisorContext.Trim().ToLowerInvariant())), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.AdvisorContext, d => d.AdvisorContext = name);
        return true;
    }

    /// <summary>The advisor embedded-mode picker (2026-10-10): Botchat multi-embedded's rows and hints, this setting's value under the cursor.</summary>
    private async Task<bool> PickAdvisorEmbeddedAsync(AppSettingsData saved, CancellationToken cancellationToken)
    {
        var names = App.BotChatMultiEmbedded.Names;
        var page = new MenuPage(Crumb(FieldName(SettingsField.AdvisorEmbedded)), names.Select(BotChatMultiEmbeddedLabel).ToList(), PickKeys);
        int? picked = await PickAsync(page, Math.Max(0, Array.FindIndex(names, n => string.Equals(n, saved.AdvisorEmbedded, StringComparison.OrdinalIgnoreCase))), cancellationToken).ConfigureAwait(false);
        if (picked is not { } index)
        {
            return Unchanged();
        }

        string name = names[index];
        Apply(SettingsField.AdvisorEmbedded, d => d.AdvisorEmbedded = name);
        return true;
    }

    /// <summary>
    /// <c>Advisor list</c> (2026-10-10, the forbidden strings' shape): <see cref="AddAdvisorRow"/> over one row per advisor. The add
    /// row picks a profile (the home's, less the loaded one, the temporary ones and those already listed) and then asks its role;
    /// an entry's Enter offers <see cref="AdvisorEntryRows"/> — a new role, or its removal after a yes; Delete goes straight to that removal
    /// (2026-10-16, the user's ask; on the add row nothing). Until ESC. True when anything changed.
    /// </summary>
    internal async Task<bool> EditAdvisorListAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        string title = FieldName(SettingsField.AdvisorList);
        while (true)
        {
            var entries = AdvisorRoster.Entries(_settings.Current.AdvisorList);
            int width = entries.Count == 0 ? 0 : Math.Min(24, entries.Max(e => TextCells.Width(e.Profile)) + 2);
            int roleWidth = AdvisorRoleColumn(entries);
            var (lines, columns) = AdvisorLines(entries);
            IReadOnlyList<string> rows = [Markup.Escape(AddAdvisorRow), Markup.Escape(NewAdvisorRow), .. lines.Select(l =>
                // Each advisor's avatar ahead of its name (later on 2026-10-10, the user's ask), as the profile pickers show theirs; a
                // missing profile none (its file would read as the defaults, the robot). The kind padded so the models line up (2026-11-22).
                AdvisorRow(l.Advisor, l.Exists, width, l.Facts, roleWidth, AdvisorMark(l.Advisor),
                    l.Exists ? ProfileAvatar(l.Advisor.Profile) : "", columns.Kind))];
            // Each row's band (2026-11-22): what the two add rows do, an advisor's description (or how to give it one).
            // Under /advisor list's lone visit one Esc closes the pane, and the hint says so (2026-11-23).
            var page = new MenuPage(Crumb(title), rows, _openOnly ? AdvisorListCloseKeys : AdvisorListKeys) { Footer = (_, row) => AdvisorRowBand(entries, row) };
            if (await PickListAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { Row: var index } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            if (index < AdvisorListLead)
            {
                if (!pick.Remove)   // Delete on an add row: nothing to remove
                {
                    changed |= index == 0
                        ? await AddAdvisorAsync(entries, page, cancellationToken).ConfigureAwait(false)
                        : await NewAdvisorFromTemplateAsync(page, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var entry = entries[index - AdvisorListLead];
            var choice = new MenuPage(Crumb(title + " › " + entry.Profile), AdvisorEntryRows.Select(Markup.Escape).ToList(), PickKeys);
            switch (pick.Remove ? RemoveAdvisorChoice : await PickAsync(choice, 0, cancellationToken).ConfigureAwait(false))   // Delete is the removal, asked as ever
            {
                case 0:
                    if (await ReadAdvisorRoleAsync(page, entry.Profile, entry.Role, cancellationToken).ConfigureAwait(false) is { } role && role != entry.Role)
                    {
                        _settings.Update(d => d.AdvisorList = AdvisorRoster.WithRole(d.AdvisorList, entry.Profile, role));
                        Sink.Notice(AdvisorChangedNotice(entry with { Role = role }));
                        changed = true;
                    }

                    break;

                case 1:
                    // The description (2026-11-22): the role prompt's shape, prefilled; an unchanged one says nothing, as the role's.
                    if (await ReadAdvisorTextAsync(page, AdvisorDescriptionPrompt(entry.Profile), entry.Description, cancellationToken).ConfigureAwait(false) is { } typed
                        && AdvisorRoster.CleanDescription(typed) is var description && description != entry.Description)
                    {
                        _settings.Update(d => d.AdvisorList = AdvisorRoster.WithDescription(d.AdvisorList, entry.Profile, description));
                        Sink.Notice(AdvisorDescriptionNotice(entry with { Description = description }));
                        changed = true;
                    }

                    break;

                case 2:
                    changed |= await ChangeAdvisorPersonaAsync(entry, page, cancellationToken).ConfigureAwait(false);
                    break;

                case ChangeAdvisorServerChoice:
                    changed |= await ChangeAdvisorServerAsync(entry, cancellationToken).ConfigureAwait(false);
                    break;

                case ChangeAdvisorModelChoice:
                    changed |= await ChangeAdvisorModelAsync(entry, cancellationToken).ConfigureAwait(false);
                    break;

                case ChangeAdvisorReasoningChoice:
                    changed |= await ChangeAdvisorReasoningAsync(entry, cancellationToken).ConfigureAwait(false);
                    break;

                case SwitchToAdvisorChoice:
                    if (await SwitchToAdvisorAsync(entry, cancellationToken).ConfigureAwait(false))
                    {
                        return true;   // another profile now: the visit ends, the pane closes and the screen switches (2026-11-22)
                    }

                    break;

                case RemoveAdvisorChoice:
                    // Asked first, as every list's remove (2026-10-07).
                    var removing = new MenuPage(RemoveAdvisorQuestion(entry.Profile), ConfirmRows, ConfirmKeys) { Hotkeys = ConfirmHotkeys };
                    if (await PickAsync(removing, 0, cancellationToken).ConfigureAwait(false) != 1)
                    {
                        Sink.Notice(ChatScreen.KeptNotice);
                        break;
                    }

                    _settings.Update(d => d.AdvisorList = AdvisorRoster.Without(d.AdvisorList, entry.Profile));
                    Sink.Notice(AdvisorRemovedNotice(entry.Profile));
                    changed = true;
                    break;
            }
        }
    }

    /// <summary>
    /// The add row: a profile not yet listed, then its persona (2026-11-22: <see cref="KeepPersonaRow"/>, or a template), then its role (a
    /// blank role keeps none), prefilled with the template's, or on Keep the role of the template its persona matches. The description
    /// is the template's (or the matched one's), else none. A template's persona is written only after the role is confirmed, and over
    /// the profile's own <c>persona.md</c> only after a yes. Esc anywhere adds and writes nothing. True when one was added.
    /// </summary>
    private async Task<bool> AddAdvisorAsync(IReadOnlyList<AdvisorEntry> entries, MenuPage listPage, CancellationToken cancellationToken)
    {
        string loaded = _settings.ProfileName;
        string home = _settings.StorageDirectory;
        var names = PickerNames(Profiles.List(home), loaded)
            .Where(n => !AdvisorRoster.SameProfile(n, loaded) && !entries.Any(e => AdvisorRoster.SameProfile(e.Profile, n)))
            .ToList();
        if (names.Count == 0)
        {
            Sink.Notice(NoAdvisorProfilesNotice);
            return false;
        }

        string crumb = FieldName(SettingsField.AdvisorList);
        var page = new MenuPage(Crumb(crumb + " › " + AdvisorProfileTitle), ProfilePickerRows(names), PickKeys) { JumpNames = names };   // avatars led (2026-10-10)
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } picked)
        {
            return false;
        }

        string profile = names[picked];
        var persona = new Llm.PersonaFile(Profiles.Directory(home, profile));
        string? own = persona.Read();
        if (await PickAdvisorTemplateAsync(crumb + " › " + profile + " › " + AdvisorPersonaTitle, [(KeepPersonaRow, KeepPersonaBand)], [], own, cancellationToken).ConfigureAwait(false) is not { } choice)
        {
            return false;
        }

        var template = choice.Template;
        var matched = template is null ? AdvisorTemplates.All(home).Matching(own) : null;
        if (await ReadAdvisorRoleAsync(listPage, profile, template?.Role ?? matched?.Role ?? "", cancellationToken).ConfigureAwait(false) is not { } role)
        {
            return false;   // ESC at the role: nothing added
        }

        if (template is not null && !await WriteAdvisorPersonaAsync(persona, profile, template, own is not null, cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(AdvisorNotAddedNotice(profile));   // 2026-11-23, review #69: its Kept notice read as success
            return false;
        }

        var entry = new AdvisorEntry(profile, role, template?.Description ?? matched?.Description ?? "");
        _settings.Update(d => d.AdvisorList = AdvisorRoster.Add(d.AdvisorList, entry));
        Sink.Notice(AdvisorAddedNotice(entry));
        return true;
    }

    /// <summary>The role, typed under <paramref name="page"/> (the pane's input row, or the prompt without it); null on ESC.</summary>
    private Task<string?> ReadAdvisorRoleAsync(MenuPage page, string profile, string initial, CancellationToken cancellationToken) =>
        ReadAdvisorTextAsync(page, AdvisorRolePrompt(profile), initial, cancellationToken);

    /// <summary>
    /// A line typed under <paramref name="page"/> titled <paramref name="prompt"/> (the role's shape, shared since 2026-11-22 with the
    /// description, the new advisor's name and a template's name): the pane's input row, or the prompt without it; null on ESC.
    /// </summary>
    private async Task<string?> ReadAdvisorTextAsync(MenuPage page, string prompt, string initial, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Title = Crumb(prompt), Hint = EditKeys, Footer = null }, 0, _input, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(prompt, EditKeys));
            result = await _input.ReadAsync(initial, remember: false, allowEmpty: true, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
        }

        return result is InputResult.Submitted submitted ? submitted.Text.Trim() : null;
    }
}
