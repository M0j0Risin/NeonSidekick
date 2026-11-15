using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Computers;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Llm;

/// <summary>What <see cref="ToolArgumentCheck"/> made of a call's arguments.</summary>
public enum ArgumentVerdict
{
    /// <summary>Every key is the tool's (or the tool can't be judged): the call runs as sent.</summary>
    Pass,

    /// <summary>A count synonym was read as the tool's own name: the call runs with <see cref="ArgumentCheck.Arguments"/>.</summary>
    Renamed,

    /// <summary>A key the tool doesn't take: nothing runs, and the model is answered <see cref="ArgumentCheck.Text"/>.</summary>
    Refused,
}

/// <summary>The undeclared names a tool reads on purpose (<see cref="IAcceptsArguments"/>).</summary>
public sealed record ToolExtras(IReadOnlyCollection<string> Names, bool Any)
{
    /// <summary>None: the schema alone.</summary>
    public static readonly ToolExtras None = new([], false);
}

/// <summary>
/// The check's verdict: for <see cref="ArgumentVerdict.Renamed"/>, the rewritten arguments and each rename; for
/// <see cref="ArgumentVerdict.Refused"/>, the answer; for a pass on a tool that takes no arguments and was sent some, the
/// <see cref="Note"/> its result ends with.
/// </summary>
public sealed record ArgumentCheck(ArgumentVerdict Verdict, IDictionary<string, object?>? Arguments = null, string? Text = null, string? Note = null)
{
    public static readonly ArgumentCheck Passed = new(ArgumentVerdict.Pass);

    /// <summary>The count synonyms read as the tool's own names, in the order sent.</summary>
    public IReadOnlyList<(string From, string To)> Renames { get; init; } = [];
}

/// <summary>
/// Misnamed arguments answered (2026-11-15, feature idea #42): a call carrying a key its tool does not declare was run as if the key
/// weren't there — <c>web_search {limit: 5}</c> ran unfiltered, and the model took the result as filtered. <see cref="Assistant.InvokeToolWithDiffAsync"/>
/// asks here before the tool runs, after the stub and cut-marker refusals. A key in the count group the tool names exactly one member of is read as
/// that member; any other unknown key refuses the call, naming the nearest real name and the tool's whole list. A key sent as JSON null is not
/// given (<see cref="ToolArguments.Has"/>) and passes. A tool with no arguments at all runs anyway, its result noting what went unread. An MCP tool
/// is judged by its server's schema, and skipped where that schema can't be trusted to be whole. Pure; reads the schema at call time, since
/// several change with settings.
/// </summary>
public static class ToolArgumentCheck
{
    /// <summary>
    /// The names that mean "how many": a key here is read as the one member a tool declares. <c>count</c>, <c>top</c> and <c>max_lines</c>
    /// are left out — they mean other things in some tools.
    /// </summary>
    public static readonly IReadOnlyList<string> CountGroup = ["limit", "max_results", "max", "max_commits", "max_rows", "tail"];

    /// <summary>The most misnamed keys one answer names; the rest are counted.</summary>
    public const int MaxNamed = 5;

    /// <summary>
    /// The longest key the nearest-name search runs on (the edit distance is the key's length times each name's, and a key matches only within
    /// a third of its length): a longer one, well past any schema's name, goes straight to the list.
    /// </summary>
    public const int MaxNearestChars = 64;

    /// <summary>The check for <paramref name="tool"/>: its schema, whether it is an MCP tool, and the names it reads on purpose.</summary>
    public static ArgumentCheck Check(AIFunction tool, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var extras = tool is IAcceptsArguments accepts ? new ToolExtras(accepts.ExtraArguments, accepts.AcceptsAnyArgument) : ToolExtras.None;
        return Check(tool.JsonSchema, tool is Mcp.McpTool, extras, tool.Name, arguments);
    }

