using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>What a bot check the web tools could not get past does (<see cref="BotcheckWindow"/>).</summary>
public enum BotcheckKind
{
    /// <summary>The pane asks first; a yes opens the Browser window on the check.</summary>
    Ask,

    /// <summary>The Browser window opens on the check without asking.</summary>
    Automatic,

    /// <summary>Never asked, never opened: the tool reports the refusal, as before 2026-10-13.</summary>
    Disabled,
}

/// <summary>
/// The setting <c>Web browser botcheck window</c> (2026-10-13, the user's ask and name): the three words the operator picks from
/// (<c>ask</c>, <c>automatic</c>, <c>off</c>) and their mapping to <see cref="BotcheckKind"/>, the <see cref="BrowserMode"/> shape.
/// When <c>web_fetch</c>, <c>web_search</c>, <c>download_file</c> or <c>convert_to_pdf</c> meets a bot check that wants a person (a
/// Cloudflare click, a captcha), the Browser window (<c>Viewer.BrowserWindow</c>) can show it to the user, who solves it there;
/// the page and its cookies then come back to the tool (<see cref="BotCheck"/>). <see cref="Resolve"/> is the one place the saved
/// string becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class BotcheckWindow
{
    /// <summary>Asks first. The compiled default (the user's pick), pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "ask";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "ask", "automatic", "off" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["disabled"] = "off",
    };

    private const string Category = "Web";

    /// <summary>Trims and ignores case; false (and <see cref="BotcheckKind.Ask"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out BotcheckKind kind)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "ask": kind = BotcheckKind.Ask; return true;
            case "automatic": kind = BotcheckKind.Automatic; return true;
            case "off": kind = BotcheckKind.Disabled; return true;
            default: kind = BotcheckKind.Ask; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(BotcheckKind kind) => kind switch
    {
        BotcheckKind.Automatic => "automatic",
        BotcheckKind.Disabled => "off",
        _ => "ask",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "ask" => "asks before opening the Browser window on a bot check",
        "automatic" => "opens the Browser window on a bot check at once",
        "off" => "never opens it; the tool reports the refusal",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static BotcheckKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebBrowserBotcheckWindow, out var kind))
        {
            return kind;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.WebBrowserBotcheckWindow)}='{effective.WebBrowserBotcheckWindow}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return BotcheckKind.Ask;
    }
}
