using System.Globalization;
using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.App;

/// <summary>What a <c>/browser</c> line asks for (<see cref="BrowserCommand.Parse"/>).</summary>
public enum BrowserVerb
{
    /// <summary>The line is wrong: <see cref="BrowserCommandLine.Error"/> says why.</summary>
    Unknown,

    /// <summary>A page in the Browser window (<see cref="BrowserCommandLine.Url"/>; null: <c>Web browser home page</c>).</summary>
    Open,

    /// <summary>Saved link <see cref="BrowserCommandLine.Number"/> in the Browser window.</summary>
    OpenSaved,

    /// <summary>The address saved (with its name), or with no address the page the window shows.</summary>
    Save,

    /// <summary>The saved links' pane, or printed without one.</summary>
    List,

    /// <summary>Every saved link taken off, after a yes (headless: at once).</summary>
    ClearList,

    /// <summary>The saved link <see cref="BrowserCommandLine.Text"/> names (a number or an address) taken off.</summary>
    Unsave,

    /// <summary>The window's data cleared (<see cref="BrowserCommandLine.Clear"/>).</summary>
    Clear,
}

/// <summary>A parsed <c>/browser</c> line: the verb and what it takes, or the error to show.</summary>
public sealed record BrowserCommandLine(BrowserVerb Verb, Uri? Url = null, string? Name = null, int? Number = null, string Text = "", BrowserClear? Clear = null, string? Error = null);

/// <summary>
/// <c>/browser</c>'s words (later on 2026-10-13, the user's ask: switches for the saved links modelled on <c>/youtube</c>'s), pure:
/// <c>/browser [url]</c> and <c>/browser clear history|all</c> as before, and <c>save [url [name]]</c>, <c>list [--clear]</c>,
/// <c>unsave &lt;n|url&gt;</c> and <c>/browser &lt;n&gt;</c> for the saved links. A verb word can never be an address: a bare word is
/// one only with a dot in it (<see cref="WebFetcher.ParseUrl"/>), so <c>/browser save news</c> is <c>save</c>'s error, not a page.
/// </summary>
public static class BrowserCommand
{
    public const string Word = "/browser";

    public const string SaveWord = "save";
    public const string ListWord = "list";
    public const string UnsaveWord = "unsave";

    /// <summary>The switch after <c>list</c> that empties the list, <c>/youtube list --clear</c>'s.</summary>
    public const string ClearSwitch = "--clear";

    /// <summary>
    /// Whether this run has a Browser window (2026-10-18, the Mac Browser plan: Windows', or a Mac's on macOS 14 or later with a desktop;
    /// <c>Program</c> sets it on a Mac): the words name the window where there is one. Off Windows there was none until then (2026-10-17, the
    /// Mac pass): <c>save</c> takes a link, and Enter on <c>list</c>'s rows opens one in the default browser.
    /// </summary>
    public static Func<bool> WindowHere { get; set; } = OperatingSystem.IsWindows;

    /// <summary>The words in the order the completion offers them, with their notes, for <see cref="WindowHere"/>. Pinned.</summary>
    public static IReadOnlyList<(string Word, string Note)> Words =>
    [
        (SaveWord, WindowHere() ? "save the page the Browser window shows, or /browser save <url> [name]" : "save a link: /browser save <url> [name]"),
        (ListWord, WindowHere() ? "the saved links: Enter opens one in the Browser window; /browser list --clear empties it" : "the saved links: Enter opens one in your browser; /browser list --clear empties it"),
        (UnsaveWord, "take a saved link off the list: /browser unsave <n|url>"),
        (BotCheckText.ClearWord, "clear the Browser window's history, or everything it keeps: /browser clear history|all"),
    ];

