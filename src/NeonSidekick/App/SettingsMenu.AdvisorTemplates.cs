using NeonSidekick.Advisors;
using NeonSidekick.Emojis;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

// ── The Advisors tab's templates (2026-11-22, the user's ask) ─────────────────

internal sealed partial class SettingsMenu
{
    /// <summary>The rows above the advisors on <c>Advisor list</c>: <see cref="AddAdvisorRow"/> and <see cref="NewAdvisorRow"/>.</summary>
    public const int AdvisorListLead = 2;

    /// <summary>The persona page's first row when adding a profile: its own persona as it is. Pinned.</summary>
    public const string KeepPersonaRow = "Keep its persona";

    /// <summary>Its band. Pinned.</summary>
    public const string KeepPersonaBand = "Leaves the profile's persona as it is; the role is offered from the template it matches, if any.";

    /// <summary>The persona page's title when adding a profile. Pinned.</summary>
    public const string AdvisorPersonaTitle = "Which persona?";

    /// <summary>The template page's title for a new advisor. Pinned.</summary>
    public const string AdvisorTemplateTitle = "Which template?";

    /// <summary>Change the persona's rows after the templates (Edit only with an editor to open). Pinned.</summary>
    public const string EditPersonaRow = "Edit in your editor";

    public const string DefaultPersonaRow = "Default persona";

    public const string SaveTemplateRow = "Save as a template…";

    /// <summary>Their bands. Pinned.</summary>
    public const string EditPersonaBand = "Opens the advisor's persona.md in your editor, made from the default persona when it has none.";

    public const string DefaultPersonaBand = "Removes the advisor's persona.md, so it answers under the default persona.";

    public const string SaveTemplateBand = "Saves the advisor's persona, role, description and avatar as your own template, listed with the built-in ones.";

    /// <summary>The template pages' keys. Pinned.</summary>
    public const string AdvisorTemplateKeys = "Enter = pick · Del = delete yours · Esc = back";

    /// <summary>The mark on the template the profile's persona is. Pinned.</summary>
    public const string CurrentTemplateMark = "(current)";

    /// <summary>The description prompt's label. Pinned.</summary>
    public static string AdvisorDescriptionPrompt(string profile) => $"When to ask {profile} (e.g. schema design, query plans, migrations)";

    /// <summary>The notice after a description change: what <c>neon_advisor</c>'s description now says of it. Pinned.</summary>
    public static string AdvisorDescriptionNotice(AdvisorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return FieldName(SettingsField.AdvisorList) + ": " + (entry.Description.Length == 0 ? entry.Profile + " has no description" : entry.Profile + ": " + entry.Description);
    }

    /// <summary>The new advisor's name prompt. Pinned.</summary>
    public const string AdvisorNamePrompt = "The new advisor's profile name";

    /// <summary>A name a profile has, typed on the pane. Pinned.</summary>
    public static string AdvisorNameTakenError(string existing, bool listed) => listed
        ? $"A profile named \"{existing}\" already exists and is an advisor; type another name."
        : $"A profile named \"{existing}\" already exists; type another name, or add it with {AddAdvisorRow}";

    /// <summary>The yes/no before a template's persona replaces a profile's own. Pinned.</summary>
    public static string ReplaceAdvisorPersonaQuestion(string profile, string id) => $"Replace {profile}'s persona.md with the {id} template?";

    /// <summary>The notice after a template's persona was written. Pinned.</summary>
    public static string AdvisorPersonaNotice(string profile, string id) => FieldName(SettingsField.AdvisorList) + ": " + profile + "'s persona is the " + id + " template's";

    /// <summary>A persona write that failed. Pinned.</summary>
    public static string AdvisorPersonaFailedError(string profile, string detail) => $"Could not write {profile}'s persona.md: {detail}";

    /// <summary>Edit in your editor's notice and failure. Pinned.</summary>
    public static string AdvisorPersonaEditingNotice(string profile) => $"(opened {profile}'s persona.md in your editor; its next answer uses it)";

    public static string AdvisorPersonaEditFailedError(string profile, string detail) => $"Could not open {profile}'s persona.md: {detail}";

    /// <summary>Default persona's question, notice, and the notice with no file. Pinned.</summary>
    public static string AdvisorDefaultPersonaQuestion(string profile) => $"Remove {profile}'s persona.md and go back to the default persona?";

    public static string AdvisorDefaultPersonaNotice(string profile) => FieldName(SettingsField.AdvisorList) + ": " + profile + " answers under the default persona";

    public static string AdvisorNoPersonaNotice(string profile) => $"({profile} has no persona.md; the default persona is already in use)";

