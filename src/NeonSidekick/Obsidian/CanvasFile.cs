using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NeonSidekick.Obsidian;

/// <summary>One node of a canvas that may point at a note: a file card's vault path, or a text card's Markdown.</summary>
public readonly record struct CanvasNode(string Type, string? File, string? Text);

/// <summary>
/// An Obsidian <c>.canvas</c> file (JSON Canvas: <c>{"nodes": [...], "edges": [...]}</c>) as <c>vault_move</c>, <c>vault_delete</c> and
/// <c>vault_links</c> need it (2026-10-22, review O4: a canvas's file cards and the wikilinks in its text cards were never renamed or
/// counted, where Obsidian does both). Read with <see cref="JsonDocument"/>, never a reflection serializer; written back through
/// <see cref="Utf8JsonWriter"/> tab-indented as Obsidian writes it, every field it doesn't change kept as it was (numbers as their raw
/// text, unknown keys and their order). Pure.
/// </summary>
public static class CanvasFile
{
    public const string Extension = ".canvas";

    /// <summary>The most canvases one call reads (a vault's usual few, with room).</summary>
    public const int MaxCanvases = 2_000;

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static bool IsCanvas(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file and text cards of <paramref name="json"/>; empty for text that is no canvas.</summary>
    public static IReadOnlyList<CanvasNode> Nodes(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var doc = JsonDocument.Parse(json, ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var found = new List<CanvasNode>();
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind == JsonValueKind.Object && Str(node, "type") is { } type)
                {
                    found.Add(new CanvasNode(type, Str(node, "file"), Str(node, "text")));
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return [];
        }

        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// <paramref name="json"/> with each file card's <c>file</c> passed through <paramref name="file"/> and each text card's <c>text</c> through
    /// <paramref name="text"/> (null from either keeps the value); null when nothing changed or the text is no canvas.
    /// </summary>
    public static string? Rewrite(string json, Func<string, string?> file, Func<string, string?> text)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var doc = JsonDocument.Parse(json, ReadOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool changed = false;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                Write(writer, doc.RootElement, null);
            }

            return changed ? Encoding.UTF8.GetString(stream.ToArray()) : null;

            void Write(Utf8JsonWriter writer, JsonElement element, string? nodeType)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        string? type = element.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                        writer.WriteStartObject();
                        foreach (var property in element.EnumerateObject())
                        {
                            writer.WritePropertyName(property.Name);
                            if (property.Value.ValueKind == JsonValueKind.String && Mapped(type, property.Name, property.Value.GetString()!) is { } replaced)
                            {
                                writer.WriteStringValue(replaced);
                                continue;
                            }

                            Write(writer, property.Value, type);
                        }

                        writer.WriteEndObject();
                        break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray();
                        foreach (var item in element.EnumerateArray())
                        {
                            Write(writer, item, nodeType);
                        }

                        writer.WriteEndArray();
                        break;
                    default:
                        writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                        break;
                }
            }

            string? Mapped(string? type, string name, string value)
            {
                string? next = (type, name) switch
                {
                    ("file", "file") => file(value),
                    ("text", "text") => text(value),
                    _ => null,
                };

                if (next is null || next == value)
                {
                    return null;
                }

                changed = true;
                return next;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
