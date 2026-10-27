using NeonSidekick.UI;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// <c>/ha stills view|thumbs|purge</c> (2026-10-27, the user's ask: a purge "similar to how we do it with comfy"): the camera stills the 📹
/// chip and <c>ha_camera</c> save in <see cref="HaCamera.FolderName"/> under the profile's images folder, opened in the picture viewer or
/// the thumbnail browser, or emptied after a yes. The screen takes it before <see cref="HaCommand.RunAsync"/>, so it works with no Home
/// Assistant URL or key (old stills can be cleared offline). Bare <c>stills</c>, or another word after it, is the usage error.
/// </summary>
public static class HaStillsCommand
{
    /// <summary>The <c>/ha</c> word.</summary>
    public const string Word = "stills";

    /// <summary>The argument list's words and notes, after <c>stills </c>. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> Words =
    [
        new(PictureFolderCommand.ViewWord, "open the picture viewer on the camera stills' folder"),
        new(PictureFolderCommand.ThumbsWord, "open the camera stills' folder as thumbnails"),
        new(PictureFolderCommand.PurgeWord, "delete every camera still saved there"),
    ];

    /// <summary>Bare <c>/ha stills</c>, or a word it does not know. Pinned.</summary>
    public const string Usage = "Usage: /ha stills view|thumbs|purge";

    /// <summary>Headless's refusal. Pinned.</summary>
    public const string NeedsScreen = "/ha stills needs the screen: its windows and the purge's question are the interactive app's.";

    /// <summary>
    /// True when <paramref name="haArgs"/> (the text after <c>/ha</c>) starts with <see cref="Word"/>, in any case; <paramref name="verb"/>
    /// is then the rest read as a picture folder's words (<see cref="PictureFolderVerb.Bare"/> and <see cref="PictureFolderVerb.Unknown"/>
    /// are the usage error).
    /// </summary>
    public static bool TryParse(string haArgs, out PictureFolderVerb verb)
    {
        ArgumentNullException.ThrowIfNull(haArgs);
        string trimmed = haArgs.Trim();
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string first = space < 0 ? trimmed : trimmed[..space];
        if (!string.Equals(first, Word, StringComparison.OrdinalIgnoreCase))
        {
            verb = PictureFolderVerb.Unknown;
            return false;
        }

        verb = PictureFolderCommand.Parse(space < 0 ? "" : trimmed[(space + 1)..]);
        return true;
    }
}
