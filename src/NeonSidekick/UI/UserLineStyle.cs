namespace NeonSidekick.UI;

/// <summary>
/// The setting <c>User line style</c> (2026-10-04, the UI review: the sent line was a full-width bold purple block, the loudest thing on
/// the screen and in the reply headings' own colour): how the transcript draws the user's sent line — <c>quiet</c> (the default:
/// the <c>›</c> in the user colour, the words in the body ink), <c>slab</c> (the line on the panel's faint fill) or <c>bold</c> (the
/// look until then, in the theme's <c>user</c> style). The screen hands the setting to the pane (<see cref="ScreenPane.UserLineStyle"/>);
/// <see cref="Resolve"/> reads an unknown value as <see cref="Default"/>.
/// </summary>
public static class UserLineStyle
{
    public const string Quiet = "quiet";
    public const string Slab = "slab";
    public const string Bold = "bold";

    /// <summary>The compiled default (the user's call). Pinned.</summary>
    public const string Default = Quiet;

    /// <summary>The styles in menu order.</summary>
    public static readonly string[] Names = { Quiet, Slab, Bold };

    /// <summary>The menu hint next to a style. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        Quiet => "the › in the user colour, your words in the body's",
        Slab => "your line on a faint slab",
        Bold => "your line bold in the user colour",
        _ => "",
    };

    /// <summary><paramref name="value"/> when it is one of <see cref="Names"/> (case and blanks aside), else <see cref="Default"/>. Pure.</summary>
    public static string Resolve(string? value)
    {
        string name = value?.Trim().ToLowerInvariant() ?? "";
        return Array.IndexOf(Names, name) >= 0 ? name : Default;
    }
}
