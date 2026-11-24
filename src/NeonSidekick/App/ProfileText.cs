using System.Globalization;

namespace NeonSidekick.App;

/// <summary>
/// The words of <c>/profile push|pull homeassistant, toolbar, perfbar, statusbar and barorder</c> (2026-10-26, the user's ask; moved here from
/// <c>ChatScreen</c> by the review's R27, the older sections' lines staying where they are): the questions, the replaced clauses and the done
/// lines. One place, so the tests can pin them.
/// </summary>
public static class ProfileText
{
    /// <summary>The notice after <c>/profile add &lt;name&gt; --fresh</c> (2026-11-23, the user's ask): the compiled defaults, nothing copied. Pinned.</summary>
    public static string ProfileCreatedFreshNotice(string name) => $"({NoticeGlyphs.Profile}created profile \"{name}\" with the default settings)";

    /// <summary><c>/profile add &lt;name&gt; --fresh</c> (2026-11-23, the user's ask): the flag, and its note on the argument list. Pinned.</summary>
    public const string FreshFlag = "--fresh";
    public const string FreshNote = "start from the default settings, copying nothing";

    /// <summary>
    /// A new profile the disk refused (2026-11-24, the last-7 review's #36): the picker's add row and <c>/profile add</c>, the half-made one taken
    /// back. Worded as <c>AdvisorText.CreateFailedError</c>. Pinned.
    /// </summary>
    public static string CreateFailedError(string name, string detail) =>
        $"Could not make profile {name}: {(detail ?? "").ReplaceLineEndings(" ").Trim()}";

    /// <summary>
    /// What a bar section's copy replaces, its verb included: <c>toolbar items, order and sides are replaced</c>, or the performance bar's, the
    /// Status bar's or the bar order's. Pinned through <see cref="BarPrompt"/>.
    /// </summary>
    private static string BarReplaced(ProfileSection section) => section switch
    {
        ProfileSection.Toolbar => "toolbar items, order and sides are replaced",
        ProfileSection.PerfBar => "performance bar meters, order, sides and look are replaced",
        ProfileSection.StatusBar => "Status bar sections, order, sides and Hints bar are replaced",
        ProfileSection.BarOrder => "bar order is replaced",
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "Not a bar section."),
    };

    /// <summary>
    /// The question before a bar section's copy: <c>Copy this profile's toolbar setup into "work"? Its toolbar items, order and sides are
    /// replaced.</c>, pulled <c>Copy "work"'s toolbar setup into this profile? This profile's toolbar items, order and sides are replaced.</c>
    /// </summary>
    public static string BarPrompt(ProfileSection section, string profile, bool pull)
    {
        string subject = ChatScreen.ProfileSectionSubject(section);
        return pull
            ? $"Copy \"{profile}\"'s {subject} into this profile? This profile's {BarReplaced(section)}."
            : $"Copy this profile's {subject} into \"{profile}\"? Its {BarReplaced(section)}.";
    }

    /// <summary>A bar section's copy done: <c>(copied the toolbar setup into "work")</c>, a pull <c>from "work"</c>.</summary>
    public static string BarCopiedNotice(ProfileSection section, string profile, bool pull) =>
        $"(copied the {ChatScreen.ProfileSectionSubject(section)} {(pull ? "from" : "into")} \"{profile}\")";

    /// <summary>
    /// The question before a <c>homeassistant</c> copy, in the rows' own words: <c>Copy this profile's Home Assistant settings and home bar
    /// into "work"? Its Home Assistant tools switch, URL, API key, … are replaced.</c>; pulled, the mirror. When the source has no URL or no
    /// key, what the mirror does, as <c>ChatScreen.ProfileKeysPrompt</c> says it: <c> "work"'s Home Assistant URL and API key are cleared: none
    /// here.</c>, pulled <c> This profile's … cleared: none in "work".</c>, so a mirror that wipes is never a surprise.
    /// </summary>
    public static string HomeAssistantPrompt(string profile, bool urlSet, bool keySet, bool pull)
    {
        const string Replaced = "Home Assistant tools switch, URL, API key, action policy, safe services, Assist agent and timeout, and its home bar's items, sides, names and emoji are replaced.";
        string question = pull
            ? $"Copy \"{profile}\"'s Home Assistant settings and home bar into this profile? This profile's {Replaced}"
            : $"Copy this profile's Home Assistant settings and home bar into \"{profile}\"? Its {Replaced}";
        string? cleared = (urlSet, keySet) switch
        {
            (false, false) => "Home Assistant URL and API key are",
            (false, true) => "Home Assistant URL is",
            (true, false) => ChatScreen.HomeAssistantKeyName + " is",
            _ => null,
        };
        if (cleared is null)
        {
            return question;
        }

        return pull
            ? $"{question} This profile's {cleared} cleared: none in \"{profile}\"."
            : $"{question} \"{profile}\"'s {cleared} cleared: none here.";
    }

    /// <summary>
    /// A <c>homeassistant</c> copy done: <c>(copied the Home Assistant settings into "work": Home Assistant URL http://…, 6 home bar items)</c>,
    /// the URL as the settings pane shows it (<c>(not set)</c> when empty) and without a user name or password (<see cref="WithoutUserInfo"/>,
    /// the review's R22), the chips counted without their dividers; never the key. A pull says from.
    /// </summary>
    public static string HomeAssistantCopiedNotice(string profile, string url, int items, bool pull) =>
        $"(copied the Home Assistant settings {(pull ? "from" : "into")} \"{profile}\": Home Assistant URL {WithoutUserInfo(url)}, {Counted(items, "home bar item", "home bar items")})";

    /// <summary>
    /// <paramref name="url"/> with any user name and password taken out (<c>http://me:secret@ha:8123/</c> reads <c>http://ha:8123/</c>), as
    /// <c>MarkOfTheWeb</c> writes a download's address (2026-10-26, the review's R22: a done line is a transcript line, kept in the session).
    /// Anything that isn't an absolute address with user info, a label among them, comes back as it is. Pure.
    /// </summary>
    public static string WithoutUserInfo(string url) => Files.SafeText.WithoutUserInfo(url);

    /// <summary>
    /// The settings another writer changed in the loaded profile's file, taken (2026-10-26, the review's R24: a push from another copy of the
    /// app, a reset or a copy made there, a hand edit): <c>(🪪 profile "work" changed outside this window; 2 settings taken)</c>.
    /// </summary>
    public static string ChangedElsewhereNotice(string profile, int changed) =>
        $"({NoticeGlyphs.Profile}profile \"{profile}\" changed outside this window; {Counted(changed, "setting", "settings")} taken)";

    /// <summary>The profile picker's top row (2026-11-24, the user's ask): a new profile, named, then copied or fresh. Pinned.</summary>
    public const string AddProfileRow = "+ Add a profile…";

    /// <summary>The name prompt's title on the add row. Pinned.</summary>
    public const string AddProfileNameTitle = "Name the new profile";

    /// <summary>The seed page's first row: the loaded profile's saved settings, as <c>/profile add</c> copies them. Pinned.</summary>
    public static string CopySettingsRow(string loaded) => $"Copy {loaded}'s settings";

    /// <summary>The seed page's second row: the compiled defaults, as <c>/profile add --fresh</c>. Pinned.</summary>
    public const string StartFreshRow = "Start fresh (defaults)";

    private static string Counted(int count, string one, string many) =>
        count == 1 ? "1 " + one : $"{count.ToString(CultureInfo.InvariantCulture)} {many}";
}
