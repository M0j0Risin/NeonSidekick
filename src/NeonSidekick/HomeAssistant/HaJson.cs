using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NeonSidekick.HomeAssistant;

/// <summary>
/// The small JSON reads and writes the Home Assistant code needs (2026-09-28): request bodies written with a
/// <see cref="Utf8JsonWriter"/> and answers read out of a <see cref="JsonElement"/> — no serializer, so nothing for the
/// AOT rules to object to and no DTO for a shape Home Assistant is free to grow. Pure.
/// </summary>
public static class HaJson
{
    /// <summary>A JSON object as text, its members written by <paramref name="members"/>.</summary>
    public static string Object(Action<Utf8JsonWriter> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary><c>entity_id</c>: one id as a string, several as an array — the two shapes every service takes.</summary>
    public static void WriteEntityIds(Utf8JsonWriter writer, IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 1)
        {
            writer.WriteString("entity_id", ids[0]);
            return;
        }

        writer.WriteStartArray("entity_id");
        foreach (string id in ids)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// A service call's body: <c>entity_id</c> (when any) and then every member of <paramref name="data"/> (a JSON object's
    /// text, or null) but an <c>entity_id</c> of its own — the ids resolved here win. Null with <paramref name="error"/> when
    /// <paramref name="data"/> is not a JSON object.
    /// </summary>
    public static string? ServiceBody(IReadOnlyList<string> ids, string? data, out string? error)
    {
        ArgumentNullException.ThrowIfNull(ids);
        error = null;
        JsonDocument? document = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(data))
            {
                try
                {
                    document = JsonDocument.Parse(data);
                }
                catch (JsonException)
                {
                    error = HaText.BadData;
                    return null;
                }

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = HaText.BadData;
                    return null;
                }
            }

            var root = document?.RootElement;
            return Object(w =>
            {
                if (ids.Count > 0)
                {
                    WriteEntityIds(w, ids);
                }

                if (root is { } members)
                {
                    foreach (var member in members.EnumerateObject())
                    {
                        if (ids.Count > 0 && string.Equals(member.Name, "entity_id", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        member.WriteTo(w);
                    }
                }
            });
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>A string member, or null when absent or not a string.</summary>
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A number member, or null when absent or not a number.</summary>
    public static double? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) ? number : null;

    /// <summary>A boolean member, or null when absent or not a boolean.</summary>
    public static bool? Boolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>A member of strings as a list; empty when absent or another shape.</summary>
    public static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    /// <summary>A time member (ISO 8601), or null.</summary>
    public static DateTimeOffset? Time(JsonElement element, string name) =>
        String(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : null;
}
