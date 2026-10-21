using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /settings' strip: the Client, Servers and Voice groups (2026-10-10) ─────

internal sealed partial class SettingsMenu
{
    /// <summary>
    /// The group tab holding the Chat, Sessions and Botchat tabs (2026-10-10, the user's pick after a naming round: "General" said
    /// nothing; this side of the connection, as <see cref="ServersTabTitle"/> is the other; Chat the old General, Sessions and Botchat
    /// off the root strip beside it, in that order; Web UI after Botchat since 2026-10-14, the user's ask). Pinned.
    /// </summary>
    public const string ClientTabTitle = "Client";

    /// <summary>The group tab holding the six server tabs (2026-10-10, the user's pick: the LLM, Embedded, Docker, Anthropic and OpenAI tabs, <c>/profile push|pull servers</c>' five; six later that day, the LLM tab split into Common and Http, the user's call). Pinned.</summary>
    public const string ServersTabTitle = "Servers";

    /// <summary>The group tab holding the TTS and STT tabs (2026-10-10, the user's pick). Pinned.</summary>
    public const string VoiceTabTitle = "Voice";

    /// <summary>
    /// The strip <c>/settings</c> shows (2026-10-10, the user's pick, <c>/tools</c>' Database and Media shape: the strip had grown to ten):
    /// General, Servers, Voice, Sessions, Botchat, and Advisors after it (later that day, the profile advisors); Client, Servers, Voice, Advisors
    /// since later still (the user's call: General renamed Chat and grouped with Sessions and Botchat under <see cref="ClientTabTitle"/>);
    /// Client, Servers, Advisors, Voice later still (the user's order: Advisors before Voice); Client, Servers, Advisors, Automation, Voice since
    /// 2026-10-21 (the user's ask: plan mode's gap review, the Ralph loop and code audits off Servers › Common, a plain tab of their own).
    /// <see cref="TabTitles"/> and <see cref="TabFields"/> stay the leaf tabs (twelve since the LLM tab's split, later that day), in the order the
    /// arrows walk them (<see cref="StripTabs"/>), each a sub-tab of its group on the second strip. Pinned.
    /// </summary>
    /// <remarks>Spelled out, never read off <see cref="TabTitles"/>: a static of another part of the class may not be set yet.</remarks>
    public static readonly IReadOnlyList<string> StripTitles = [ClientTabTitle, ServersTabTitle, AdvisorsTabTitle, AutomationTabTitle, VoiceTabTitle];

    /// <summary>The tabs each of <see cref="StripTitles"/> holds, in <see cref="SettingsTab"/> order (the enum follows the walk): one for a plain tab, more for a group. Pinned.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<SettingsTab>> StripTabs =
    [
        [SettingsTab.Chat, SettingsTab.Sessions, SettingsTab.BotChat, SettingsTab.WebUi],
        [SettingsTab.Common, SettingsTab.Http, SettingsTab.Embedded, SettingsTab.Docker, SettingsTab.Anthropic, SettingsTab.OpenAI],
        [SettingsTab.Advisors],
        [SettingsTab.Automation],
        [SettingsTab.Tts, SettingsTab.Stt],
    ];

    /// <summary>Whether strip tab <paramref name="strip"/> (a <see cref="StripTitles"/> index) is a group: Client, Servers and Voice. Pure.</summary>
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
    /// The picker's root under a group's tab (2026-10-10, <c>/tools</c>' <c>GroupCrumb</c>): <c>⚙️ Settings › Client</c> for a row of Chat,
    /// Sessions, Botchat or Web UI (2026-10-14), <c>⚙️ Settings › Servers</c> for a row of Common, Http, Embedded, Docker, Anthropic or OpenAI,
    /// <c>⚙️ Settings › Voice</c> for TTS and STT; the title alone elsewhere (Advisors, Automation). Pinned.
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
