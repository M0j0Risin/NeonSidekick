using NeonSidekick.UI;

namespace NeonSidekick.Images;

/// <summary>
/// <c>/pasted view|thumbs|purge</c> (2026-10-18, the user's ask): the <c>Pasted images folder</c>, where the clipboard's pictures are saved as
/// their messages are sent (<c>ChatScreen.Pasted.cs</c>), opened in the picture viewer or the thumbnail browser, or emptied after a yes. Bare,
/// or any other word, is the usage error. An empty setting is the profile's <c>images</c> folder itself, which <c>view</c> and <c>thumbs</c>
/// open and <c>purge</c> refuses.
/// </summary>
public static class PastedCommand
{
    /// <summary>The command's word.</summary>
    public const string Word = "/pasted";

    /// <summary>The argument list's words and notes. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> Words =
    [
        new(PictureFolderCommand.ViewWord, "open the picture viewer on the pasted pictures' folder"),
        new(PictureFolderCommand.ThumbsWord, "open the pasted pictures' folder as thumbnails"),
        new(PictureFolderCommand.PurgeWord, "delete every pasted picture saved there"),
    ];

    /// <summary>Bare <c>/pasted</c>, or a word it does not know. Pinned.</summary>
    public const string Usage = "Usage: /pasted view|thumbs|purge";

    /// <summary>Headless's refusal. Pinned.</summary>
    public const string NeedsScreen = "/pasted needs the screen: its windows and the purge's question are the interactive app's.";

    public static PictureFolderVerb Parse(string args) => PictureFolderCommand.Parse(args);

    public static IReadOnlyList<CompletionItem> Complete(string argText) => PictureFolderCommand.Complete(argText, Words);
}
