using NeonSidekick.Settings;

namespace NeonSidekick.HomeAssistant;

/// <summary>What <see cref="HaPolicy.Judge"/> decided for one service call.</summary>
public enum HaVerdict
{
    /// <summary>It runs.</summary>
    Run,

    /// <summary>It runs only after the user's yes on the pane; nothing to ask (headless) is a no.</summary>
    Ask,

    /// <summary>It does not run: <c>Home Assistant action policy</c> is <c>off</c>.</summary>
    Refuse,
}

/// <summary>
/// Which Home Assistant service calls the model may make (2026-09-28, the user's call: "read + act, ask for risky"): the
/// setting <c>Home Assistant action policy</c> — <see cref="Off"/> (reads only), <see cref="Ask"/> (the default: the
/// services in <c>HomeAssistantSafeServices</c> run, every other asks on the pane first) or <see cref="Allow"/> (every call
/// runs) — the shell's <c>Shell command policy</c> in shape. A read that goes through a service (<see cref="ReadServices"/>,
/// <c>todo.get_items</c>) runs under every policy. <c>/ha</c> is the user's own hand and is never judged. Pure.
/// </summary>
public static class HaPolicy
{
    public const string Off = "off";
    public const string Ask = "ask";
    public const string Allow = "allow";

    /// <summary>The policies in the picker's order.</summary>
    public static readonly string[] Names = [Off, Ask, Allow];

    public const string Default = Ask;

    /// <summary>
    /// The services that run without asking under <see cref="Ask"/>, the setting's default (2026-09-28): the lights, a
    /// scene, the TV's power, volume, source and playback, and the to-do lists — what "dim the den" or "switch the TV to
    /// HDMI 2" needs, and nothing that restarts, presses, sends a remote code or flips a switch or an automation. A
    /// <c>domain.*</c> entry takes every service of the domain.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSafeServices =
    [
        "light.*",
        "scene.turn_on",
        "media_player.turn_on", "media_player.turn_off",
        "media_player.volume_set", "media_player.volume_up", "media_player.volume_down", "media_player.volume_mute",
        "media_player.select_source",
        "media_player.media_play", "media_player.media_pause", "media_player.media_play_pause", "media_player.media_stop",
        "media_player.media_next_track", "media_player.media_previous_track",
        "todo.add_item", "todo.update_item", "todo.remove_item",
    ];

    /// <summary>The services that only read, which run under every policy.</summary>
    public static readonly IReadOnlySet<string> ReadServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "todo.get_items", "weather.get_forecasts", "calendar.get_events",
    };

    /// <summary>The policy in force: the setting in lower case, anything unknown read as <see cref="Default"/>.</summary>
    public static string Resolve(string? policy)
    {
        string name = policy?.Trim().ToLowerInvariant() ?? "";
        return Names.Contains(name, StringComparer.Ordinal) ? name : Default;
    }

    /// <summary>A one-line description for the picker. Pinned.</summary>
    public static string Describe(string policy) => Resolve(policy) switch
    {
        Off => "the model reads states only; nothing is switched",
        Allow => "every service call runs without asking",
        _ => "lights, scenes, TV and to-do lists run; anything else asks first",
    };

    /// <summary>Whether <paramref name="domain"/>.<paramref name="service"/> is in <paramref name="safe"/> (an entry <c>domain.*</c> takes the domain). Any case.</summary>
    public static bool IsSafe(string domain, string service, IReadOnlyList<string>? safe)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(service);
        string full = domain.Trim() + "." + service.Trim();
        foreach (string entry in safe ?? DefaultSafeServices)
        {
            string e = entry.Trim();
            if (string.Equals(e, full, StringComparison.OrdinalIgnoreCase)
                || (e.EndsWith(".*", StringComparison.Ordinal) && string.Equals(e[..^2], domain.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The verdict for <paramref name="domain"/>.<paramref name="service"/> as <paramref name="effective"/> stands.</summary>
    public static HaVerdict Judge(AppSettingsData effective, string domain, string service)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (ReadServices.Contains(domain.Trim() + "." + service.Trim()))
        {
            return HaVerdict.Run;
        }

        return Resolve(effective.HomeAssistantActionPolicy) switch
        {
            Off => HaVerdict.Refuse,
            Allow => HaVerdict.Run,
            _ => IsSafe(domain, service, effective.HomeAssistantSafeServices) ? HaVerdict.Run : HaVerdict.Ask,
        };
    }
}
