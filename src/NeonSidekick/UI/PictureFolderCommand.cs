namespace NeonSidekick.UI;

/// <summary>What a picture folder's words ask for (2026-10-18, the user's ask: <c>/splash</c> and <c>/pasted</c> took <c>/comfy</c>'s three words).</summary>
public enum PictureFolderVerb
{
    /// <summary>No words: the command's own bare meaning (<c>/splash</c> the splash; <c>/pasted</c> its usage).</summary>
    Bare,

    /// <summary><c>view</c>: the picture viewer on the folder, made when missing.</summary>
    View,

    /// <summary><c>thumbs</c>: the thumbnail browser on the folder, made when missing.</summary>
    Thumbs,

    /// <summary><c>purge</c>: everything in the folder, after a yes (the Recycle bin under <c>File deletes to Recycle bin</c>).</summary>
    Purge,

    /// <summary>Any other words: the usage error, nothing done.</summary>
    Unknown,
}

/// <summary>
/// The three words a picture folder's command takes (2026-10-18): <c>view</c>, <c>thumbs</c> and <c>purge</c>, alone and in any case, as
/// <c>/comfy</c>'s are. Shared by <see cref="SplashCommand"/> and <see cref="Images.PastedCommand"/>. Pure.
/// </summary>
public static class PictureFolderCommand
{
    public const string ViewWord = "view";
    public const string ThumbsWord = "thumbs";
    public const string PurgeWord = "purge";

    /// <summary><paramref name="args"/> read: empty is <see cref="PictureFolderVerb.Bare"/>; one of the three words alone, its verb; anything else unknown.</summary>
    public static PictureFolderVerb Parse(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Trim().ToLowerInvariant() switch
        {
            "" => PictureFolderVerb.Bare,
            ViewWord => PictureFolderVerb.View,
            ThumbsWord => PictureFolderVerb.Thumbs,
            PurgeWord => PictureFolderVerb.Purge,
            _ => PictureFolderVerb.Unknown,
        };
    }

    /// <summary>The input line's argument list: the three words with their notes, none past a space.</summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<CompletionItem> words)
    {
        ArgumentNullException.ThrowIfNull(argText);
        return argText.Contains(' ', StringComparison.Ordinal) ? [] : MentionCompleter.Matches(words, argText);
    }
}

/// <summary>
/// <c>/splash [view|thumbs|purge]</c> (2026-10-18, the user's ask): bare, the startup view over a fresh conversation as always; the words open
/// or empty the profile's <c>splash_images</c> folder (<see cref="SplashImages.ProfileFolderName"/>), its <c>.trash</c> too. A word it does not
/// know is the usage error and wipes nothing — <c>/splash</c> took no argument until then, so <c>/splash view</c> was refused before any handler.
/// </summary>
public static class SplashCommand
{
    /// <summary>The argument list's words and notes. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> Words =
    [
        new(PictureFolderCommand.ViewWord, "open the picture viewer on the splash pictures' folder"),
        new(PictureFolderCommand.ThumbsWord, "open the splash pictures' folder as thumbnails"),
        new(PictureFolderCommand.PurgeWord, "delete every splash picture; the built-in ones show again"),
    ];

    /// <summary>A <c>/splash</c> word it does not know. Pinned.</summary>
    public const string Usage = "Usage: /splash, or /splash view|thumbs|purge";

    /// <summary>Headless's refusal for any <c>/splash</c> with words. Pinned.</summary>
    public const string NeedsScreen = "/splash view, thumbs and purge need the screen: they are the interactive app's.";

    /// <summary>After <c>/splash view|thumbs</c> on a folder with no pictures yet: the built-in ones are the app's, not in it. Pinned.</summary>
    public const string BuiltInNotice = "(the built-in splash pictures show until this folder has some; drop pictures here, or ask the model to set_splash_image)";

    public static PictureFolderVerb Parse(string args) => PictureFolderCommand.Parse(args);

    public static IReadOnlyList<CompletionItem> Complete(string argText) => PictureFolderCommand.Complete(argText, Words);
}
