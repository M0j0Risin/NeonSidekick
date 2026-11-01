using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.UI;

/// <summary>
/// A tool call as its 🛠️ line names it (2026-10-04, the UI review: the line showed the raw JSON of the arguments, or nothing at all):
/// the call's values, not its JSON — <c>read_file notes.md</c>, <c>run_command dir /b · 30</c>. Read over <see cref="JsonDocument"/>
/// (no serializer context). Pure; the shape is pinned.
/// </summary>
public static class ToolCallText
{
    /// <summary>The most values a brief shows.</summary>
    public const int MaxValues = 3;

    /// <summary>What stands between two values. Pinned.</summary>
    public const string Separator = " · ";

    /// <summary>
    /// The first <see cref="MaxValues"/> plain values of <paramref name="argumentsJson"/>'s object, in its order: a string as it is (its
    /// line breaks as spaces), a number or a boolean as written; arrays, objects and nulls left out. Empty for no arguments, an empty
    /// object or text that is no JSON object.
    /// </summary>
    public static string Brief(string? argumentsJson) => BriefWithTarget(argumentsJson).Brief;

    /// <summary>The argument keys whose value a tool line links (2026-11-01, clickable links and paths), in the order they are looked for.</summary>
    public static readonly IReadOnlyList<string> TargetKeys = ["url", "path"];

    /// <summary>
    /// <see cref="Brief"/> and the call's link target (2026-11-01, clickable links and paths): the first of <see cref="TargetKeys"/> that holds
    /// a string, its key, its full value (as the arguments hold it, trimmed) and where that value sits in the brief (start and length), or
    /// −1 and 0 when it is not among the values shown. Key and value null when the call has no such argument.
    /// </summary>
    public static BriefTarget BriefWithTarget(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return BriefTarget.None;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return BriefTarget.None;
            }

            string? key = null;
            string? full = null;
            foreach (string wanted in TargetKeys)
            {
                if (document.RootElement.TryGetProperty(wanted, out var found) && found.ValueKind == JsonValueKind.String
                    && found.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    key = wanted;
                    full = text.Trim();
                    break;
                }
            }

            var brief = new StringBuilder();
            int taken = 0;
            int start = -1, length = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                string? value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()?.ReplaceLineEndings(" "),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (taken++ > 0)
                {
                    brief.Append(Separator);
                }

                if (start < 0 && key is not null && string.Equals(property.Name, key, StringComparison.Ordinal))
                {
                    start = brief.Length;
                    length = value.Trim().Length;
                }

                brief.Append(value.Trim());
                if (taken == MaxValues)
                {
                    break;
                }
            }

            return new BriefTarget(brief.ToString(), key, full, start, length);
        }
        catch (JsonException)
        {
            return BriefTarget.None;
        }
    }

    /// <summary>What <see cref="BriefWithTarget"/> found: the brief, the target's key and full value, and its place in the brief (−1 = not shown).</summary>
    public readonly record struct BriefTarget(string Brief, string? Key, string? Value, int Start, int Length)
    {
        /// <summary>No arguments: an empty brief, no target.</summary>
        public static BriefTarget None => new("", null, null, -1, 0);
    }

    /// <summary>A result's line count beside its first line when folded: <c>(+12 lines)</c>. Pinned.</summary>
    public static string MoreLines(int lines) =>
        "(+" + lines.ToString(CultureInfo.InvariantCulture) + (lines == 1 ? " line)" : " lines)");
}
