using NeonSidekick.UI;

namespace NeonSidekick.Shopping;

/// <summary>A <c>/shopping</c> verb (2026-10-11).</summary>
public enum ShoppingVerb
{
    /// <summary>The bare word: the list's pane (the lines printed without one).</summary>
    Pane,

    /// <summary><c>list [&lt;store&gt;]</c>: the list printed, walked through the store.</summary>
    List,

    /// <summary><c>add &lt;item&gt;[, &lt;item&gt;…]</c>.</summary>
    Add,

    /// <summary><c>check &lt;item&gt;</c> (or <c>tick</c>).</summary>
    Check,

    /// <summary><c>uncheck &lt;item&gt;</c> (or <c>untick</c>).</summary>
    Uncheck,

    /// <summary><c>remove &lt;item&gt;</c>.</summary>
    Remove,

    /// <summary><c>clear</c>: the ticked lines.</summary>
    Clear,

    /// <summary><c>clear all</c>: the whole list, after a yes.</summary>
    ClearAll,

    /// <summary><c>store</c> alone or <c>stores</c>: the stores, the active one marked; <c>store &lt;name&gt;</c> makes one the active store.</summary>
    Stores,
    UseStore,

    /// <summary><c>store add &lt;name&gt;</c>: a new store.</summary>
    AddStore,

    /// <summary><c>store remove &lt;name&gt;</c>: a store and its layout gone, after a yes (the model cannot).</summary>
    RemoveStore,

    /// <summary><c>export [&lt;store&gt;] [verbose]</c>: the list as Markdown in the working directory's stamped file (<see cref="ShoppingText.ExportFileName"/>), compact unless verbose.</summary>
    Export,

    /// <summary><c>print [&lt;store&gt;] [verbose]</c>: the list printed, through <c>/print</c>'s service, compact unless verbose.</summary>
    Print,

    /// <summary><c>reindex [&lt;store&gt;]</c>: the store's reference documents read again, every file (2026-10-11, phase 3).</summary>
    Reindex,

    /// <summary>A line no form takes (the error is the usage).</summary>
    Unknown,
}

/// <summary>
/// A <c>/shopping</c> line read: the verb, its text (the items, the item, the store's name), the error for a line that is no command,
/// and <see cref="Verbose"/> for an <c>export</c> or <c>print</c> asked for in the roomy form (2026-10-11).
/// </summary>
public sealed record ShoppingCommandLine(ShoppingVerb Verb, string Text = "", string? Error = null, bool Verbose = false);

/// <summary>
/// <c>/shopping</c>'s words (2026-10-11, the user's ask), pure: <see cref="Parse"/> the line, <see cref="Complete"/> for the input line's
/// argument list. The user's own hand on the list, so no switch and no plan mode judges it; what the model's tools say, it says.
/// </summary>
public static class ShoppingCommand
{
    public const string Word = "/shopping";

    /// <summary>The words in the order the completion offers them, with their notes. Pinned.</summary>
    public static readonly IReadOnlyList<(string Word, string Note)> Words =
    [
        ("add", "put items on the list: /shopping add milk, 2 eggs"),
        ("check", "tick an item as bought: /shopping check <item|n>"),
        ("uncheck", "untick an item: /shopping uncheck <item|n>"),
        ("remove", "take an item off the list: /shopping remove <item|n>"),
        ("list", "print the list walked through the store: /shopping list [<store>]"),
        ("clear", "remove the ticked items; /shopping clear all empties the list, after a yes"),
        ("store", "the stores; /shopping store <name> shops there, store add|remove <name>"),
        ("export", "write the list to " + ShoppingText.ExportFilePattern + ": /shopping export [<store>] [verbose]"),
        ("print", "print the list, compact: /shopping print [<store>] [verbose]"),
        ("reindex", "read the store's reference documents again: /shopping reindex [<store>]"),
    ];

