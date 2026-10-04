using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Screen;

/// <summary>Whether the model's screenshot waits for the user (<see cref="ScreenAskMode"/>).</summary>
public enum ScreenAsk
{
    /// <summary>The allow pane: Deny, Allow once, Allow for this session.</summary>
    Ask,

    /// <summary>Taken without asking; the preview still shows what was sent.</summary>
    Allow,
}

/// <summary>
/// The setting <c>Screen capture ask</c> (2026-10-04, the user's call): <c>ask</c> or <c>allow</c>. The <c>CameraShutterMode</c>
/// shape: <see cref="Resolve"/> is the one place the saved string becomes the enum.
/// </summary>
public static class ScreenAskMode
{
    public const string Default = "ask";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = ["ask", "allow"];

    public static bool TryParse(string? text, out ScreenAsk ask)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "ask": ask = ScreenAsk.Ask; return true;
            case "allow": ask = ScreenAsk.Allow; return true;
            default: ask = ScreenAsk.Ask; return false;
        }
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "ask" => "you allow each screenshot (once, or for the session) on a pane",
        "allow" => "the model takes screenshots without asking",
        _ => "",
    };

    public static ScreenAsk Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.ScreenAsk, out var ask))
        {
            return ask;
        }

        DiagnosticLog.Warn(ScreenText.Category, $"{nameof(AppSettingsData.ScreenAsk)}='{effective.ScreenAsk}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return ScreenAsk.Ask;
    }
}
