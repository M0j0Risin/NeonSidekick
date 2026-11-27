using System.Globalization;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Advisors;

/// <summary>
/// What <see cref="AdvisorRoster.Pick"/> found (2026-11-27): the advisor to ask, else the off one the words name, else whether every advisor
/// is off, with the active candidates for the "names no single advisor" error.
/// </summary>
public readonly record struct AdvisorPick(AdvisorEntry? Found, IReadOnlyList<AdvisorEntry> Candidates, AdvisorEntry? OffOne, bool AllOff);

/// <summary>
/// The profile advisors' list (2026-10-10, <see cref="AppSettingsData.AdvisorList"/>): read clean, changed by copy, and an advisor
/// found by the words a call or <c>/advisor</c> names it with. One entry per profile, compared without case as profile names are;
/// a blank profile is no entry. Pure.
/// </summary>
public static class AdvisorRoster
{
    /// <summary>
    /// The saved list as it counts: trimmed, blank profiles dropped, the first of a profile kept; sorted by profile name, case ignored
    /// (2026-11-23, the user's ask: a removed-and-re-added advisor landed last), so every list shows one order.
    /// </summary>
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

            entries.Add(new AdvisorEntry(profile, (entry!.Role ?? "").Trim(), CleanDescription(entry.Description), entry.Off));
        }

        return entries.OrderBy(e => e.Profile, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The advisors in service (2026-11-27): <see cref="Entries"/> without the ones switched off.</summary>
    public static IReadOnlyList<AdvisorEntry> Active(IReadOnlyList<AdvisorEntry>? saved) => Entries(saved).Where(e => !e.Off).ToList();

    /// <summary>The advisors switched off, A to Z (2026-11-27).</summary>
    public static IReadOnlyList<AdvisorEntry> OffEntries(IReadOnlyList<AdvisorEntry>? saved) => Entries(saved).Where(e => e.Off).ToList();

    /// <summary>
    /// The off advisor <paramref name="words"/> names, by <see cref="Find"/>'s rules run over the off ones alone (2026-11-27), for a refusal
    /// that names it; asked only after <see cref="Find"/> over <see cref="Active"/> found nothing, so an active advisor's partial role match
    /// wins over an off one's (an off advisor named whole is <see cref="Pick"/>'s first step).
    /// </summary>
    public static AdvisorEntry? Off(IReadOnlyList<AdvisorEntry>? saved, string? words) => Find(OffEntries(saved), words).Found;

    /// <summary>The list in the order every list shows it (2026-11-27): the advisors in service A to Z, then the ones switched off A to Z.</summary>
    public static IReadOnlyList<AdvisorEntry> Shown(IReadOnlyList<AdvisorEntry>? saved) => [.. Active(saved), .. OffEntries(saved)];

    /// <summary>
    /// The advisor a call or <c>/advisor</c> names (2026-11-27, the off switch), in one order for all three askers. First the words matched
    /// whole over every advisor (2026-11-27, the last-3 review's #5): an off advisor named by its profile name, or the one holding the role
    /// whole, is <see cref="AdvisorPick.OffOne"/>, never an active one whose role merely holds the words. Then <see cref="Find"/> over the ones
    /// in service, then an off one the words name in part, then <see cref="AdvisorPick.AllOff"/> when none is in service; otherwise the active
    /// candidates. Callers say "no advisors" first when the list is empty.
    /// </summary>
    public static AdvisorPick Pick(IReadOnlyList<AdvisorEntry>? saved, string? words)
    {
        var active = Active(saved);
        string wanted = (words ?? "").Trim();
        if (wanted.Length > 0)
        {
            var all = Entries(saved);
            var byRole = all.Where(e => string.Equals(e.Role, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            var named = all.FirstOrDefault(e => SameProfile(e.Profile, wanted)) ?? (byRole.Count == 1 ? byRole[0] : null);
            if (named is { Off: true })
            {
                return new AdvisorPick(null, Find(active, words).Candidates, named, false);
            }
        }

        var (found, candidates) = Find(active, words);
        if (found is not null)
        {
            return new AdvisorPick(found, candidates, null, false);
        }

        return Off(saved, words) is { } off ? new AdvisorPick(null, candidates, off, false) : new AdvisorPick(null, candidates, null, active.Count == 0);
    }

    /// <summary><paramref name="saved"/> with <paramref name="profile"/> switched off or on (2026-11-27); unchanged when it is not there.</summary>
    public static List<AdvisorEntry> WithOff(IReadOnlyList<AdvisorEntry>? saved, string profile, bool off) =>
        Entries(saved).Select(e => SameProfile(e.Profile, profile) ? e with { Off = off } : e).ToList();

    /// <summary>
    /// The most characters an advisor's description keeps (2026-11-22, the advisor templates): it lands in <c>neon_advisor</c>'s description
    /// for every advisor, so about fifteen words each. Pinned.
    /// </summary>
    public const int DescriptionMax = 120;

    /// <summary>A description as an entry keeps it: one line, trimmed, cut at <see cref="DescriptionMax"/>; "" for none. Pure.</summary>
    public static string CleanDescription(string? description)
    {
        string text = AdvisorText.Flat(description ?? "");
        // Cut by TextCut.Safe (2026-11-23, review #21): an index cut could split an emoji's surrogate pair.
        return text.Length <= DescriptionMax ? text : Files.TextCut.Safe(text, DescriptionMax - 1).TrimEnd() + "…";
    }

    /// <summary>Whether two profile names are one profile (case ignored, as <see cref="Profiles.Resolve"/> does).</summary>
    public static bool SameProfile(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="saved"/> holds <paramref name="profile"/>.</summary>
    public static bool Contains(IReadOnlyList<AdvisorEntry>? saved, string profile) => Entries(saved).Any(e => SameProfile(e.Profile, profile));

    /// <summary>
    /// <paramref name="saved"/> with <paramref name="entry"/> last (shown by name all the same, <see cref="Entries"/>); a profile already there (a <c>(missing)</c> one whose name a new advisor
    /// takes again) keeps its place and takes the new role and description, and is on again (2026-11-27: adding is a deliberate act).
    /// </summary>
    public static List<AdvisorEntry> Add(IReadOnlyList<AdvisorEntry>? saved, AdvisorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entries = Entries(saved).ToList();
        var clean = new AdvisorEntry(entry.Profile.Trim(), (entry.Role ?? "").Trim(), CleanDescription(entry.Description));
        int at = entries.FindIndex(e => SameProfile(e.Profile, clean.Profile));
        if (at >= 0)
        {
            entries[at] = entries[at] with { Role = clean.Role, Description = clean.Description, Off = false };
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

    /// <summary><paramref name="saved"/> with <paramref name="profile"/>'s description <paramref name="description"/> (cleaned); unchanged when it is not there.</summary>
    public static List<AdvisorEntry> WithDescription(IReadOnlyList<AdvisorEntry>? saved, string profile, string? description) =>
        Entries(saved).Select(e => SameProfile(e.Profile, profile) ? e with { Description = CleanDescription(description) } : e).ToList();

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

        // Several holding the role whole are a choice, as several holding it in part are (2026-11-24, the last-7 review's #16), never the first.
        var byRole = entries.Where(e => string.Equals(e.Role, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byRole.Count > 0)
        {
            return byRole.Count == 1 ? (byRole[0], byRole) : (null, byRole);
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

    /// <summary>
    /// One advisor as <c>neon_advisor</c>'s description names it (2026-11-22): <see cref="Label"/> and its description after a colon,
    /// <c>dba (database administrator): schema design, query plans…</c>; <see cref="Label"/> alone with no description. Pinned.
    /// </summary>
    public static string ToolLabel(AdvisorEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Description.Length == 0 ? Label(entry) : Label(entry) + ": " + entry.Description;
    }

    /// <summary>
    /// The settings row's value: <c>none</c>, or how many are in service and their labels, cut at <paramref name="width"/>, then
    /// <c> · 1 off</c> when some are switched off (2026-11-27), never cut; <c>2 off</c> alone when every one is. Pinned.
    /// </summary>
    public static string Value(IReadOnlyList<AdvisorEntry>? saved, int width = 70)
    {
        var entries = Entries(saved);
        if (entries.Count == 0)
        {
            return AdvisorText.NoneValue;
        }

        var active = entries.Where(e => !e.Off).ToList();
        int off = entries.Count - active.Count;
        string offPart = AdvisorText.OffCount(off);
        if (active.Count == 0)
        {
            return offPart;
        }

        string tail = off == 0 ? "" : " · " + offPart;
        string text = active.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", active.Select(Label));
        // Cut by cells (2026-11-24, the last-7 review's #56): an emoji or CJK role is two cells a character, and a cut by length could split a pair.
        return TextCells.Cut(text, Math.Max(1, width - TextCells.Width(tail))) + tail;
    }
}
