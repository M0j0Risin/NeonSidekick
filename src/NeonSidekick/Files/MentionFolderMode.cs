using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Files;

/// <summary>What applying a folder from the @-mention list does (<c>File @-mention folder mode</c>).</summary>
public enum MentionFolderAction
{
    /// <summary>The folder is a mention like a file: <c>@folder/</c> and a space go on the line, the list closes.</summary>
    Apply,

    /// <summary>The folder is a step down: <c>@folder/</c> goes on the line and the list stays open on the folder's contents.</summary>
    Remain,
}

/// <summary>
/// The @-mention-folder-mode setting: the two words the operator picks from (<c>apply</c>,
/// <c>remain</c>) and their mapping to <see cref="MentionFolderAction"/>, the
/// <c>CompactType</c> shape. <see cref="Resolve"/> is the one place the saved string becomes the
/// enum: a hand-edited value that is neither falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class MentionFolderMode
{
    /// <summary>A folder is a step down. The compiled default (since 2026-09-19, the user's call; <c>apply</c> before), pinned by <c>MentionFolderModeTests</c>.</summary>
    public const string Default = "remain";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "apply", "remain" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["folder-apply"] = "apply",
        ["folder-remain"] = "remain",
    };

    private const string Category = "Files";

    /// <summary>Trims and ignores case; false for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out MentionFolderAction action)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "apply": action = MentionFolderAction.Apply; return true;
            case "remain": action = MentionFolderAction.Remain; return true;
            default: action = MentionFolderAction.Apply; return false;
        }
    }

    /// <summary>The saved word for <paramref name="action"/>.</summary>
    public static string Name(MentionFolderAction action) => action switch
    {
        MentionFolderAction.Remain => "remain",
        _ => "apply",
    };

    /// <summary>The menu hint next to a mode. The user's wording (2026-09-16), pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "apply" => "insert @folder/ and close the list",
        "remain" => "insert @folder/ and keep listing inside it",
        _ => "",
    };

    /// <summary>The action in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static MentionFolderAction Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.FileMentionFolderMode, out var action))
        {
            return action;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.FileMentionFolderMode)}='{effective.FileMentionFolderMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out action);
        return action;
    }
}