    /// <summary>The check itself, pure, for a schema as given (tests drive each rule through here).</summary>
    internal static ArgumentCheck Check(JsonElement schema, bool mcp, ToolExtras extras, string name, IDictionary<string, object?>? arguments)
    {
        ArgumentNullException.ThrowIfNull(extras);
        ArgumentNullException.ThrowIfNull(name);
        if (arguments is null || arguments.Count == 0 || extras.Any || !Judged(schema, mcp, out var properties))
        {
            return ArgumentCheck.Passed;
        }

        var declared = properties.EnumerateObject().Select(p => p.Name).ToList();
        var unknown = arguments.Where(a => Given(a.Value) && !declared.Contains(a.Key, StringComparer.Ordinal) && !extras.Names.Contains(a.Key, StringComparer.Ordinal))
            .Select(a => a.Key).ToList();
        if (unknown.Count == 0)
        {
            return ArgumentCheck.Passed;
        }

        if (declared.Count == 0)
        {
            // Nothing could have been filtered: run it, and say what went unread. An MCP tool never gets here (Judged).
            var shownKeys = unknown.Take(MaxNamed).Select(ArgumentText.Echo).ToList();
            return ArgumentCheck.Passed with { Note = TurnText.IgnoredArguments(name, shownKeys, unknown.Count - shownKeys.Count) };
        }

        var counts = declared.Where(d => CountGroup.Contains(d, StringComparer.Ordinal)).ToList();
        string? countTarget = counts.Count == 1 ? counts[0] : null;
        bool targetSent = countTarget is not null && arguments.TryGetValue(countTarget, out var targetValue) && Given(targetValue);
        bool synonymsSentTwice = unknown.Count(k => CountGroup.Contains(k, StringComparer.Ordinal)) > 1;
        bool Renameable(string key) => countTarget is not null && !targetSent && !synonymsSentTwice && CountGroup.Contains(key, StringComparer.Ordinal);

        if (unknown.TrueForAll(Renameable))
        {
            var rewritten = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
            string key = unknown[0];
            rewritten[countTarget!] = rewritten[key];
            rewritten.Remove(key);
            return new ArgumentCheck(ArgumentVerdict.Renamed, rewritten) { Renames = [(key, countTarget!)] };
        }

        var named = unknown.Take(MaxNamed).Select(key => (ArgumentText.Echo(key), Hint(key))).ToList();
        return new ArgumentCheck(ArgumentVerdict.Refused, Text: TurnText.UnknownArguments(name, named, unknown.Count - named.Count, declared));

        string? Hint(string key)
        {
            if (countTarget is not null && CountGroup.Contains(key, StringComparer.Ordinal))
            {
                return targetSent || synonymsSentTwice ? TurnText.BothSentHint(countTarget) : TurnText.SynonymHint(countTarget);
            }

            if (key.Length > MaxNearestChars)
            {
                return null;
            }

            // A near miss of a count synonym ("limt") is answered with the tool's own member of the group.
            IEnumerable<string> candidates = countTarget is null ? declared : declared.Concat(CountGroup.Except(declared, StringComparer.Ordinal));
            string? nearest = ConfigChecks.Nearest(key, candidates);
            if (nearest is not null && countTarget is not null && CountGroup.Contains(nearest, StringComparer.Ordinal))
            {
                nearest = countTarget;
            }

            return nearest is null ? null : TurnText.NearestHint(nearest);
        }
    }

    /// <summary>
    /// Whether the schema can be judged, and its <c>properties</c>. Never without a <c>properties</c> object, with <c>additionalProperties</c>
    /// set to anything but <c>false</c>, or with a top-level <c>oneOf</c>/<c>anyOf</c>/<c>allOf</c>/<c>$ref</c>. An MCP tool with no properties is
    /// skipped too: servers often under-declare, and <see cref="Mcp.McpTool"/> forwards every key as it is, so "ignored" would claim what the
    /// server may not do.
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

        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        foreach (string combinator in (string[])["oneOf", "anyOf", "allOf", "$ref"])
        {
            if (schema.TryGetProperty(combinator, out _))
            {
                return false;
            }
        }

        return !mcp || properties.EnumerateObject().Any();
    }

    /// <summary>Sent and not JSON null, as <see cref="ToolArguments.Has"/> reads it.</summary>
    private static bool Given(object? value) => value is not null and not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
}
