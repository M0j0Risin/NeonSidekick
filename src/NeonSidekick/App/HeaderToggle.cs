namespace NeonSidekick.App;

/// <summary>
/// <c>/header [on|off]</c> and Ctrl+Alt+H (2026-10-01, the user's ask): <c>Show header</c>
/// (<see cref="Settings.AppSettingsData.ShowHeader"/>) flipped or set and saved, nothing redrawn — the banner is rows of the
/// transcript drawn at each wipe (<see cref="SidekickApp.RenderScreen(Spectre.Console.IAnsiConsole)"/>), so the change shows
/// at the next <c>/clear</c>, <c>/splash</c>, <c>/theme</c> or profile switch (the user's pick over a redraw that would wipe the
/// transcript). <c>/toolbar</c>'s shape (<see cref="ToolbarItems"/>): its words, its notice, its usage error, its pure toggle.
/// </summary>
public static class HeaderToggle
{
    /// <summary><c>/header</c>'s words: <c>on</c> and <c>off</c>, the completion's list. Pinned.</summary>
    public const string OnWord = "on";

    public const string OffWord = "off";

    public static readonly string[] Words = [OnWord, OffWord];

    /// <summary><c>/header</c>'s completion hint beside a word. Pinned.</summary>
    public static string DescribeWord(string word) => word switch
    {
        OnWord => "show the header from the next clear",
        OffWord => "hide the header from the next clear",
        _ => "",
    };

    /// <summary><c>/header</c>'s row on <c>/help</c>. Pinned.</summary>
    public const string HelpSummary = "show or hide the header at the next clear, or /header on|off";

    /// <summary>What <c>/header</c> says it did: the setting, and when the screen follows. Pinned.</summary>
    public static string Notice(bool shown) => shown ? "(header on: drawn from the next clear)" : "(header off: gone from the next clear)";

    /// <summary><c>/header</c> given something that is not on or off. Pinned.</summary>
    public const string UsageError = "/header takes on or off, or nothing to toggle.";

    /// <summary>What <c>/header</c> with <paramref name="args"/> saves over <paramref name="current"/>: bare flips it, <c>on</c> / <c>off</c> set it, any other word is null (the usage error). Pure.</summary>
    public static bool? Toggle(string args, bool current)
    {
        ArgumentNullException.ThrowIfNull(args);
        string word = args.Trim();
        if (word.Length == 0)
        {
            return !current;
        }

        if (word.Equals(OnWord, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return word.Equals(OffWord, StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}