    /// <summary>A <c>(missing)</c> advisor's persona rows. Pinned.</summary>
    public static string MissingAdvisorPersonaNotice(string profile) => $"({profile}'s profile no longer exists, so it has no persona to change)";

    /// <summary>Save as a template's prompt, refusals, question and notice. Pinned.</summary>
    public const string TemplateNamePrompt = "The template's name (a profile name: an advisor made from it is named after it)";

    public static readonly string TemplateNameError = "Template name " + Profiles.NameError + ".";

    /// <summary>A template name starting with _ (2026-11-23, review #19): an advisor made from it would get a temporary profile's name. Pinned.</summary>
    public const string TemplateTemporaryNameError = "A template's name can't start with _: an advisor made from it takes that name, and _ names a temporary profile.";

    /// <summary>+ Add an advisor stopped at the persona (2026-11-23, review #69): a No to replacing it, or a failed write, adds nothing. Pinned.</summary>
    public static string AdvisorNotAddedNotice(string profile) => $"({profile} was not added as an advisor)";

    public static string NothingToSaveNotice(string profile) => $"({profile} answers under the default persona: nothing to save as a template)";

    public static string ReplaceYourTemplateQuestion(string id) => $"Replace your {id} template?";

    public static string TemplateSavedNotice(string id, bool shadows) =>
        $"(saved your {id} template" + (shadows ? "; it is used in place of the built-in one" : "") + ")";

    public static string TemplateSaveFailedError(string id, string detail) => $"Could not save your {id} template: {detail}";

    /// <summary>Delete on your template: the question, the notice (how it went, and a built-in back), the refusals. Pinned.</summary>
    public static string DeleteTemplateQuestion(string id) => $"Delete your {id} template?";

    public static string TemplateDeletedNotice(string id, DeleteWay way, bool builtInBack) =>
        "(" + (way switch
        {
            DeleteWay.Recycled => $"moved your {id} template to the {FileText.RecycleBinName}",
            DeleteWay.DeletedNoBin => $"deleted your {id} template (no {FileText.RecycleBinName} on that drive)",
            _ => $"deleted your {id} template",
        }) + (builtInBack ? "; the built-in one is back" : "") + ")";

    public static string TemplateDeleteFailedError(string id, string detail) => $"Could not delete your {id} template: {detail}";

    public static string TemplateChangedNotice(string id) => $"(your {id} template changed or is gone since the list opened; here is the list as it is now)";

    /// <summary>The persona's writer for a new advisor, a test's seam (the profile's <see cref="PersonaFile"/> when null).</summary>
    internal Action<string, string>? AdvisorPersonaWriter { get; set; }

    /// <summary>A template page's pick: a lead row (<see cref="Lead"/>), a template, or a tail row (<see cref="Tail"/>); −1 for neither.</summary>
    private sealed record AdvisorTemplatePick(int Lead, AdvisorTemplate? Template, int Tail);

    /// <summary>
    /// <c>Advisor list</c>'s band for <paramref name="row"/>: the add rows' texts, then each advisor's description, and since 2026-11-23
    /// (the user's ask) its reasoning level in words on a last row of its own (<see cref="AdvisorReasoningBand"/>), which a long
    /// description never pushes off; none for a missing or unreadable profile.
    /// </summary>
    private MenuFooter? AdvisorRowBand(IReadOnlyList<AdvisorEntry> entries, int row) => row switch
    {
        0 => new MenuFooter(AddAdvisorBand),
        1 => new MenuFooter(NewAdvisorBand),
        _ when row - AdvisorListLead is var at && at >= 0 && at < entries.Count =>
            new MenuFooter(entries[at].Description.Length == 0 ? NoDescriptionBand : entries[at].Description,
                AdvisorFactsOf(entries[at].Profile) is { Exists: true, Facts: { } facts } ? AdvisorReasoningBand(facts.Reasoning) : null),
        _ => null,
    };

    /// <summary>
    /// An advisor's reasoning in the band (2026-11-23, the user's ask): <c>Reasoning: ◕ high (extensive thinking)</c>, the row's disc
    /// beside the level and <c>/reasoning</c>'s words for it, so the disc reads. Pinned.
    /// </summary>
    public static string AdvisorReasoningBand(string level) =>
        "Reasoning: " + Llm.ReasoningLevel.Glyph(level) + " " + level + " (" + Llm.ReasoningLevel.Describe(level) + ")";

