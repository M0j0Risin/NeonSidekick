using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeonSidekick.Llm;

/// <summary>
/// The repair of a leaked parameter tag (2026-10-17, the toki-pona session's log and two before it, 2026-10-12 and 2026-10-14): Qwen,
/// writing its XML tool-call form, closed a parameter with <c>&lt;/parameter=name&gt;</c> where <c>&lt;/parameter&gt;</c> belongs, and
/// llama-server folded the next argument into the first — <c>"action":"create\n&lt;/parameter=name&gt;\ncomfy-image-refinement","name":""</c>.
/// Each time it was <c>skill_editor</c>'s <c>action</c>; once the swallowed argument was a whole file, refused and then (sent again
/// without it) written as nothing. A string value holding the tag is split at it: the text before stays the value, the rest becomes
/// the argument it names — only when that one is in the tool's schema, is another argument, and was not sent, sent empty, or sent as
/// the same text. A different value already there leaves the call as it is, for the tool's own refusal. Only the shape seen is
/// handled; a leaked opening tag never was. Pure.
/// </summary>
public static partial class LeakedParameters
{
    /// <summary>
    /// <paramref name="arguments"/> with every leak <paramref name="schema"/> allows repaired, or the same instance when there is
    /// none; <paramref name="repaired"/> names each repair as <c>action → name</c>, in order.
    /// </summary>
    public static IDictionary<string, object?> Repair(IDictionary<string, object?> arguments, JsonElement schema, out IReadOnlyList<string> repaired)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var names = Properties(schema);
        List<string>? done = null;
        Dictionary<string, object?>? copy = null;
        var pending = new Queue<string>(arguments.Keys);
        while (pending.Count > 0)
        {
            string key = pending.Dequeue();
            var current = copy ?? arguments;
            if (!current.TryGetValue(key, out object? value) || Text(value) is not { } text)
            {
                continue;
            }

            var match = Tag().Match(text);
            if (!match.Success)
            {
                continue;
            }

            string target = match.Groups["name"].Value;
            string before = text[..match.Index].TrimEnd('\r', '\n');
            string rest = Close().Replace(text[(match.Index + match.Length)..], "");
            if (string.Equals(target, key, StringComparison.Ordinal) || !names.Contains(target))
            {
                continue;
            }

            if (current.TryGetValue(target, out object? there) && Text(there) is { } sent && sent.Length > 0 && !string.Equals(sent, rest, StringComparison.Ordinal))
            {
                continue;
            }

            copy ??= new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
            copy[key] = before;
            copy[target] = rest;
            (done ??= []).Add(key + " → " + target);
            pending.Enqueue(target);   // the rest may hold a second leak
        }

        repaired = done ?? [];
        return copy ?? arguments;
    }

    /// <summary>The Info line for a repaired call: <c>Repaired a leaked &lt;/parameter=…&gt; in skill_editor: action → name.</c> Pinned.</summary>
    public static string LogLine(string tool, IReadOnlyList<string> repaired)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(repaired);
        return "Repaired a leaked </parameter=…> in " + tool + ": " + string.Join(", ", repaired) + ".";
    }

    private static string? Text(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };

    private static HashSet<string> Properties(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                names.Add(property.Name);
            }
        }

        return names;
    }

    /// <summary>The leaked closing tag that names the next parameter, with the line breaks around it.</summary>
    [GeneratedRegex(@"\r?\n?</parameter=(?<name>[A-Za-z_][A-Za-z0-9_]*)>\r?\n?", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    /// <summary>A proper closing tag left at the end of the swallowed value.</summary>
    [GeneratedRegex(@"\r?\n?</parameter>\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Close();
}
