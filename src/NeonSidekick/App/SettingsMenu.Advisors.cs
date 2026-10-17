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

    /// <summary>An entry's two choices, in order. Pinned.</summary>
    public static readonly IReadOnlyList<string> AdvisorEntryRows = ["Change the role", "Remove"];

    /// <summary>The profile picker's title when adding. Pinned.</summary>
    public const string AdvisorProfileTitle = "Which profile advises?";

    /// <summary>The notice when every other profile is already an advisor (or there is none). Pinned.</summary>
    public const string NoAdvisorProfilesNotice = "(no other profile to add: make one with /profile add, or every one is an advisor already)";

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
    /// (<see cref="ProfilePickerRow"/>): the emoji and a space, or three blanks for "" so the names stand in one column; null leads with nothing. Pinned.
    /// </summary>
    public static string AdvisorRow(AdvisorEntry entry, bool exists, int width, AdvisorServerFacts? facts = null, int roleWidth = 0, string? mark = null, string? avatar = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string role = entry.Role.Length == 0 ? "(no role)" : entry.Role;
        string name = avatar is null ? Markup.Escape(entry.Profile.PadRight(width)) : ProfilePickerRow(entry.Profile.PadRight(width), avatar);
        if (!exists)
        {
            return name + Theme.DimMarkup(Markup.Escape(role + "  " + MissingAdvisorMark));
        }

        string tail = (facts is null ? "" : " · " + facts.Kind + " · " + facts.Model) + (mark is null ? "" : " · " + mark);
        if (tail.Length > 0 && roleWidth > 0)
        {
            role = ScreenPane.Fit(role, roleWidth).PadRight(roleWidth);
        }

        return name + Theme.DimMarkup(Markup.Escape(role + tail));
    }

    /// <summary>The editor's role column: the longest role (or <c>(no role)</c>), at most <see cref="AdvisorRoleWidth"/>.</summary>
    public static int AdvisorRoleColumn(IReadOnlyList<AdvisorEntry> entries) =>
        entries.Count == 0 ? 0 : Math.Min(AdvisorRoleWidth, entries.Max(e => e.Role.Length == 0 ? "(no role)".Length : e.Role.Length));

    /// <summary>
    /// The mark of a listed advisor (2026-10-10): an unreadable profile said so; null for the rest. (Until 2026-10-13 a Claude Code
    /// advisor was marked <c>tools</c> or <c>text only</c>, one holding the chat's process; every one has its own since.)
    /// </summary>
    private string? AdvisorMark(AdvisorEntry entry) =>
        AdvisorFactsOf(entry.Profile) is { Exists: true, Facts: null } ? AdvisorText.UnreadableMark : null;

    /// <summary>Each advisor's profile as last read, with the write time it was read at (2026-10-10).</summary>
    private readonly Dictionary<string, (DateTime Written, bool Exists, AdvisorServerFacts? Facts)> _advisorFacts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether advisor <paramref name="profile"/> is in the home and what it runs on (null when its file cannot be read), read again
    /// only when its <c>profile.json</c> was written since: <c>Advisor list</c>'s band asks on every move of the cursor.
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

        lock (_advisorFacts)
        {
            if (_advisorFacts.TryGetValue(profile, out var kept) && kept.Written == written)
            {
                return (kept.Exists, kept.Facts);
            }
        }

        bool exists = Profiles.Exists(home, profile);
        var facts = exists ? AdvisorServer.Read(home, profile) : null;
        lock (_advisorFacts)
        {
            _advisorFacts[profile] = (written, exists, facts);
        }

        return (exists, facts);
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

        var lines = entries.Select(e =>
        {
            var (exists, facts) = AdvisorFactsOf(e.Profile);
            return AdvisorText.Detail(e, facts, exists);
        });
        return new MenuFooter(string.Join("\n", lines), FieldFooter(SettingsField.AdvisorList).Last) { KeepLines = true };
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
            int width = entries.Count == 0 ? 0 : Math.Min(24, entries.Max(e => e.Profile.Length) + 2);
            int roleWidth = AdvisorRoleColumn(entries);
            IReadOnlyList<string> rows = [Markup.Escape(AddAdvisorRow), .. entries.Select(e =>
            {
                var (exists, facts) = AdvisorFactsOf(e.Profile);
                // Each advisor's avatar ahead of its name (later on 2026-10-10, the user's ask), as the profile pickers show theirs; a
                // missing profile none (its file would read as the defaults, the robot).
                return AdvisorRow(e, exists, width, facts, roleWidth, AdvisorMark(e), exists ? ProfileAvatar(e.Profile) : "");
            })];
            var page = new MenuPage(Crumb(title), rows, AdvisorListKeys);
            if (await PickListAsync(page, Math.Min(cursor, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { Row: var index } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = index;
            if (index == 0)
            {
                if (!pick.Remove)   // Delete on the add row: nothing to remove
                {
                    changed |= await AddAdvisorAsync(entries, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            var entry = entries[index - 1];
            var choice = new MenuPage(Crumb(title + " › " + entry.Profile), AdvisorEntryRows.Select(Markup.Escape).ToList(), PickKeys);
            switch (pick.Remove ? 1 : await PickAsync(choice, 0, cancellationToken).ConfigureAwait(false))   // Delete is the removal, asked as ever
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

    /// <summary>The add row: a profile not yet listed, then its role (a blank role keeps none). True when one was added.</summary>
    private async Task<bool> AddAdvisorAsync(IReadOnlyList<AdvisorEntry> entries, CancellationToken cancellationToken)
    {
        string loaded = _settings.ProfileName;
        var names = PickerNames(Profiles.List(_settings.StorageDirectory), loaded)
            .Where(n => !AdvisorRoster.SameProfile(n, loaded) && !entries.Any(e => AdvisorRoster.SameProfile(e.Profile, n)))
            .ToList();
        if (names.Count == 0)
        {
            Sink.Notice(NoAdvisorProfilesNotice);
            return false;
        }

        var page = new MenuPage(Crumb(FieldName(SettingsField.AdvisorList) + " › " + AdvisorProfileTitle), ProfilePickerRows(names), PickKeys) { JumpNames = names };   // avatars led (2026-10-10)
        if (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { } picked)
        {
            return false;
        }

        string profile = names[picked];
        if (await ReadAdvisorRoleAsync(page, profile, "", cancellationToken).ConfigureAwait(false) is not { } role)
        {
            return false;   // ESC at the role: nothing added
        }

        var entry = new AdvisorEntry(profile, role);
        _settings.Update(d => d.AdvisorList = AdvisorRoster.Add(d.AdvisorList, entry));
        Sink.Notice(AdvisorAddedNotice(entry));
        return true;
    }

    /// <summary>The role, typed under <paramref name="page"/> (the pane's input row, or the prompt without it); null on ESC.</summary>
    private async Task<string?> ReadAdvisorRoleAsync(MenuPage page, string profile, string initial, CancellationToken cancellationToken)
    {
        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(page with { Title = Crumb(AdvisorRolePrompt(profile)), Hint = EditKeys }, 0, _input, initial, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Flow.Notice(PromptTitle(AdvisorRolePrompt(profile), EditKeys));
            result = await _input.ReadAsync(initial, remember: false, allowEmpty: true, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
        }

        return result is InputResult.Submitted submitted ? submitted.Text.Trim() : null;
    }
}
