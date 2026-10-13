using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Files;

/// <summary>What the folder browsers and <c>/tree</c> list (<c>File browser/tree mode</c>; <c>File browser mode</c>, the browsers alone, until 2026-09-23).</summary>
public enum FileBrowserVisibility
{
    /// <summary>Hidden and system folders, and dot-folders, are left out — Explorer's and Finder's default; <c>/tree</c> leaves out dot-files too.</summary>
    Default,

    /// <summary>Every folder the enumeration returns; <c>/tree</c> lists every file too, hidden or dot-named.</summary>
    ShowHidden,
}

/// <summary>
/// The file-browser-mode setting (2026-09-21, the user's ask): the two words the operator picks
/// from (<c>hide-hidden</c>, <c>show-hidden</c>) and their mapping to <see cref="FileBrowserVisibility"/>,
/// the <see cref="MentionFolderMode"/> shape. <see cref="Resolve"/> is the one place the saved
/// string becomes the enum: a hand-edited value that is neither falls back to <see cref="Default"/>
/// with a warning.
/// </summary>
public static class FileBrowserMode
{
    /// <summary>Hidden folders left out. The compiled default, pinned by <c>FileBrowserModeTests</c>.</summary>
    public const string Default = "hide-hidden";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "hide-hidden", "show-hidden" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["default"] = "hide-hidden",
    };

    private const string Category = "Files";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out FileBrowserVisibility visibility)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "hide-hidden": visibility = FileBrowserVisibility.Default; return true;
            case "show-hidden": visibility = FileBrowserVisibility.ShowHidden; return true;
            default: visibility = FileBrowserVisibility.Default; return false;
        }
    }

    /// <summary>The saved word for <paramref name="visibility"/>.</summary>
    public static string Name(FileBrowserVisibility visibility) => visibility switch
    {
        FileBrowserVisibility.ShowHidden => "show-hidden",
        _ => "hide-hidden",
    };

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "hide-hidden" => "hide hidden, system and dot entries",
        "show-hidden" => "list hidden, system and dot entries too",
        _ => "",
    };

    /// <summary>The visibility in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static FileBrowserVisibility Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.FileBrowserMode, out var visibility))
        {
            return visibility;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.FileBrowserMode)}='{effective.FileBrowserMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out visibility);
        return visibility;
    }
}
