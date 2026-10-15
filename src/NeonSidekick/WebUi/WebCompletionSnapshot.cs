using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.WebUi;

/// <summary>What the page's dropdown asks: the text up to the end of the word under the cursor, the cursor, and the page's own count for it.</summary>
public sealed class WebCompleteRequest
{
    public string? Text { get; set; }

    public int Cursor { get; set; }

    public long Seq { get; set; }
}

/// <summary>One row of the page's dropdown: its words, the note beside them, and what a pick writes over the span.</summary>
public sealed record WebCompletionItem(string Text, string? Note, string Insert);

/// <summary>
/// The page's dropdown (later on 2026-10-14): the span a pick replaces (<see cref="Start"/> to <see cref="End"/>, in the text the page sent), the
/// rows, and whether there were more (or the server was busy). <see cref="Seq"/> is the page's own, so it drops an answer for a text it changed since.
/// </summary>
public sealed record WebCompletion(long Seq, int Start, int End, IReadOnlyList<WebCompletionItem> Items, bool Truncated)
{
    public static WebCompletion Empty(long seq, bool truncated = false) => new(seq, 0, 0, [], truncated);
}

/// <summary>
/// The completion lists as the Web UI's dropdown reads them (later on 2026-10-14): built on the screen's own task (the idle line's top while a
/// page is connected) from what the screen holds — the lists, the argument lists' data, the folder mode — and read on Kestrel's threads.
/// It never refreshes, probes or writes: a source that would (the Home Assistant and Docker lists, the printers) answers what was last read.
/// What the page may not run is never offered (<see cref="WebCommands"/>), the <c>%</c> and <c>*</c> notes go unless <i>Web UI connection
/// details</i>, and <c>@</c> and the paths are empty while <i>File tools</i> is off (the sources say so).
/// </summary>
public sealed class WebCompletionSnapshot(CompletionSources sources, CompletionLists lists, Func<string, bool>? pathArgument, bool folderRemains, WebCommandSettings settings, bool connectionDetails)
{
    /// <summary>The most rows one answer carries.</summary>
    public const int MaxItems = 200;

    /// <summary>The most text the page sends (UTF-16 characters).</summary>
    public const int MaxText = 4096;

    /// <summary>The dropdown for <paramref name="text"/> with the cursor at <paramref name="cursor"/>.</summary>
    public WebCompletion Complete(string text, int cursor, long seq)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxText)
        {
            return WebCompletion.Empty(seq, truncated: true);
        }

        cursor = Math.Clamp(cursor, 0, text.Length);
        var shown = lists with { Commands = lists.Commands && settings.Commands, Arguments = lists.Arguments && settings.Commands };
        if (CompletionQuery.Route(text, cursor, shown, pathArgument) is not { } route)
        {
            return WebCompletion.Empty(seq);
        }

        SlashCommand command = SlashCommand.None;
        if (route.Kind == CompletionKind.Argument)
        {
            command = SlashCommands.Parse(route.Command!).Command;
            if (!WebCommands.Offers(command))
            {
                return WebCompletion.Empty(seq);
            }
        }

        if (CompletionQuery.Fetch(route, sources) is not { } answer)
        {
            return WebCompletion.Empty(seq);
        }

        var items = new List<WebCompletionItem>();
        for (int i = 0; i < answer.Matches.Count && items.Count < MaxItems; i++)
        {
            string match = answer.Matches[i];
            string? note = answer.Notes?[i];
            if (!Offered(route, command, match))
            {
                continue;
            }

            if (route.Trigger is '%' or '*' && !connectionDetails)
            {
                note = null;
            }

            items.Add(new WebCompletionItem(match, string.IsNullOrEmpty(note) ? null : note, Insert(answer, match)));
        }

        bool truncated = answer.Truncated || items.Count >= MaxItems && answer.Matches.Count > items.Count;
        return items.Count == 0 ? WebCompletion.Empty(seq) : new WebCompletion(seq, route.Start, route.End, items, truncated);
    }

    /// <summary>Whether a row may be offered: a command the page may run, an argument whose form it may run (or may with more words after it).</summary>
    private bool Offered(CompletionRoute route, SlashCommand command, string match)
    {
        switch (route.Kind)
        {
            case CompletionKind.Command:
                return WebCommands.Offers(SlashCommands.Parse(match).Command);
            case CompletionKind.Argument:
                string word = route.Command!;
                return WebCommands.Check(command, match, word + " " + match, settings).Allowed
                    || WebCommands.Check(command, match + " x", word + " " + match + " x", settings).Allowed;
            default:
                return true;
        }
    }

    /// <summary>What a pick writes: the mention's character and the match, then a space — none after a folder the list stays open on.</summary>
    private string Insert(CompletionAnswer answer, string match)
    {
        bool folder = !answer.IsWordList && (match.EndsWith('/') || match.EndsWith('\\'));
        return answer.Prefix + match + (folder && folderRemains ? "" : " ");
    }
}
