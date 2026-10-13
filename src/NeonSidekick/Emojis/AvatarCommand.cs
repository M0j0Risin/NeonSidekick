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

/// <summary>Whose avatar a <c>/avatar</c> line is about (2026-10-12): the model's (the profile's replies) or the user's (their sent lines).</summary>
public enum AvatarTarget
{
    /// <summary><c>model</c>, or no word: <c>Model avatar</c>.</summary>
    Model,

    /// <summary><c>user</c>: <c>User avatar</c>.</summary>
    User,
}

/// <summary>
/// <c>/avatar</c>'s argument read (2026-10-10, the user's pick of the extras): bare opens the picker, <c>clear</c> clears, an emoji sets it,
/// anything else is a search (<see cref="AvatarVerb"/>). The screen and headless share it. Pure. Since 2026-10-12 (the user's ask) a leading
/// <c>model</c> or <c>user</c> names whose avatar (<see cref="ParseLine"/>): <c>/avatar user [clear]</c>, <c>/avatar model [clear]</c>; no word
/// is the model's, as it was.
/// </summary>
public static class AvatarCommand
{
    /// <summary>The word for the model's avatar (2026-10-12). Pinned (completion offers it).</summary>
    public const string ModelWord = "model";

    /// <summary>The word for the user's avatar (2026-10-12). Pinned (completion offers it).</summary>
    public const string UserWord = "user";

    /// <summary>The note beside <see cref="ModelWord"/> in the completion list.</summary>
    public const string ModelNote = "the model's avatar: its replies open with it";

    /// <summary>The note beside <see cref="UserWord"/> in the completion list.</summary>
    public const string UserNote = "your avatar: your sent lines open with it";

    /// <summary>The note beside <see cref="ClearWord"/> after <see cref="UserWord"/>.</summary>
    public const string UserClearNote = "no user avatar: your lines open with › again";

    /// <summary>
    /// <paramref name="args"/> with its leading target word read (2026-10-12): <c>user</c> or <c>model</c> (any case), then <see cref="Parse"/>
    /// over the rest; with neither word, the model's and the whole of it.
    /// </summary>
    public static (AvatarTarget Target, AvatarVerb Verb, string Text) ParseLine(string? args)
    {
        var (target, rest) = Target(args);
        var (verb, text) = Parse(rest);
        return (target, verb, text);
    }

    /// <summary>The target word at the head of <paramref name="args"/> and what follows it; <see cref="AvatarTarget.Model"/> and the whole line without one.</summary>
    public static (AvatarTarget Target, string After) Target(string? args)
    {
        string text = (args ?? "").Trim();
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text[..space];
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        if (head.Equals(UserWord, StringComparison.OrdinalIgnoreCase))
        {
            return (AvatarTarget.User, rest);
        }

        return head.Equals(ModelWord, StringComparison.OrdinalIgnoreCase) ? (AvatarTarget.Model, rest) : (AvatarTarget.Model, text);
    }

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