    /// <summary>
    /// One template's row (2026-11-22): <see cref="AdvisorText.TemplateLine"/>'s lead and name escaped, then dim <see cref="AdvisorText.YoursMark"/>
    /// for yours and <see cref="CurrentTemplateMark"/> for the one the profile's persona is. Pinned.
    /// </summary>
    public static string AdvisorTemplateRow(AdvisorTemplate template, bool current)
    {
        ArgumentNullException.ThrowIfNull(template);
        string marks = (template.Yours ? " " + AdvisorText.YoursMark : "") + (current ? " " + CurrentTemplateMark : "");
        return Markup.Escape(AdvisorText.TemplateLead(template) + AdvisorText.TemplateName(template)) + (marks.Length == 0 ? "" : Theme.DimMarkup(Markup.Escape(marks)));
    }

    /// <summary>
    /// A page of templates between <paramref name="lead"/> and <paramref name="tail"/> rows (each with its band), the one
    /// <paramref name="persona"/> is marked current: the templates read when it opens (their problems said then), each row's band the
    /// template's description or its persona's first sentence, worked out once; a typed letter jumps by id (the other rows by nothing,
    /// so <c>d</c> never lands on <c>Default persona</c>). Delete on one of yours removes it after a yes and reads the list again; on any
    /// other row it does nothing. Null on Esc.
    /// </summary>
    private async Task<AdvisorTemplatePick?> PickAdvisorTemplateAsync(
        string crumb, IReadOnlyList<(string Row, string Band)> lead, IReadOnlyList<(string Row, string Band)> tail, string? persona, CancellationToken cancellationToken)
    {
        int cursor = 0;
        bool reported = false;
        while (true)
        {
            var scan = AdvisorTemplates.All(_settings.StorageDirectory);
            if (!reported)
            {
                AdvisorTemplates.Report(scan);
                reported = true;
            }

            var templates = scan.Templates;
            var current = scan.Matching(persona);
            var bands = new List<string>();
            var jumps = new List<string>();
            bands.AddRange(lead.Select(l => l.Band));
            jumps.AddRange(lead.Select(_ => ""));
            bands.AddRange(templates.Select(AdvisorTemplates.Band));
            jumps.AddRange(templates.Select(t => t.Id));
            bands.AddRange(tail.Select(t => t.Band));
            jumps.AddRange(tail.Select(_ => ""));
            // Rules between the lead, the templates and the tail (2026-11-24, the user's ask); bands and jumps are items, "" at a rule.
            var ruled = RuledRows.Of([
                lead.Select(l => Markup.Escape(l.Row)).ToList(),
                templates.Select(t => AdvisorTemplateRow(t, ReferenceEquals(t, current))).ToList(),
                tail.Select(t => Markup.Escape(t.Row)).ToList()]);
            var page = new MenuPage(Crumb(crumb), ruled.Rows, AdvisorTemplateKeys)
            {
                Headings = ruled.Headings,
                JumpNames = Enumerable.Range(0, ruled.Rows.Count).Select(r => ruled.ItemOf(r) is >= 0 and var i ? jumps[i] : "").ToList(),
                Footer = (_, row) => ruled.ItemOf(row) is >= 0 and var i && i < bands.Count && bands[i].Length > 0 ? new MenuFooter(bands[i]) : null,
            };
            if (await PickListAsync(page, ruled.RowOf(cursor), cancellationToken).ConfigureAwait(false) is not { Row: var pickedRow } pick)
            {
                return null;
            }

            int index = ruled.ItemOf(pickedRow);
            if (index < 0)
            {
                continue;
            }

            cursor = index;
            int at = index - lead.Count;
            if (pick.Remove)
            {
                if (at >= 0 && at < templates.Count && templates[at].Yours)
                {
                    await DeleteYourTemplateAsync(templates[at], cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            if (index < lead.Count)
            {
                return new AdvisorTemplatePick(index, null, -1);
            }

            return at < templates.Count ? new AdvisorTemplatePick(-1, templates[at], -1) : new AdvisorTemplatePick(-1, null, at - templates.Count);
        }
    }

    /// <summary>
    /// Delete on your template <paramref name="seen"/>: the folder checked again first (the theme menu's rescan), so one gone or changed
    /// since the page opened is a notice and nothing is deleted; else after a yes, through the app's one delete (the Recycle bin when
    /// <c>File deletes to Recycle bin</c> is on).
    /// </summary>
    private async Task DeleteYourTemplateAsync(AdvisorTemplate seen, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (AdvisorTemplates.All(home).Find(seen.Id) is not { Yours: true } now || now != seen)
        {
            Sink.Notice(TemplateChangedNotice(seen.Id));
            return;
        }

        if (!await ConfirmAsync(DeleteTemplateQuestion(seen.Id), cancellationToken, close: false).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return;
        }

        try
        {
            var way = AdvisorTemplates.DeleteYours(home, seen.Id, ThemeBin());
            Sink.Notice(TemplateDeletedNotice(seen.Id, way, AdvisorTemplates.BuiltIn.Find(seen.Id) is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(TemplateDeleteFailedError(seen.Id, ex.Message));
        }
    }

    /// <summary>
    /// Writes <paramref name="template"/>'s persona as <paramref name="profile"/>'s <c>persona.md</c>, after a yes when the profile has
    /// its own (<paramref name="replacing"/>). False on a no (kept) or a failed write (said).
    /// </summary>
    private async Task<bool> WriteAdvisorPersonaAsync(PersonaFile persona, string profile, AdvisorTemplate template, bool replacing, CancellationToken cancellationToken)
    {
        if (replacing && !await ConfirmAsync(ReplaceAdvisorPersonaQuestion(profile, template.Id), cancellationToken, close: false).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        try
        {
            persona.WriteText(template.Persona);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(AdvisorPersonaFailedError(profile, ex.Message));
            return false;
        }
    }

    /// <summary>
    /// <see cref="NewAdvisorRow"/> (2026-11-22): a template, then the new profile's name (prefilled with its id; a taken, invalid or
    /// temporary name says so and asks again), then its role (prefilled with the template's), then <see cref="AdvisorFromTemplate.Create"/>:
    /// the profile made from the loaded one's saved settings with the template's persona and avatar, listed here. No switch. True when
    /// one was added.
    /// </summary>
    private async Task<bool> NewAdvisorFromTemplateAsync(MenuPage listPage, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        string crumb = FieldName(SettingsField.AdvisorList);
        if (await PickAdvisorTemplateAsync(crumb + " › " + AdvisorTemplateTitle, [], [], null, cancellationToken).ConfigureAwait(false) is not { Template: { } template })
        {
            return false;
        }

        string initial = template.Id;
        string name;
        while (true)
        {
            if (await ReadAdvisorTextAsync(listPage, AdvisorNamePrompt, initial, cancellationToken).ConfigureAwait(false) is not { } typed)
            {
                return false;
            }

            var saved = _settings.Current.AdvisorList;
            string? problem = AdvisorFromTemplate.NameProblem(home, saved, template.Id, typed);
            if (problem is null)
            {
                name = typed;
                break;
            }

            // On the pane the typed command's wording would point at /advisor new; a taken name says so in the pane's terms.
            Sink.Error(Profiles.Resolve(home, typed) is { } existing && Profiles.IsValidName(typed) && !Profiles.IsTemporary(typed)
                ? AdvisorNameTakenError(existing, AdvisorRoster.Contains(saved, existing))
                : problem);
            initial = typed;
        }

        if (await ReadAdvisorRoleAsync(listPage, name, template.Role, cancellationToken).ConfigureAwait(false) is not { } role)
        {
            return false;
        }

        // The effective switch (2026-11-23, review #16): Advisors on through a flag or an env var is on.
        var made = AdvisorFromTemplate.Create(_settings, template, name, role, writePersona: AdvisorPersonaWriter, advisorsOn: EffectiveNow().Advisors);
        if (made.Error is { } error)
        {
            Sink.Error(error);
        }

        if (made.Notice is { } notice)
        {
            Sink.Notice(notice);
        }

        return made.Entry is not null;
    }

    /// <summary>
    /// <c>Change the persona</c> (2026-11-22): the templates, then <see cref="EditPersonaRow"/> (with an editor to open),
    /// <see cref="DefaultPersonaRow"/> and <see cref="SaveTemplateRow"/>. A template replaces the advisor's persona (after a yes when it
    /// has its own), then offers its role unless the advisor's already is, and takes its description when the advisor's is blank or
    /// still the old template's (a hand-written one is kept). True when the list changed.
    /// </summary>
    private async Task<bool> ChangeAdvisorPersonaAsync(AdvisorEntry entry, MenuPage listPage, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (!Profiles.Exists(home, entry.Profile))
        {
            Sink.Notice(MissingAdvisorPersonaNotice(entry.Profile));
            return false;
        }

        var persona = new PersonaFile(Profiles.Directory(home, entry.Profile));
        string? own = persona.Read();
        List<(string Row, string Band)> tail = [];
        if (_openFile is not null)
        {
            tail.Add((EditPersonaRow, EditPersonaBand));
        }

        tail.Add((DefaultPersonaRow, DefaultPersonaBand));
        tail.Add((SaveTemplateRow, SaveTemplateBand));
        string crumb = FieldName(SettingsField.AdvisorList) + " › " + entry.Profile + " › " + AdvisorEntryRows[2];
        if (await PickAdvisorTemplateAsync(crumb, [], tail, own, cancellationToken).ConfigureAwait(false) is not { } pick)
        {
            return false;
        }

        if (pick.Template is { } template)
        {
            var old = AdvisorTemplates.All(home).Matching(own);
            if (!await WriteAdvisorPersonaAsync(persona, entry.Profile, template, own is not null, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Sink.Notice(AdvisorPersonaNotice(entry.Profile, template.Id));
            if (entry.Description.Length == 0 || (old is not null && entry.Description == AdvisorRoster.CleanDescription(old.Description)))
            {
                _settings.Update(d => d.AdvisorList = AdvisorRoster.WithDescription(d.AdvisorList, entry.Profile, template.Description));
            }

            if (!string.Equals(entry.Role, template.Role, StringComparison.Ordinal)
                && await ReadAdvisorRoleAsync(listPage, entry.Profile, template.Role, cancellationToken).ConfigureAwait(false) is { } role && role != entry.Role)
            {
                _settings.Update(d => d.AdvisorList = AdvisorRoster.WithRole(d.AdvisorList, entry.Profile, role));
                Sink.Notice(AdvisorChangedNotice(entry with { Role = role }));
            }

            return true;
        }

        switch (tail[pick.Tail].Row)
        {
            case EditPersonaRow:
                try
                {
                    persona.EnsureExists();
                    _openFile!(persona.FilePath);
                    Sink.Notice(AdvisorPersonaEditingNotice(entry.Profile));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Sink.Error(AdvisorPersonaEditFailedError(entry.Profile, Diagnostics.LogText.Excerpt(ex.Message)));
                }

                return false;

            case DefaultPersonaRow:
                if (!File.Exists(persona.FilePath))
                {
                    Sink.Notice(AdvisorNoPersonaNotice(entry.Profile));
                    return false;
                }

                if (!await ConfirmAsync(AdvisorDefaultPersonaQuestion(entry.Profile), cancellationToken, close: false).ConfigureAwait(false))
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                    return false;
                }

                try
                {
                    persona.Delete();
                    Sink.Notice(AdvisorDefaultPersonaNotice(entry.Profile));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Sink.Error(AdvisorPersonaFailedError(entry.Profile, ex.Message));
                }

                return false;

            default:
                return await SaveAdvisorTemplateAsync(entry, persona, own is not null, listPage, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="SaveTemplateRow"/>: the advisor's own <c>persona.md</c> (none is the default persona: nothing to save), its role and
    /// description, and its avatar when it picked one, as your template under a name (prefilled with the profile's; one of yours by that
    /// name asks before it is replaced; a built-in's name says yours is used in its place). Never changes the list: false.
    /// </summary>
    private async Task<bool> SaveAdvisorTemplateAsync(AdvisorEntry entry, PersonaFile persona, bool hasOwn, MenuPage listPage, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        if (!hasOwn)
        {
            Sink.Notice(NothingToSaveNotice(entry.Profile));
            return false;
        }

        string initial = entry.Profile;
        string id;
        while (true)
        {
            if (await ReadAdvisorTextAsync(listPage, TemplateNamePrompt, initial, cancellationToken).ConfigureAwait(false) is not { } typed)
            {
                return false;
            }

            if (Profiles.IsValidName(typed) && !Profiles.IsTemporary(typed))
            {
                id = typed;
                break;
            }

            Sink.Error(Profiles.IsValidName(typed) ? TemplateTemporaryNameError : TemplateNameError);
            initial = typed;
        }

        if (AdvisorTemplates.HasYours(home, id) && !await ConfirmAsync(ReplaceYourTemplateQuestion(id), cancellationToken, close: false).ConfigureAwait(false))
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return false;
        }

        // The avatar when the profile picked one: none ("" from 🚫 or /avatar clear) and the default robot aren't a choice to keep.
        string face = AvatarRules.Normalize(ProfileAvatar(entry.Profile));
        string? avatar = face.Length == 0 || face == AvatarRules.DefaultAvatar ? null : face;
        try
        {
            AdvisorTemplates.SaveYours(home, id, File.ReadAllText(persona.FilePath), entry.Role, entry.Description, avatar);
            Sink.Notice(TemplateSavedNotice(id, AdvisorTemplates.BuiltIn.Find(id) is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(TemplateSaveFailedError(id, ex.Message));
        }

        return false;
    }
}
