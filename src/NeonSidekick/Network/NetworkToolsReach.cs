using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>
/// The setting <c>Network tools reach</c> (2026-10-15, the admin-tools plan, the user's call: the network tools get their own, so a ping
/// across the LAN need not open <c>web_fetch</c> to it): <c>same-as-web</c> (the default) follows <c>Web browser network mode</c>
/// (<see cref="NetworkMode.Resolve"/>); <c>internet</c>, <c>lan</c> and <c>both</c> are that setting's words for these tools alone.
/// <see cref="Resolve"/> is the one place the saved word becomes a <see cref="NetworkReach"/>; an unknown word warns and reads as the default.
/// </summary>
public static class NetworkToolsReach
{
    public const string SameAsWeb = "same-as-web";

    /// <summary>The compiled default.</summary>
    public const string Default = SameAsWeb;

    /// <summary>The words in menu order.</summary>
    public static readonly string[] Names = [SameAsWeb, "internet", "lan", "both"];

    /// <summary>The menu hint beside a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        SameAsWeb => "as Web browser network mode says",
        _ => NetworkMode.Describe(name),
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> s_warned = new(StringComparer.Ordinal);

    /// <summary>The reach in force for the network tools under <paramref name="effective"/>.</summary>
    public static NetworkReach Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        string word = effective.NetworkToolsReach?.Trim().ToLowerInvariant() ?? "";
        if (word.Length == 0 || word == SameAsWeb)
        {
            return NetworkMode.Resolve(effective);
        }

        if (NetworkMode.TryParse(word, out var reach))
        {
            return reach;
        }

        // Once a run per word (U15).
        if (s_warned.TryAdd(effective.NetworkToolsReach ?? "", 0))
        {
            DiagnosticLog.Warn(NetworkText.Category, $"{nameof(AppSettingsData.NetworkToolsReach)}='{effective.NetworkToolsReach}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return NetworkMode.Resolve(effective);
    }
}
