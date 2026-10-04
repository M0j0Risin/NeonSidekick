using System.Text;
using System.Text.Json;

namespace NeonSidekick.Settings;

/// <summary>
/// What changed between two settings snapshots, one line per property, for the diagnostic log:
/// <c>TtsSpeed: 1 → 1.2</c>. The two are serialised through <see cref="SettingsJsonContext"/>
/// (the same shape the file has, so the property names are the JSON keys) and compared property
/// by property; a value that did not change gives no line. The secrets,
/// <see cref="AppSettingsData.LlmApiKey"/> and <see cref="AppSettingsData.AnthropicApiKey"/> (2026-09-27), are named but never shown (<see cref="Redacted"/>).
/// </summary>
public static class SettingsDiff
{
    /// <summary>What stands in for a secret's value on both sides of the arrow.</summary>
    public const string Redacted = "(redacted)";

    /// <summary>The arrow between the old and the new value.</summary>
    public const string Arrow = " → ";

    /// <summary>The properties whose values are never written to a log.</summary>
    public static readonly IReadOnlySet<string> Secrets = new HashSet<string>(StringComparer.Ordinal) { nameof(AppSettingsData.LlmApiKey), nameof(AppSettingsData.AnthropicApiKey), nameof(AppSettingsData.OpenAIApiKey), nameof(AppSettingsData.HomeAssistantToken) };

    /// <summary>
    /// One <c>Name: old → new</c> line per property whose value differs, in the file's order;
    /// empty when nothing differs.
    /// </summary>
    public static IReadOnlyList<string> Changes(AppSettingsData before, AppSettingsData after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        using var old = JsonDocument.Parse(JsonSerializer.Serialize(before, SettingsJsonContext.Default.AppSettingsData));
        using var now = JsonDocument.Parse(JsonSerializer.Serialize(after, SettingsJsonContext.Default.AppSettingsData));
        var lines = new List<string>();
        foreach (var property in now.RootElement.EnumerateObject())
        {
            if (!old.RootElement.TryGetProperty(property.Name, out var previous) || string.Equals(previous.GetRawText(), property.Value.GetRawText(), StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(Secrets.Contains(property.Name)
                ? property.Name + ": " + Redacted
                : property.Name + ": " + Render(previous) + Arrow + Render(property.Value));
        }

        return lines;
    }

    /// <summary>
    /// The properties of <paramref name="settings"/> whose values are not the compiled defaults,
    /// as <c>Name=value</c> (a secret as <c>Name=(redacted)</c>), in the file's order; empty for
    /// a default profile.
    /// </summary>
    public static IReadOnlyList<string> NotDefault(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var defaults = JsonDocument.Parse(JsonSerializer.Serialize(new AppSettingsData(), SettingsJsonContext.Default.AppSettingsData));
        using var now = JsonDocument.Parse(JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettingsData));
        var lines = new List<string>();
        foreach (var property in now.RootElement.EnumerateObject())
        {
            if (defaults.RootElement.TryGetProperty(property.Name, out var standard) && string.Equals(standard.GetRawText(), property.Value.GetRawText(), StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(property.Name + "=" + (Secrets.Contains(property.Name) ? Redacted : Render(property.Value)));
        }

        return lines;
    }

    /// <summary>A string as itself, an array as <c>[a, b]</c>, anything else as the JSON text.</summary>
    public static string Render(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? "";
            case JsonValueKind.Array:
                var sb = new StringBuilder("[");
                bool first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    first = false;
                    sb.Append(Render(item));
                }

                return sb.Append(']').ToString();
            default:
                return value.GetRawText();
        }
    }
}
