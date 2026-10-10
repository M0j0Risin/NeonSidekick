using System.Globalization;
using NeonSidekick.Settings;

namespace NeonSidekick.Advisors;

/// <summary>
/// The profile advisors' list (2026-10-10, <see cref="AppSettingsData.AdvisorList"/>): read clean, changed by copy, and an advisor
/// found by the words a call or <c>/advisor</c> names it with. One entry per profile, compared without case as profile names are;
/// a blank profile is no entry. Pure.
/// </summary>
public static class AdvisorRoster
{
    /// <summary>The saved list as it counts: trimmed, blank profiles dropped, the first of a profile kept.</summary>
    public static IReadOnlyList<AdvisorEntry> Entries(IReadOnlyList<AdvisorEntry>? saved)
    {
        var entries = new List<AdvisorEntry>();
        foreach (var entry in saved ?? [])
        {
            string profile = (entry?.Profile ?? "").Trim();
            if (profile.Length == 0 || entries.Any(e => SameProfile(e.Profile, profile)))
            {
                continue;
            }

            entries.Add(new AdvisorEntry(profile, (entry!.Role ?? "").Trim()));
        }

        return entries;
    }

    /// <summary>Whether two profile names are one profile (case ignored, as <see cref="Profiles.Resolve"/> does).</summary>
    public static bool SameProfile(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="saved"/> holds <paramref name="profile"/>.</summary>
    public static bool Contains(IReadOnlyList<AdvisorEntry>? saved, string profile) => Entries(saved).Any(e => SameProfile(e.Profile, profile));

    /// <summary><paramref name="saved"/> with <paramref name="entry"/> last; a profile already there keeps its place and takes the new role.</summary>
    public static List<AdvisorEntry> Add(IReadOnlyList<AdvisorEntry>? saved, AdvisorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entries = Entries(saved).ToList();
        var clean = new AdvisorEntry(entry.Profile.Trim(), (entry.Role ?? "").Trim());
        int at = entries.FindIndex(e => SameProfile(e.Profile, clean.Profile));
        if (at >= 0)
        {
            entries[at] = entries[at] with { Role = clean.Role };
        }
        else
        {
            entries.Add(clean);
        }

        return entries;
    }

    /// <summary><paramref name="saved"/> with <paramref name="profile"/>'s role <paramref name="role"/>; unchanged when it is not there.</summary>
    public static List<AdvisorEntry> WithRole(IReadOnlyList<AdvisorEntry>? saved, string profile, string role) =>
        Entries(saved).Select(e => SameProfile(e.Profile, profile) ? e with { Role = (role ?? "").Trim() } : e).ToList();

    /// <summary><paramref name="saved"/> without <paramref name="profile"/>; null when nothing is left (the file keeps no empty list).</summary>
    public static List<AdvisorEntry>? Without(IReadOnlyList<AdvisorEntry>? saved, string profile)
    {
        var left = Entries(saved).Where(e => !SameProfile(e.Profile, profile)).ToList();
        return left.Count == 0 ? null : left;
    }

    /// <summary>
    /// The advisor <paramref name="words"/> names among <paramref name="entries"/>: a profile name first, then a whole role, then the one
    /// role holding the words (case ignored throughout). Null with the candidates when none or several fit.
    /// </summary>
    public static (AdvisorEntry? Found, IReadOnlyList<AdvisorEntry> Candidates) Find(IReadOnlyList<AdvisorEntry> entries, string? words)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string wanted = (words ?? "").Trim();
        if (wanted.Length == 0)
        {
            return (null, entries);
        }

        if (entries.FirstOrDefault(e => SameProfile(e.Profile, wanted)) is { } byName)
        {
            return (byName, [byName]);
        }

        if (entries.FirstOrDefault(e => string.Equals(e.Role, wanted, StringComparison.OrdinalIgnoreCase)) is { } byRole)
        {
            return (byRole, [byRole]);
        }

        var holding = entries.Where(e => e.Role.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return holding.Count == 1 ? (holding[0], holding) : (null, holding.Count > 1 ? holding : entries);
    }

    /// <summary>One advisor as the menus and the tool's description name it: <c>dba (database architect)</c>, or the name alone with no role.</summary>
    public static string Label(AdvisorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Role.Length == 0 ? entry.Profile : entry.Profile + " (" + entry.Role + ")";
    }

    /// <summary>The settings row's value: <c>none</c>, or how many and their labels, cut at <paramref name="width"/>. Pinned.</summary>
    public static string Value(IReadOnlyList<AdvisorEntry>? saved, int width = 70)
    {
        var entries = Entries(saved);
        if (entries.Count == 0)
        {
            return "none";
        }

        string text = entries.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", entries.Select(Label));
        return text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";
    }
}