    public static BrowserCommandLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new BrowserCommandLine(BrowserVerb.Open);
        }

        int gap = text.IndexOf(' ', StringComparison.Ordinal);
        string first = gap < 0 ? text : text[..gap];
        string rest = gap < 0 ? "" : text[(gap + 1)..].Trim();
        switch (first.ToLowerInvariant())
        {
            case BotCheckText.ClearWord:
            {
                var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                BrowserClear? clear = words.Length != 1 ? null
                    : string.Equals(words[0], BotCheckText.ClearHistoryWord, StringComparison.OrdinalIgnoreCase) ? BrowserClear.History
                    : string.Equals(words[0], BotCheckText.ClearAllWord, StringComparison.OrdinalIgnoreCase) ? BrowserClear.All
                    : null;
                return clear is null ? Error(BotCheckText.ClearUsage) : new BrowserCommandLine(BrowserVerb.Clear, Clear: clear);
            }

            case SaveWord:
            {
                if (rest.Length == 0)
                {
                    return new BrowserCommandLine(BrowserVerb.Save);
                }

                int nameGap = rest.IndexOf(' ', StringComparison.Ordinal);
                string address = nameGap < 0 ? rest : rest[..nameGap];
                string? name = nameGap < 0 ? null : rest[(nameGap + 1)..].Trim();
                return WebFetcher.ParseUrl(address) is { } url
                    ? new BrowserCommandLine(BrowserVerb.Save, url, string.IsNullOrEmpty(name) ? null : name)
                    : Error($"'{address}' is not an http or https address. " + BrowserLinkText.Usage);
            }

            case ListWord when rest.Length == 0:
                return new BrowserCommandLine(BrowserVerb.List);
            case ListWord when rest.Equals(ClearSwitch, StringComparison.OrdinalIgnoreCase):
                return new BrowserCommandLine(BrowserVerb.ClearList);
            case ListWord:
                return Error(BrowserLinkText.Usage);
            case UnsaveWord when rest.Length == 0:
                return Error(BrowserLinkText.Usage);
            case UnsaveWord:
                return new BrowserCommandLine(BrowserVerb.Unsave, Text: rest);
        }

        if (text.All(char.IsAsciiDigit))
        {
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? new BrowserCommandLine(BrowserVerb.OpenSaved, Number: number, Text: text)
                : new BrowserCommandLine(BrowserVerb.OpenSaved, Number: int.MaxValue, Text: text);
        }

        return WebFetcher.ParseUrl(text) is { } page ? new BrowserCommandLine(BrowserVerb.Open, page) : Error(BotCheckText.NotAUrl(text));
    }

    /// <summary>
    /// Whether the line waits on the watcher under a reply rather than running on the turn task (later on 2026-10-13): the pane
    /// (<c>list</c>), the questions (<c>list --clear</c>, <c>clear all</c>) and a bare <c>save</c>, which waits for the window's answer.
    /// </summary>
    public static bool IsPane(string? args) => Parse(args) switch
    {
        { Verb: BrowserVerb.List or BrowserVerb.ClearList } => true,
        { Verb: BrowserVerb.Clear, Clear: BrowserClear.All } => true,
        { Verb: BrowserVerb.Save, Url: null } => true,
        _ => false,
    };

    /// <summary>
    /// <see cref="Complete(string)"/> with the saved links by number (2026-10-27, the completion survey): <c>&lt;n&gt;</c> after the verbs while
    /// the first word is typed (not <paramref name="unsaveOnly"/>: the Web UI's page, where a number opens a window on the PC) and
    /// <c>unsave &lt;n&gt;</c>, each noted by its name, else its address. On the bare list for <c>/prompts</c>' reason
    /// (<see cref="PromptsCommand"/>). <paramref name="links"/> is asked only then. Pure.
    /// </summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText, Func<IReadOnlyList<BrowserLink>> links, bool unsaveOnly)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(links);
        string unsaveHead = UnsaveWord + " ";
        if (!unsaveOnly && !argText.Contains(' ', StringComparison.Ordinal))
        {
            return UI.MentionCompleter.Matches([.. Complete(""), .. UI.NumberedItems.Of("", links().Select(LinkNote))], argText);
        }

        if (argText.StartsWith(unsaveHead, StringComparison.OrdinalIgnoreCase))
        {
            return UI.MentionCompleter.Matches(UI.NumberedItems.Of(argText[..unsaveHead.Length], links().Select(LinkNote)), argText);
        }

        return Complete(argText);

        static string LinkNote(BrowserLink link) => string.IsNullOrWhiteSpace(link.Name) ? link.Url : link.Name;
    }

    /// <summary>The input line's argument list after <c>/browser </c>: the verbs while the first word is typed; then <c>--clear</c> after <c>list</c>, <c>history</c> and <c>all</c> after <c>clear</c>.</summary>
    public static IReadOnlyList<UI.CompletionItem> Complete(string argText)
    {
        ArgumentNullException.ThrowIfNull(argText);
        if (argText.StartsWith(ListWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            return UI.MentionCompleter.Matches([new(ListWord + " " + ClearSwitch, "take every saved link off the list, after a yes")], argText);
        }

        if (argText.StartsWith(BotCheckText.ClearWord + " ", StringComparison.OrdinalIgnoreCase))
        {
            return UI.MentionCompleter.Matches(
            [
                new(BotCheckText.ClearWord + " " + BotCheckText.ClearHistoryWord, "the pages the Browser window visited"),
                new(BotCheckText.ClearWord + " " + BotCheckText.ClearAllWord, "everything it keeps for this profile, after a yes; your saved links stay"),
            ], argText);
        }

        return argText.Contains(' ', StringComparison.Ordinal) ? [] : UI.MentionCompleter.Matches(Words.Select(w => new UI.CompletionItem(w.Word, w.Note)).ToList(), argText);
    }

    /// <summary>
    /// A <c>/browser</c> line headless (later on 2026-10-13): the saved links need no window, so <c>save &lt;url&gt; [name]</c>,
    /// <c>list</c> (printed), <c>list --clear</c> (at once, <c>/youtube list --clear</c>'s headless rule) and <c>unsave</c> work; anything
    /// else wants the window, <see cref="BotCheckText.BrowserNeedsScreen"/>. The lines to print, an error's led by <c>Error: </c>.
    /// </summary>
    public static IReadOnlyList<string> Headless(BrowserLinks links, string? args)
    {
        ArgumentNullException.ThrowIfNull(links);
        var line = Parse(args);
        if (line.Error is { } error)
        {
            return ["Error: " + error];
        }

        switch (line.Verb)
        {
            case BrowserVerb.Save when line.Url is { } url:
            {
                var outcome = links.Add(url.AbsoluteUri, line.Name, out var saved);
                return [BrowserLinkText.Refusal(outcome) ?? (outcome == BrowserLinkOutcome.AlreadySaved ? BrowserLinkText.AlreadySaved(saved!) : BrowserLinkText.Saved(saved!))];
            }

            case BrowserVerb.List:
            {
                var list = links.List();
                bool window = WindowHere();   // the window's words where there is one (2026-10-17, the Mac pass; a Mac's too since 2026-10-18)
                return list.Count == 0 ? [BrowserLinkText.NoneSavedFor(window)] : [BrowserLinkText.Caption(list.Count, window), .. list.Select((l, i) => BrowserLinkText.PrintedRow(i + 1, l))];
            }

            case BrowserVerb.ClearList:
            {
                var outcome = links.Clear(out int cleared);
                return [BrowserLinkText.Refusal(outcome) ?? (cleared == 0 ? BrowserLinkText.NoneSavedFor(WindowHere()) : BrowserLinkText.Cleared(cleared))];
            }

            case BrowserVerb.Unsave:
                return links.Resolve(line.Text) is { } link
                    ? [BrowserLinkText.Refusal(links.Remove(link.Url), line.Text) ?? BrowserLinkText.Removed(link)]
                    : [BrowserLinkText.NotSaved(line.Text, links.List().Count)];
            default:
                return ["Error: " + BotCheckText.BrowserNeedsScreen];
        }
    }

    private static BrowserCommandLine Error(string error) => new(BrowserVerb.Unknown, Error: error);
}
