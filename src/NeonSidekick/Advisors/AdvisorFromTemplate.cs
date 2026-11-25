using NeonSidekick.Llm;
using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>What making an advisor from a template came to: the entry when one was added, the notice, and an error when something failed.</summary>
public sealed record AdvisorCreation(AdvisorEntry? Entry, string? Notice, string? Error);

/// <summary>
/// A new advisor from a template (2026-11-22, the user's ask), one way for <c>/advisor new &lt;template&gt; [&lt;name&gt;]</c> (the screen
/// and headless) and the Advisors pane's <c>+ New advisor from a template…</c>: a new profile named <c>name</c> made with
/// <see cref="Profiles.NewProfileSeed"/> (the loaded profile's saved settings), its advisor list cleared (an advisor never consults
/// others), the template's avatar, no sidekick files (an advisor is a role, not a copy of you: no memories), the template's persona
/// written, and the entry on the loaded profile's <see cref="AppSettingsData.AdvisorList"/>. No profile switch. A taken name is
/// refused before anything is written (<see cref="Profiles.Create"/> would write over that profile's settings); a persona write
/// that fails after the profile exists still adds the entry, so the profile is reachable, and nothing is deleted.
/// </summary>
public static class AdvisorFromTemplate
{
    /// <summary>
    /// Why <paramref name="name"/> can't name a new advisor, or null: not a profile name, a temporary one, or one a profile has
    /// (in its own case, saying when it is an advisor already).
    /// </summary>
    public static string? NameProblem(string home, IReadOnlyList<AdvisorEntry>? saved, string template, string? name)
    {
        string typed = (name ?? "").Trim();
        if (Profiles.IsTemporary(typed))
        {
            return AdvisorText.TemporaryNameError;
        }

        if (!Profiles.IsValidName(typed))
        {
            return App.ChatScreen.ProfileNameError;
        }

        return Profiles.Resolve(home, typed) is { } existing
            ? AdvisorText.TakenError(existing, template, AdvisorRoster.Contains(saved, existing))
            : null;
    }

    /// <summary>
    /// Makes the advisor <paramref name="name"/> from <paramref name="template"/> with <paramref name="role"/> and
    /// <paramref name="description"/> (the template's when null), its entry added to <paramref name="settings"/>' list.
    /// <paramref name="advisorsOn"/> is <c>Advisors enabled</c> as in force (the saved value when null), for the notice.
    /// <paramref name="writePersona"/> is the persona's writer, a test's seam (the profile's <see cref="PersonaFile"/> by default).
    /// </summary>
    public static AdvisorCreation Create(
        AppSettings settings, AdvisorTemplate template, string name, string? role = null, string? description = null,
        bool? advisorsOn = null, Action<string, string>? writePersona = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(template);
        string home = settings.StorageDirectory;
        string typed = (name ?? "").Trim();
        if (NameProblem(home, settings.Current.AdvisorList, template.Id, typed) is { } problem)
        {
            return new AdvisorCreation(null, null, problem);
        }

        var seed = Profiles.NewProfileSeed(settings.Current);
        seed.AdvisorList = null;
        if (template.Avatar.Length > 0)
        {
            seed.ModelAvatar = template.Avatar;
        }

        try
        {
            // Taken meanwhile by another creator (2026-11-24, review #33): said so, and no persona written into its profile.
            if (!Profiles.Create(home, typed, seed))
            {
                return new AdvisorCreation(null, null, AdvisorText.TakenError(typed, template.Id, AdvisorRoster.Contains(settings.Current.AdvisorList, typed)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AdvisorCreation(null, null, AdvisorText.CreateFailedError(typed, ex.Message));
        }

        string? error = null;
        try
        {
            (writePersona ?? WritePersona)(Profiles.Directory(home, typed), template.Persona);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = AdvisorText.PersonaFailedError(typed, ex.Message);
        }

        var entry = new AdvisorEntry(typed, role ?? template.Role, description ?? template.Description);
        settings.Update(d => d.AdvisorList = AdvisorRoster.Add(d.AdvisorList, entry));
        entry = AdvisorRoster.Entries(settings.Current.AdvisorList).First(e => AdvisorRoster.SameProfile(e.Profile, typed));
        return new AdvisorCreation(entry, error is null ? AdvisorText.CreatedNotice(entry, advisorsOn ?? settings.Current.Advisors) : null, error);
    }

    private static void WritePersona(string profileDirectory, string persona) => new PersonaFile(profileDirectory).WriteText(persona);
}
