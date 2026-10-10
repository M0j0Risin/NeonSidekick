using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /settings' strip: the Servers and Voice groups (2026-10-10) ─────────────

internal sealed partial class SettingsMenu
{
    /// <summary>The group tab holding the six server tabs (2026-10-10, the user's pick: the LLM, Embedded, Docker, Anthropic and OpenAI tabs, <c>/profile push|pull servers</c>' five; six later that day, the LLM tab split into Common and Http, the user's call). Pinned.</summary>
    public const string ServersTabTitle = "Servers";

    /// <summary>The group tab holding the TTS and STT tabs (2026-10-10, the user's pick). Pinned.</summary>
    public const string VoiceTabTitle = "Voice";

    /// <summary>
    /// The strip <c>/settings</c> shows (2026-10-10, the user's pick, <c>/tools</c>' Database and Media shape: the strip had grown to ten):
    /// General, Servers, Voice, Sessions, Botchat, and Advisors after it (later that day, the profile advisors). <see cref="TabTitles"/> and <see cref="TabFields"/> stay the leaf tabs (twelve since the LLM tab's split, later that day), in the order the
    /// arrows walk them (<see cref="StripTabs"/>), each a sub-tab of its group on the second strip. Pinned.
    /// </summary>
    /// <remarks>Spelled out, never read off <see cref="TabTitles"/>: a static of another part of the class may not be set yet.</remarks>
    public static readonly IReadOnlyList<string> StripTitles = ["General", ServersTabTitle, VoiceTabTitle, "Sessions", "Botchat", AdvisorsTabTitle];

    /// <summary>The tabs each of <see cref="StripTitles"/> holds, in <see cref="SettingsTab"/> order: one for a plain tab, more for a group. Pinned.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsTab>> StripTabs =
    [
        [SettingsTab.General],
        [SettingsTab.Common, SettingsTab.Http, SettingsTab.Embedded, SettingsTab.Docker, SettingsTab.Anthropic, SettingsTab.OpenAI],
        [SettingsTab.Tts, SettingsTab.Stt],
        [SettingsTab.Sessions],
        [SettingsTab.BotChat],
        [SettingsTab.Advisors],
    ];

    /// <summary>Whether strip tab <paramref name="strip"/> (a <see cref="StripTitles"/> index) is a group: Servers and Voice. Pure.</summary>
    public static bool IsGroup(int strip) => strip >= 0 && strip < StripTabs.Count && StripTabs[strip].Count > 1;

    /// <summary>Where settings tab <paramref name="tab"/> sits on the strip: its group's index and its own among the group's (0 on a plain tab). Pure.</summary>
    public static (int Strip, int Sub) PlaceOf(SettingsTab tab)
    {
        for (int s = 0; s < StripTabs.Count; s++)
        {
            int sub = IndexOf(StripTabs[s], tab);
            if (sub >= 0)
            {
                return (s, sub);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(tab));
    }

    /// <summary>The settings tab at strip tab <paramref name="strip"/>, sub-tab <paramref name="sub"/> (clamped; −1, a plain tab's, is its one). Pure.</summary>
    public static SettingsTab TabAt(int strip, int sub)
    {
        var tabs = StripTabs[Math.Clamp(strip, 0, StripTabs.Count - 1)];
        return tabs[Math.Clamp(sub, 0, tabs.Count - 1)];
    }

    /// <summary>The settings tab a pane page of <c>/settings</c> shows: its strip tab and the sub-tab on show there.</summary>
    private static int TabOf(MenuPage page) => (int)TabAt(page.Tab, page.Sub);

    /// <summary>
    /// The picker's root under a group's tab (2026-10-10, <c>/tools</c>' <c>GroupCrumb</c>): <c>⚙️ Settings › Servers</c> for a row of Common,
    /// Http, Embedded, Docker, Anthropic or OpenAI, <c>⚙️ Settings › Voice</c> for TTS and STT; the title alone elsewhere. Pinned.
    /// </summary>
    public static string GroupCrumb(SettingsTab tab) => PlaceOf(tab) is var (strip, _) && IsGroup(strip) ? Title + " › " + StripTitles[strip] : Title;

    /// <summary>The sub-tab on show on each group the last pick left (2026-10-10, <see cref="MenuPane.ShownSubs"/>), so a group came back to finds it.</summary>
    private int[] _stripSubs = new int[StripTabs.Count];

    private static int IndexOf(IReadOnlyList<SettingsTab> tabs, SettingsTab tab)
    {
        for (int i = 0; i < tabs.Count; i++)
        {
            if (tabs[i] == tab)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary><paramref name="tabs"/> with group <paramref name="strip"/> showing sub-tab <paramref name="sub"/>; the same list when it holds none.</summary>
    private static IReadOnlyList<MenuTab> WithSub(IReadOnlyList<MenuTab> tabs, int strip, int sub)
    {
        if (sub < 0 || tabs[strip].SubTabs is not { Count: > 0 })
        {
            return tabs;
        }

        var held = tabs.ToArray();
        held[strip] = held[strip] with { Sub = sub };
        return held;
    }
}
