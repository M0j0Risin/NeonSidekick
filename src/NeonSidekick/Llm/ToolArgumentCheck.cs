using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Llm;

/// <summary>What <see cref="ToolArgumentCheck"/> made of a call's arguments.</summary>
public enum ArgumentVerdict
{
    /// <summary>Every key is the tool's (or the tool can't be judged, or the rest are harmless): the call runs as sent.</summary>
    Pass,

    /// <summary>A key the tool doesn't take: nothing runs, and the model is answered <see cref="ArgumentCheck.Text"/>.</summary>
    Refused,
}

/// <summary>The undeclared names a tool reads on purpose (<see cref="IAcceptsArguments"/>).</summary>
public sealed record ToolExtras(IReadOnlyCollection<string> Names)
{
    /// <summary>None: the schema alone.</summary>
    public static readonly ToolExtras None = new([]);
}

/// <summary>
/// The check's verdict: for <see cref="ArgumentVerdict.Refused"/>, the answer; for a pass that left keys unread (a tool that takes no
/// arguments, or a commentary key), the <see cref="Note"/> its result ends with.
/// </summary>
public sealed record ArgumentCheck(ArgumentVerdict Verdict, string? Text = null, string? Note = null)
{
    public static readonly ArgumentCheck Passed = new(ArgumentVerdict.Pass);
}

/// <summary>
/// Misnamed arguments answered (2026-11-15, feature idea #42): a call carrying a key its tool does not declare was run as if the key
/// weren't there — <c>web_search {limit: 5}</c> ran unfiltered, and the model took the result as filtered. <see cref="Assistant.InvokeToolWithDiffAsync"/>
/// asks here before the tool runs, after the stub and cut-marker refusals. An unknown key refuses the call, each key named with the name it
/// likely meant (<see cref="Hints"/>, a case-only twin, then the nearest declared name not already sent) and the tool's own list. Nothing is
/// ever renamed (the tool-arguments review, the user's call: a guessed count could run a statement or a search wider than asked). A key with
/// a harmless value (null, <c>""</c>, <c>false</c>, <c>[]</c>, <c>{}</c>) is ignored silently; a commentary key (<see cref="CommentaryKeys"/>)
/// is ignored and named in the result's note. A tool with no arguments at all runs anyway, its result noting what went unread. An MCP tool is
/// judged only by a closed, plain schema. Pure; reads the schema it is given, so the caller reads a dynamic one once.
/// </summary>
public static class ToolArgumentCheck
{
    /// <summary>
    /// The names that mean "how many" (2026-11-15): a key here is hinted toward the one member a tool declares, never renamed onto it.
    /// <c>count</c>, <c>top</c> and <c>max_lines</c> are left out — they mean other things in some tools.
    /// </summary>
    public static readonly IReadOnlyList<string> CountGroup = ["limit", "max_results", "max", "max_commits", "max_rows", "tail"];

