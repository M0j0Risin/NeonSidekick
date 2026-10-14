using System.Globalization;
using NeonSidekick.UI;

namespace NeonSidekick.Viewer;

/// <summary>
/// The saved links' wording (2026-10-13, the user's ask: <see cref="BrowserLinks"/>): <c>/browser list</c>'s pane, the <c>/browser</c>
/// verbs' answers and the Browser window's ☆ menu. An answer leading with <c>Error: </c> is shown as an error, the rest as a notice
/// (the <c>/youtube</c> answers' rule). Pinned by the tests.
/// </summary>
public static class BrowserLinkText
{
    private const string ErrorPrefix = "Error: ";

    // ── The pane ─────────────────────────────────────────────────────────────

    /// <summary>The pane's title: the toolbar item's ⭐, as <c>📺 Saved videos</c> is the YouTube item's. Pinned.</summary>
    public const string PaneTitle = "⭐ Saved links";

    public const string AddButton = "⊕ add";
    public const char AddKey = 'a';
    public const string NameButton = "✎ name";
    public const char NameKey = 'n';
    public const string AddressButton = "✎ address";
    public const char AddressKey = 'e';

    /// <summary>The remove button: the list's <c>✖  remove</c> (two spaces: Windows Terminal draws ✖ two cells wide).</summary>
    public const string RemoveButton = YouTube.YouTubeText.RemoveButton;
    public const char RemoveKey = 'd';

    /// <summary>The pane's hint row while nothing is typed. Pinned.</summary>
    public const string PaneKeys = "Enter = open · a = add · n = name · e = address · d = remove · c = clear all · Shift+↑↓ = move · " + MenuFilter.TypeOrShiftAndCloseKeys;

    /// <summary>The one dim row of an empty list, under the same buttons, so add works on it. Pinned.</summary>
    public const string EmptyRow = "No saved links yet: a adds one, and Ctrl+D in the Browser window saves its page.";

    /// <summary>No saved links, on the transcript (a printed list, a clear of none).</summary>
    public const string NoneSaved = "No saved links yet: /browser save <url> adds one, and Ctrl+D in the Browser window saves its page.";

    /// <summary>The count over the pane's rows and the printed list.</summary>
    public static string Caption(int count) =>
        $"{Links(count)}; Enter opens one in the Browser window.";

    /// <summary>The pane's name column at most: a longer name is cut with an ellipsis, the footer showing it whole.</summary>
    public const int NameWidth = 40;

    /// <summary>The pane's address column at most, cut the same way.</summary>
    public const int AddressWidth = 60;

    /// <summary>The name and address columns over <paramref name="links"/>: the widest of each in cells, capped; over the whole list, so a filter leaves them where they are.</summary>
    public static (int Name, int Address) Columns(IReadOnlyList<BrowserLink> links)
    {
        ArgumentNullException.ThrowIfNull(links);
        int name = links.Count == 0 ? 0 : links.Max(l => TextCells.Width(l.Name ?? ""));
        int address = links.Count == 0 ? 0 : links.Max(l => TextCells.Width(BrowserLinks.WithoutScheme(l.Url)));
        return (Math.Min(name, NameWidth), Math.Min(address, AddressWidth));
    }

    /// <summary>A link as the pane's row: its name padded to its column (none with no name on the list), two blanks, its address without the scheme. Pinned.</summary>
    public static string PaneRow(BrowserLink link, int nameWidth, int addressWidth)
    {
        ArgumentNullException.ThrowIfNull(link);
        string row = nameWidth > 0 ? Cell(link.Name ?? "", nameWidth) + "  " : "";
        return row + ScreenPane.Fit(BrowserLinks.WithoutScheme(link.Url), Math.Max(1, addressWidth));
    }

    /// <summary>The footer under the list for <paramref name="link"/>: its name (or address) whole, then <c>https://… · saved 2026-10-13 18:00</c>. Pinned.</summary>
    public static MenuFooter Footer(BrowserLink link, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(zone);
        return new MenuFooter(link.Name ?? link.Url, link.Url + " · saved " + Sessions.SessionText.Moment(link.Added, zone));
    }

    /// <summary>Whether <paramref name="link"/> stays under <paramref name="filter"/>: its name or address holds it.</summary>
    public static bool Matches(string filter, BrowserLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return MenuFilter.Matches(filter, link.Name ?? "", link.Url);
    }

    /// <summary>A line of the printed list (the pane off, headless, macOS): <c>3. News — https://example.com/</c>, the address alone with no name.</summary>
    public static string PrintedRow(int number, BrowserLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        string at = number.ToString(CultureInfo.InvariantCulture) + ". ";
        return at + (link.Name is { Length: > 0 } name ? name + " — " + link.Url : link.Url);
    }

    // ── The answers ──────────────────────────────────────────────────────────

    /// <summary>How a link is named in a sentence: its name in quotes, else its address.</summary>
    public static string Name(BrowserLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.Name is { Length: > 0 } name ? $"\"{name}\"" : link.Url;
    }

