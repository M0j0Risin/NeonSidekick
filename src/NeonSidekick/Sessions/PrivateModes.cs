using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Sessions;

/// <summary>What happens to a private conversation's pictures when it ends (<c>Private pictures</c>, 2026-11-25).</summary>
public enum PrivatePicturesMode
{
    /// <summary>Saved in their usual folders; the end asks whether to keep or delete them.</summary>
    Ask,

    /// <summary>Saved in their usual folders and kept.</summary>
    Keep,

    /// <summary>Saved in the profile's <c>images\private\&lt;run&gt;</c> folder and deleted at the end, with those saved before the switch.</summary>
    Delete,
}

/// <summary>
/// The <c>Private pictures</c> setting's words (2026-11-25, the private-conversations plan), the <see cref="SessionNamingMode"/> shape:
/// <see cref="Resolve"/> is the one place the saved string becomes the enum, a hand-edited word falling back to <see cref="Default"/> with a warning.
/// </summary>
public static class PrivatePictures
{
    /// <summary>Asks at the end (the user's ask, the default). Pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "ask";

    /// <summary>The words in menu order.</summary>
    public static readonly string[] Names = { "ask", "keep", "delete" };

    private const string Category = "Sessions";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out PrivatePicturesMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "ask": mode = PrivatePicturesMode.Ask; return true;
            case "keep": mode = PrivatePicturesMode.Keep; return true;
            case "delete": mode = PrivatePicturesMode.Delete; return true;
            default: mode = PrivatePicturesMode.Ask; return false;
        }
    }

    /// <summary>The saved word for <paramref name="mode"/>.</summary>
    public static string Name(PrivatePicturesMode mode) => mode switch
    {
        PrivatePicturesMode.Keep => "keep",
        PrivatePicturesMode.Delete => "delete",
        _ => "ask",
    };

    /// <summary>The menu hint next to a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "ask" => "asks whether to keep or delete them when the conversation ends",
        "keep" => "they stay in the images folders",
        "delete" => "deleted when the conversation ends, with those saved before it went private",
        _ => "",
    };

    /// <summary>The mode in force for <paramref name="effective"/>; an unknown saved word warns and uses <see cref="Default"/>.</summary>
    public static PrivatePicturesMode Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.PrivatePictures, out var mode))
        {
            return mode;
        }

        DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.PrivatePictures)}='{effective.PrivatePictures}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out mode);
        return mode;
    }
}

/// <summary>The <c>Private log text</c> setting's words (2026-11-25): whether the <c>--log</c> file hides what a private conversation says.</summary>
public static class PrivateLogText
{
    /// <summary>Hidden (the default). Pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "hide";

    /// <summary>The words in menu order.</summary>
    public static readonly string[] Names = { "hide", "keep" };

    private const string Category = "Sessions";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out bool hide)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "hide": hide = true; return true;
            case "keep": hide = false; return true;
            default: hide = true; return false;
        }
    }

    /// <summary>The menu hint next to a word. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "hide" => "the --log file shows [private] for what the conversation says",
        "keep" => "logged as usual",
        _ => "",
    };

    /// <summary>Whether <paramref name="effective"/> hides the text; an unknown saved word warns and hides.</summary>
    public static bool Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.PrivateLogText, out bool hide))
        {
            return hide;
        }

        DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.PrivateLogText)}='{effective.PrivateLogText}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return true;
    }
}
