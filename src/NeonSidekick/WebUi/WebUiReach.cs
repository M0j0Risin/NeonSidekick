using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.WebUi;

/// <summary>Who may reach the Web UI (<see cref="WebUiReach"/>).</summary>
public enum WebUiAudience
{
    /// <summary>This machine alone: the server listens on loopback.</summary>
    Local,

    /// <summary>The home network: every interface, a private address's browsers only (<see cref="Web.LanPolicy.IsPrivate"/>).</summary>
    LocalAreaNetwork,
}

/// <summary>
/// The setting <c>Web UI reach</c> (2026-10-14, the user's pick: the home network with a token): the two words the operator picks
/// from (<c>local</c>, <c>lan</c>) and their mapping to <see cref="WebUiAudience"/>, <see cref="Web.NetworkMode"/>'s shape.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that is neither falls back to
/// <see cref="Default"/> with a warning.
/// </summary>
public static class WebUiReach
{
    /// <summary>The home network. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "lan";

    /// <summary>The words in menu order.</summary>
    public static readonly string[] Names = { "local", "lan" };

    private const string Category = "WebUi";

    /// <summary>Trims and ignores case; false (and <see cref="WebUiAudience.LocalAreaNetwork"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out WebUiAudience audience)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "local": audience = WebUiAudience.Local; return true;
            case "lan": audience = WebUiAudience.LocalAreaNetwork; return true;
            default: audience = WebUiAudience.LocalAreaNetwork; return false;
        }
    }

    /// <summary>The saved word for <paramref name="audience"/>.</summary>
    public static string Name(WebUiAudience audience) => audience == WebUiAudience.Local ? "local" : "lan";

    /// <summary>The menu hint next to a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "local" => "localhost only",                         // the user's wording, 2026-10-14
        "lan" => "your home network and your tailnet",       // the user's wording, 2026-10-14 (Tailscale's tab, /webui)
        _ => "",
    };

    /// <summary>The reach in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static WebUiAudience Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebUiReach, out var audience))
        {
            return audience;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.WebUiReach)}='{effective.WebUiReach}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out audience);
        return audience;
    }
}