    /// <summary>A save done.</summary>
    public static string Saved(BrowserLink link) => link.Name is { Length: > 0 } ? $"Saved {Name(link)} ({link.Url})." : $"Saved {link.Url}.";

    /// <summary>
    /// A save of an address saved already: its name, and how to rename it, so a new name given with it is never silently dropped (only a
    /// missing name is filled).
    /// </summary>
    public static string AlreadySaved(BrowserLink link) =>
        link.Name is { Length: > 0 } ? $"{link.Url} is saved already, as {Name(link)}; ✎ name on /browser list renames it." : $"{link.Url} is saved already.";

    public static string Removed(BrowserLink link) => $"Removed {Name(link)} from the saved links.";

    public static string Renamed(BrowserLink link, string? name) =>
        string.IsNullOrEmpty(name) ? $"{link.Url} has no name now." : $"Renamed to \"{name}\".";

    public static string Readdressed(BrowserLink link, string url) => $"{Name(link)} goes to {url} now.";

    /// <summary>The question before the pane's remove.</summary>
    public static string RemovePrompt(BrowserLink link) => $"⭐ Remove {Name(link)} from the saved links?";

    /// <summary>The question before the pane's clear all and <c>/browser list --clear</c>. Pinned.</summary>
    public static string ClearPrompt(int count) => count == 1 ? "⭐ Remove the one saved link?" : $"⭐ Remove all {Links(count)}?";

    /// <summary>Every saved link taken off. Pinned.</summary>
    public static string Cleared(int count) => $"Removed {Links(count)}.";

    /// <summary>What names no saved link: for a number, the list's size with it.</summary>
    public static string NotSaved(string? text, int count) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? ErrorPrefix + $"There is no saved link {text!.Trim()}: " + (count == 0 ? "none is saved yet." : $"the list runs 1 to {count.ToString(CultureInfo.InvariantCulture)}.")
            : ErrorPrefix + $"\"{text}\" is no saved link: give its number in /browser list or its address.";

    /// <summary>The list could not be written (the log has why).</summary>
    public const string SaveFailed = "Could not write the saved links (" + BrowserLinks.FileName + "); the log says why.";

    /// <summary>The file is a newer NeonSidekick's: left alone, nothing changed. Pinned.</summary>
    public const string NewerFile = BrowserLinks.FileName + " was saved by a newer NeonSidekick; update to change it.";

    public const string NotWeb = "Only an http or https address can be saved.";

    /// <summary>An address with a user name or password: the list is a plain file. Pinned.</summary>
    public const string Credentials = "An address with a user name or password in it is not saved: " + BrowserLinks.FileName + " is a plain file.";

    public const string Duplicate = "Another saved link has that address already.";

    /// <summary>A refusal's answer, led by <c>Error: </c>; null for an outcome that is no refusal.</summary>
    public static string? Refusal(BrowserLinkOutcome outcome, string? text = null, int count = 0) => outcome switch
    {
        BrowserLinkOutcome.NotWeb => ErrorPrefix + NotWeb,
        BrowserLinkOutcome.Credentials => ErrorPrefix + Credentials,
        BrowserLinkOutcome.Duplicate => ErrorPrefix + Duplicate,
        BrowserLinkOutcome.Newer => ErrorPrefix + NewerFile,
        BrowserLinkOutcome.Failed => ErrorPrefix + SaveFailed,
        BrowserLinkOutcome.NotSaved => NotSaved(text, count),
        _ => null,
    };

    /// <summary>The <c>/browser</c> saved-link verbs' usage. Pinned.</summary>
    public const string Usage = "Usage: /browser save [<url> [name]] · /browser list [--clear] · /browser unsave <n|url> · /browser <n> opens a saved link";

    /// <summary>The pane's add, name or address with no input row to type on.</summary>
    public const string NeedsInput = "Typing an address or a name needs the input row: /browser save <url> [name] adds one.";

    /// <summary>A bare <c>/browser save</c> with no web page open in the Browser window.</summary>
    public const string NothingToSave = "The Browser window is not open on a web page to save. " + Usage;

    // ── The window's ☆ menu ──────────────────────────────────────────────────

    public const string SaveThisPage = "Save this page";
    public const string RemoveThisPage = "Remove this page";
    public const string NoneInMenu = "No saved links yet";

    /// <summary>An overflow submenu's name: the numbers of its first and last links, <c>16–40</c>. Pinned.</summary>
    public static string SubmenuLabel(int first, int last) =>
        first.ToString(CultureInfo.InvariantCulture) + "–" + last.ToString(CultureInfo.InvariantCulture);

    /// <summary>The dim row past the menu's room. Pinned.</summary>
    public static string MoreInList(int count) => $"{count.ToString(CultureInfo.InvariantCulture)} more in /browser list";

    private static string Links(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} saved link{(count == 1 ? "" : "s")}";

    /// <summary><paramref name="text"/> in exactly <paramref name="width"/> cells: cut with an ellipsis, or padded with blanks.</summary>
    private static string Cell(string text, int width)
    {
        string fitted = ScreenPane.Fit(text, width);
        return fitted + new string(' ', Math.Max(0, width - TextCells.Width(fitted)));
    }
}
