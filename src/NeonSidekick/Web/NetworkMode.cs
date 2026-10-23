using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>Where <c>web_fetch</c> may reach (<see cref="LanPolicy"/>, <see cref="WebFetcher.FetchAsync"/>).</summary>
public enum NetworkReach
{
    /// <summary>Public addresses alone: this machine and the local network are refused.</summary>
    Internet,

    /// <summary>This machine and the local network alone: the internet is refused.</summary>
    LocalAreaNetwork,

    /// <summary>Every address.</summary>
    Both,
}

/// <summary>
/// The setting <c>Web browser network mode</c> (2026-09-18, in place of the on/off <c>Browser allow LAN</c>):
/// the three words the operator picks from (<c>internet</c>, <c>lan</c>, <c>both</c>)
/// and their mapping to <see cref="NetworkReach"/>, the <see cref="BrowserMode"/> shape.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value
/// that is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class NetworkMode
{
    /// <summary>The internet alone. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "internet";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "internet", "lan", "both" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["local_area_network"] = "lan",
    };

    private const string Category = "Web";

    /// <summary>Trims and ignores case; false (and <see cref="NetworkReach.Internet"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out NetworkReach reach)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "internet": reach = NetworkReach.Internet; return true;
            case "lan": reach = NetworkReach.LocalAreaNetwork; return true;
            case "both": reach = NetworkReach.Both; return true;
            default: reach = NetworkReach.Internet; return false;
        }
    }

    /// <summary>The saved word for <paramref name="reach"/>.</summary>
    public static string Name(NetworkReach reach) => reach switch
    {
        NetworkReach.LocalAreaNetwork => "lan",
        NetworkReach.Both => "both",
        _ => "internet",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "internet" => "public addresses alone; this machine and the local network refused",
        "lan" => "this machine and the local network alone; the internet refused",
        "both" => "every address",
        _ => "",
    };

    /// <summary>The reach in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_warned = new(StringComparer.Ordinal);

    public static NetworkReach Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebBrowserNetworkMode, out var reach))
        {
            return reach;
        }

        // Once a run per word (2026-10-23, the admin review's U15: every host of every question warned again).
        if (s_warned.TryAdd(effective.WebBrowserNetworkMode ?? "", 0))
        {
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.WebBrowserNetworkMode)}='{effective.WebBrowserNetworkMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }
        TryParse(Default, out reach);
        return reach;
    }
}
