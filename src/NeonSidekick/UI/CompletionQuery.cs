using NeonSidekick.Files;

namespace NeonSidekick.UI;

/// <summary>What a completion completes: a command's word, its argument, or a mention (<c>@ # $ % ^ *</c>).</summary>
public enum CompletionKind
{
    Command,
    Argument,
    Mention,
}

/// <summary>The completion lists switched on: the command and argument lists, and each mention's.</summary>
public readonly record struct CompletionLists(bool Commands, bool Arguments, bool At, bool Hash, bool Dollar, bool Percent, bool Caret, bool Star);

/// <summary>
/// Where a completion stands in the text (later on 2026-10-14): what it completes, the span its pick replaces (<see cref="Start"/> to
/// <see cref="End"/>), the text typed there, the command for an argument, and the mention's character (<c>\0</c> for none).
/// </summary>
public sealed record CompletionRoute(CompletionKind Kind, int Start, int End, string Query, string? Command = null, char Trigger = '\0')
{
    /// <summary>What a pick writes ahead of the match: the mention's character, nothing for a command or an argument.</summary>
    public string Prefix => Kind == CompletionKind.Mention ? Trigger.ToString() : "";
}

/// <summary>Where the lists come from: the commands, an argument's list, each mention's list, and the <c>@</c> paths.</summary>
public sealed record CompletionSources(
    Func<IReadOnlyList<CompletionItem>> Commands,
    Func<string, string, ArgumentList> Arguments,
    Func<IReadOnlyList<CompletionItem>> Skills,
    Func<IReadOnlyList<CompletionItem>> Tools,
    Func<IReadOnlyList<CompletionItem>> Connections,
    Func<IReadOnlyList<CompletionItem>> Workflows,
    Func<IReadOnlyList<CompletionItem>> Shares,
    Func<string, MentionResult> Mentions);

/// <summary>
/// A completion's matches: the words with their notes (a word list) or the paths (a path list, <see cref="Notes"/> null, maybe cut short), and
/// what a pick writes ahead of each.
/// </summary>
public sealed record CompletionAnswer(IReadOnlyList<string> Matches, IReadOnlyList<string>? Notes, bool Truncated, string Prefix)
{
    /// <summary>Whether it is a word list (commands, arguments, skills…) rather than paths.</summary>
    public bool IsWordList => Notes is not null;
}

/// <summary>
/// The completion lists' lookup, pure (later on 2026-10-14, out of the input line's editor so the Web UI's dropdown asks it too): <see cref="Route"/>
/// finds what the word under the cursor is — in the editor's order, a command at the start, its argument unless a mention is typed there, then
/// <c># $ % ^ *</c>, then <c>@</c> — and <see cref="Fetch"/> reads its list from the sources. The editor keeps its own dismissal and its open
/// list's reuse around them, so the PC's list and its highlighted row behave as they did.
/// </summary>
public static class CompletionQuery
{
    /// <summary>
    /// What the word at <paramref name="cursor"/> in <paramref name="text"/> completes, or null for nothing. <paramref name="pathArgument"/> says
    /// which commands take a path of their own (their argument never completes a mention).
    /// </summary>
    public static CompletionRoute? Route(string text, int cursor, CompletionLists lists, Func<string, bool>? pathArgument)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        if (lists.Commands && MentionCompleter.TryFindCommand(text, cursor, out int commandEnd, out string commandQuery))
        {
            return new CompletionRoute(CompletionKind.Command, 0, commandEnd, commandQuery);
        }

        if (lists.Arguments && MentionCompleter.TryFindArgument(text, cursor, out string command, out int start, out int end, out string query)
            && !MentionInArgument(text, cursor, command, lists, pathArgument))
        {
            return new CompletionRoute(CompletionKind.Argument, start, end, query, command);
        }

        foreach (var (on, trigger) in new[] { (lists.Hash, '#'), (lists.Dollar, '$'), (lists.Percent, '%'), (lists.Caret, '^'), (lists.Star, '*') })
        {
            if (on && MentionCompleter.TryFind(text, cursor, trigger, out start, out end, out query))
            {
                return new CompletionRoute(CompletionKind.Mention, start, end, query, Trigger: trigger);
            }
        }

        return lists.At && MentionCompleter.TryFind(text, cursor, out start, out end, out query)
            ? new CompletionRoute(CompletionKind.Mention, start, end, query, Trigger: '@')
            : null;
    }

    /// <summary>
    /// The list for <paramref name="route"/>, or null when it has nothing: the commands and the mentions' words narrowed by what is typed, an
    /// argument's list as its source answers it (it narrows itself, and may answer paths), the <c>@</c> paths.
    /// </summary>
    public static CompletionAnswer? Fetch(CompletionRoute route, CompletionSources sources)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(sources);
        IReadOnlyList<CompletionItem> words;
        switch (route.Kind)
        {
            case CompletionKind.Command:
                words = MentionCompleter.Matches(sources.Commands(), route.Query);
                break;
            case CompletionKind.Argument:
                var answer = sources.Arguments(route.Command!, route.Query);
                if (answer.IsPathList)
                {
                    return answer.Paths!.Count == 0 ? null : new CompletionAnswer(answer.Paths, null, answer.Truncated, "");
                }

                words = answer.Words;
                break;
            case CompletionKind.Mention when route.Trigger == '@':
                var found = sources.Mentions(route.Query);
                return found.Paths.Count == 0 ? null : new CompletionAnswer(found.Paths, null, found.Truncated, "@");
            default:
                var list = route.Trigger switch
                {
                    '#' => sources.Skills(),
                    '$' => sources.Tools(),
                    '%' => sources.Connections(),
                    '^' => sources.Workflows(),
                    _ => sources.Shares(),
                };
                words = MentionCompleter.Matches(list, route.Query);
                break;
        }

        return words.Count == 0 ? null : new CompletionAnswer([.. words.Select(i => i.Text)], [.. words.Select(i => i.Note)], false, route.Prefix);
    }

    /// <summary>
    /// Whether the word under the cursor in a slash command's text is a mention to complete as one (2026-09-30, the user's ask:
    /// <c>/loop infinite 1s append the time to @file.txt</c> completes the <c>@</c> as a message would): its character's list is on, and the
    /// command's argument is no path of its own (<c>/read</c>, <c>/view</c>, <c>/print</c> keep their file list and <c>/tree</c>,
    /// <c>/explore</c>, <c>/vault</c> their folder list, and none of them takes an <c>@</c>).
    /// </summary>
    public static bool MentionInArgument(string text, int cursor, string command, CompletionLists lists, Func<string, bool>? pathArgument) =>
        MentionCompleter.TriggerAt(text, cursor) switch
        {
            '@' => lists.At,
            '#' => lists.Hash,
            '$' => lists.Dollar,
            '%' => lists.Percent,
            '^' => lists.Caret,
            '*' => lists.Star,
            _ => false,
        }
        && pathArgument?.Invoke(command) != true;
}
