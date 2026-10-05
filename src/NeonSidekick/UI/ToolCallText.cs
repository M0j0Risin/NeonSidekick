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
    public static string Brief(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "";
            }

            var brief = new StringBuilder();
            int taken = 0;
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

                brief.Append(value.Trim());
                if (taken == MaxValues)
                {
                    break;
                }
            }

            return brief.ToString();
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>A result's line count beside its first line when folded: <c>(+12 lines)</c>. Pinned.</summary>
    public static string MoreLines(int lines) =>
        "(+" + lines.ToString(CultureInfo.InvariantCulture) + (lines == 1 ? " line)" : " lines)");
}