    /// <summary>
    /// The names other agents use for the app's own (2026-11-15, the review's #74, #118): a sent key, exactly, to the declared names it likely
    /// means, first declared one wins. Hints only. Claude Code's <c>read_file {offset, limit}</c> and <c>old_string</c>/<c>new_string</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Hints = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["offset"] = ["start_line"],
        ["limit"] = ["max_lines"],
        ["old_string"] = ["old_text"],
        ["new_string"] = ["new_text"],
        ["file_path"] = ["path"],
    };

    /// <summary>
    /// Keys a model adds to explain itself (2026-11-15, the review's #55): never an instruction, so a tool that doesn't declare one runs, and
    /// the note names it.
    /// </summary>
    public static readonly IReadOnlySet<string> CommentaryKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "description", "explanation", "reason", "reasoning", "thought", "comment",
    };

    /// <summary>
    /// The tools whose <c>max_rows</c> limits only the rows shown (2026-11-15, the review's #41): a count name is never hinted onto it, since a
    /// statement "limited to 100" would still run on every row.
    /// </summary>
    public static readonly IReadOnlySet<string> RowsShownOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        SqlExecuteTool.ToolName, OracleExecuteTool.ToolName, MySqlExecuteTool.ToolName, PostgresExecuteTool.ToolName,
        SqliteExecuteTool.ToolName, DuckDbExecuteTool.ToolName,
    };

    /// <summary>The most misnamed keys one answer names; the rest are counted.</summary>
    public const int MaxNamed = 5;

    /// <summary>The most of the tool's own names one answer lists (2026-11-15, #76: an MCP server's 300); the rest are counted.</summary>
    public const int MaxTakes = 20;

    /// <summary>
    /// The longest key the nearest-name search runs on (the edit distance is the key's length times each name's, and a key matches only within
    /// a third of its length): a longer one, well past any schema's name, goes straight to the list.
    /// </summary>
    public const int MaxNearestChars = 64;

    /// <summary>The check for <paramref name="tool"/> under <paramref name="schema"/>, its schema as the caller read it once (#98).</summary>
    public static ArgumentCheck Check(AIFunction tool, JsonElement schema, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var extras = tool is IAcceptsArguments accepts ? new ToolExtras(accepts.ExtraArguments) : ToolExtras.None;
        return Check(schema, tool is Mcp.McpTool, extras, tool.Name, arguments);
    }

    /// <summary>The check itself, pure, for a schema as given (tests drive each rule through here).</summary>
    internal static ArgumentCheck Check(JsonElement schema, bool mcp, ToolExtras extras, string name, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentNullException.ThrowIfNull(name);
        if (arguments is null || arguments.Count == 0 || !Judged(schema, mcp, out var properties))
        {
            return ArgumentCheck.Passed;
        }

        // The ordered list for "It takes:" and the nearest search's ties; the sets for the lookups (#72).
        var declared = properties.EnumerateObject().Select(p => p.Name).ToList();
        var declaredSet = new HashSet<string>(declared, StringComparer.Ordinal);
        var extraSet = extras.Names as IReadOnlySet<string> ?? new HashSet<string>(extras.Names, StringComparer.Ordinal);
        var unknown = new List<string>();
        var ignored = new List<string>();
        foreach (var (key, value) in arguments)
        {
            if (declaredSet.Contains(key) || extraSet.Contains(key) || Harmless(value))
            {
                continue;
            }

            (CommentaryKeys.Contains(key) ? ignored : unknown).Add(key);
        }

        if (unknown.Count == 0 && ignored.Count == 0)
        {
            return ArgumentCheck.Passed;
        }

        if (declared.Count == 0 || unknown.Count == 0)
        {
            // Nothing could have been filtered (no arguments at all), or only commentary went unread: run it, and say what went unread.
            var unread = declared.Count == 0 ? [.. unknown, .. ignored] : ignored;
            var shownKeys = unread.Take(MaxNamed).Select(ArgumentText.Echo).ToList();
            return ArgumentCheck.Passed with { Note = TurnText.IgnoredArguments(name, shownKeys, unread.Count - shownKeys.Count, takesArguments: declared.Count > 0) };
        }

        var sent = declared.Where(d => arguments.TryGetValue(d, out var value) && ToolArguments.IsGiven(value)).ToHashSet(StringComparer.Ordinal);
        bool synonymsSentTwice = unknown.Count(k => CountGroup.Contains(k, StringComparer.Ordinal)) > 1;
        bool rowsShownOnly = RowsShownOnly.Contains(name);
        var named = unknown.Take(MaxNamed).Select(key => (ArgumentText.Echo(key), Hint(key, declared, sent, rowsShownOnly, synonymsSentTwice))).ToList();
        var takes = declared.Take(MaxTakes).Select(ArgumentText.Echo).ToList();
        return new ArgumentCheck(ArgumentVerdict.Refused, TurnText.UnknownArguments(name, named, unknown.Count - named.Count, takes, declared.Count - takes.Count));
    }

    /// <summary>
    /// The hint beside an unknown <paramref name="key"/>, or null (2026-11-15; public since the tool-arguments review's #12, so an <c>edits</c>
    /// item's unknown key is answered the same way): a case-only twin first, against every declared name, so a sent <c>path</c> doesn't send
    /// <c>Path</c> on to <c>paths</c> (#121, #158); a count name toward the tool's one (or, on a statement tool, the rows-shown note, #41);
    /// another agent's name (<see cref="Hints"/>); last the nearest declared name not already <paramref name="sent"/> (#80).
    /// </summary>
    public static string? Hint(string key, IReadOnlyList<string> declared, IReadOnlySet<string> sent, bool rowsShownOnly = false, bool synonymsSentTwice = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(sent);
        if (declared.FirstOrDefault(d => string.Equals(d, key, StringComparison.OrdinalIgnoreCase)) is { } twin)
        {
            return sent.Contains(twin) ? TurnText.CaseTwinHint(ArgumentText.Echo(key), twin) : TurnText.NearestHint(twin);
        }

        if (CountGroup.Contains(key, StringComparer.Ordinal))
        {
            var counts = declared.Where(d => CountGroup.Contains(d, StringComparer.Ordinal)).ToList();
            if (rowsShownOnly)
            {
                return TurnText.RowsShownHint(counts.FirstOrDefault() ?? "max_rows");
            }

            if (counts.Count == 1)
            {
                return sent.Contains(counts[0]) || synonymsSentTwice ? TurnText.BothSentHint(counts[0]) : TurnText.SynonymHint(counts[0]);
            }
        }

        if (Hints.TryGetValue(key, out var likely) && likely.FirstOrDefault(declared.Contains) is { } meant)
        {
            return TurnText.NearestHint(meant);
        }

        if (key.Length > MaxNearestChars)
        {
            return null;
        }

        string? nearest = NearestName.Nearest(key, declared.Where(d => !sent.Contains(d)));
        return nearest is null ? null : TurnText.NearestHint(nearest);
    }

    /// <summary>Whether an undeclared key is left aside without a word: a harmless value (#55), or a commentary key (an <c>edits</c> item's, #12).</summary>
    public static bool Unread(string key, object? value) => Harmless(value) || CommentaryKeys.Contains(key);

    /// <summary>
    /// Whether an unknown key's value is harmless to leave unread (2026-11-15, the review's #55): absent in effect — null, a JSON null,
    /// <c>""</c>, <c>false</c>, an empty list or object — so a schema-filling model isn't refused for keys that would change nothing.
    /// </summary>
    public static bool Harmless(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        bool b => !b,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => true,
            JsonValueKind.String => element.GetString()!.Length == 0,
            JsonValueKind.Array => element.GetArrayLength() == 0,
            JsonValueKind.Object => !element.EnumerateObject().Any(),
            _ => false,
        },
        System.Collections.ICollection collection => collection.Count == 0,
        _ => false,
    };

    /// <summary>
    /// Whether the schema can be judged, and its <c>properties</c>. Never without a <c>properties</c> object, with <c>additionalProperties</c>
    /// set to anything but <c>false</c>, or with a top-level keyword that admits or shapes names beyond <c>properties</c>
    /// (<c>oneOf</c>/<c>anyOf</c>/<c>allOf</c>/<c>$ref</c>, and since 2026-11-15, #60, <c>patternProperties</c>, <c>if</c>/<c>then</c>/<c>else</c>,
    /// <c>dependentSchemas</c>, <c>unevaluatedProperties</c>; <c>propertyNames</c> only narrows, so it stays closed). An MCP tool is judged
    /// only when its server said <c>additionalProperties: false</c> (#2: servers often read undeclared keywords), and never with no properties:
    /// <see cref="Mcp.McpTool"/> forwards every key as it is, so "ignored" would claim what the server may not do.
    /// </summary>
    private static bool Judged(JsonElement schema, bool mcp, out JsonElement properties)
    {
        properties = default;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        bool closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        if (!closed && (mcp || schema.TryGetProperty("additionalProperties", out _)))
        {
            return false;
        }

        foreach (string open in OpenKeywords)
        {
            if (schema.TryGetProperty(open, out _))
            {
                return false;
            }
        }

        return !mcp || properties.EnumerateObject().Any();
    }

    private static readonly string[] OpenKeywords =
        ["oneOf", "anyOf", "allOf", "$ref", "patternProperties", "if", "then", "else", "dependentSchemas", "unevaluatedProperties"];
}
