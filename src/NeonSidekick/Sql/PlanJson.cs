using System.Text.Json;

namespace NeonSidekick.Sql;

/// <summary>
/// The JSON plan readers' shared parts (2026-11-15, the database-tools review's #10 and #131): PostgreSQL's, MySQL's and DuckDB's plans are
/// read through one <see cref="Parse"/> and the same value readers. A plan nests two JSON levels for each step it nests, and the reader's default
/// depth of 64 failed a plan about 30 steps deep into the raw answer, so the depth is <see cref="MaxDepth"/>. Pure.
/// </summary>
public static class PlanJson
{
    /// <summary>The deepest a plan's JSON may nest.</summary>
    public const int MaxDepth = 512;

    /// <summary>The plan's document; throws <see cref="JsonException"/> when it isn't JSON (or nests past <see cref="MaxDepth"/>).</summary>
    public static JsonDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
    }

    /// <summary>A string member, or null when it is missing or not a string.</summary>
    public static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A number member, written as one (PostgreSQL, MariaDB) or as text (MySQL's <c>"1.20"</c>, DuckDB's <c>"40000"</c>); null otherwise.</summary>
    public static double? Number(JsonElement node, string name)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double d) && double.IsFinite(d) => d,
            JsonValueKind.String => QueryPlan.Number(value.GetString()),
            _ => null,
        };
    }

    /// <summary>Whether a member is JSON <c>true</c>.</summary>
    public static bool True(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