    public static ShoppingCommandLine Parse(string? args)
    {
        string text = (args ?? "").Trim();
        if (text.Length == 0)
        {
            return new(ShoppingVerb.Pane);
        }

        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string word = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        return word switch
        {
            "list" => new(ShoppingVerb.List, rest),
            "add" when rest.Length > 0 => new(ShoppingVerb.Add, rest),
            "check" or "tick" when rest.Length > 0 => new(ShoppingVerb.Check, rest),
            "uncheck" or "untick" when rest.Length > 0 => new(ShoppingVerb.Uncheck, rest),
            "remove" when rest.Length > 0 => new(ShoppingVerb.Remove, rest),
            "clear" when rest.Length == 0 => new(ShoppingVerb.Clear),
            "clear" when rest.Equals("all", StringComparison.OrdinalIgnoreCase) => new(ShoppingVerb.ClearAll),
            "stores" when rest.Length == 0 => new(ShoppingVerb.Stores),
            "store" => Store(rest),
            "export" => Output(ShoppingVerb.Export, rest),
            "print" => Output(ShoppingVerb.Print, rest),
            "reindex" => new(ShoppingVerb.Reindex, rest),
            _ => Usage(),
        };
    }

    /// <summary>The word after an <c>export</c> or <c>print</c> (and a store's name) that asks for the roomy form. Pinned.</summary>
    public const string VerboseWord = "verbose";

    /// <summary><c>export</c> or <c>print</c>'s rest (2026-10-11): a last word <see cref="VerboseWord"/> (any case) is the roomy form, what is left the store's name (blank: the active store).</summary>
    private static ShoppingCommandLine Output(ShoppingVerb verb, string rest)
    {
        int space = rest.LastIndexOf(' ');
        string last = space < 0 ? rest : rest[(space + 1)..];
        bool verbose = last.Equals(VerboseWord, StringComparison.OrdinalIgnoreCase);
        return new(verb, verbose ? (space < 0 ? "" : rest[..space].Trim()) : rest, Verbose: verbose);
    }

    private static ShoppingCommandLine Store(string rest)
    {
        if (rest.Length == 0)
        {
            return new(ShoppingVerb.Stores);
        }

        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        string word = (space < 0 ? rest : rest[..space]).ToLowerInvariant();
        string name = space < 0 ? "" : rest[(space + 1)..].Trim();
        return word switch
        {
            "add" when name.Length > 0 => new(ShoppingVerb.AddStore, name),
            "remove" when name.Length > 0 => new(ShoppingVerb.RemoveStore, name),
            "add" or "remove" => Usage(),
            _ => new(ShoppingVerb.UseStore, rest),
        };
    }

    /// <summary>
    /// The items of an <c>add</c>: split at commas and semicolons, blanks dropped, each read by <see cref="ShoppingList.ParseLine"/>
    /// when it is added.
    /// </summary>
    public static IReadOnlyList<string> Items(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// The input line's argument list after <c>/shopping </c>: the verbs while the first word is typed; after <c>store </c>, its
    /// <c>add</c> and <c>remove</c> and the stores' names; after <c>check</c>, <c>uncheck</c> or <c>remove</c>, the list's lines.
    /// </summary>
    public static IReadOnlyList<CompletionItem> Complete(string argText, IReadOnlyList<string> stores, IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(argText);
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(items);
        int space = argText.IndexOf(' ', StringComparison.Ordinal);
        if (space < 0)
        {
            return MentionCompleter.Matches(Words.Select(w => new CompletionItem(w.Word, w.Note)).ToList(), argText);
        }

        string word = argText[..space].ToLowerInvariant();
        string head = argText[..(space + 1)];
        return word switch
        {
            "store" => MentionCompleter.Matches(
                [new(head + "add ", "a new store: /shopping store add <name>"), new(head + "remove ", "a store and its layout gone, after a yes"), .. stores.Select(s => new CompletionItem(head + s, "shop at " + s))],
                argText),
            "check" or "tick" or "uncheck" or "untick" or "remove" => MentionCompleter.Matches(items.Select(i => new CompletionItem(head + i, "")).ToList(), argText),
            "list" => MentionCompleter.Matches(stores.Select(s => new CompletionItem(head + s, "the list walked through " + s)).ToList(), argText),
            "reindex" => MentionCompleter.Matches(stores.Select(s => new CompletionItem(head + s, "read " + s + "'s documents again")).ToList(), argText),
            "export" or "print" => MentionCompleter.Matches([new(head + VerboseWord, "the roomy form: a heading per aisle, a box per item"), .. stores.Select(s => new CompletionItem(head + s, "the list walked through " + s))], argText),
            _ => [],
        };
    }

    private static ShoppingCommandLine Usage() => new(ShoppingVerb.Unknown, Error: ShoppingText.CommandUsage);
}
