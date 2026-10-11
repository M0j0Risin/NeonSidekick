namespace NeonSidekick.Emojis;

/// <summary>What a <c>/avatar</c> line asks for (2026-10-10).</summary>
public enum AvatarVerb
{
    /// <summary>Bare: the picker on the screen, the avatar said headless.</summary>
    Show,

    /// <summary><c>clear</c> (or <c>none</c>, <c>off</c>): no avatar.</summary>
    Clear,

    /// <summary>One emoji: that one, at once.</summary>
    Set,

    /// <summary>Words: the picker searching for them on the screen; headless, the best of the search.</summary>
    Search,
}

/// <summary>
/// <c>/avatar</c>'s argument read (2026-10-10, the user's pick of the extras): bare opens the picker, <c>clear</c> clears, an emoji sets it,
/// anything else is a search (<see cref="AvatarVerb"/>). The screen and headless share it. Pure.
/// </summary>
public static class AvatarCommand
{
    /// <summary>The word that clears the avatar. Pinned (completion offers it).</summary>
    public const string ClearWord = "clear";

    /// <summary>The note beside <see cref="ClearWord"/> in the completion list.</summary>
    public const string ClearNote = "no avatar: replies open with ● again";

    /// <summary>The verb and its emoji or words for <paramref name="args"/>.</summary>
    public static (AvatarVerb Verb, string Text) Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return (AvatarVerb.Show, "");
        }

        if (text.Equals(ClearWord, StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return (AvatarVerb.Clear, "");
        }

        return EmojiSearch.AsEmoji(text) is { } emoji ? (AvatarVerb.Set, emoji) : (AvatarVerb.Search, text);
    }
}
