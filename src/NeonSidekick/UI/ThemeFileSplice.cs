using System.Text;
using System.Text.Json;

namespace NeonSidekick.UI;

/// <summary>
/// A theme file changed in its own bytes (2026-11-17, the admin-themes-render review's #25, #26, #156, #163, #98): <c>/theme edit</c>'s save and
/// a built-in's copy replaced the whole file with a <c>JsonNode</c> rewrite, which dropped its comments, re-indented it, ended its lines in
/// <c>\n</c> and wrote every role under the canonical key beside a <c>"Primary"</c> the user had typed. Here a <see cref="Utf8JsonReader"/>
/// (comments skipped, trailing commas allowed, as <see cref="ThemeJsonContext"/> reads) finds each value's bytes, and only those are replaced;
/// a role the <c>colors</c> object lacks goes in after its last entry, on the file's own line ending and indent. A byte-order mark is kept.
/// Each returns null when the reader refuses the file or the part to change isn't there, and the caller falls back. Pure.
/// </summary>
public static class ThemeFileSplice
{
    private static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static ReadOnlySpan<byte> Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// <paramref name="utf8"/> with its top-level <c>colors</c> object's <paramref name="changes"/> (role key, <c>#RRGGBB</c>) set: each onto the
    /// key the file already has for that role, matched ignoring case (the last of two, as the loader keeps), in the file's spelling; a role it
    /// lacks added. Null for a file the reader refuses or one with no <c>colors</c> object.
    /// </summary>
    public static byte[]? SetColors(byte[] utf8, IReadOnlyList<(string Key, string Hex)> changes)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(changes);
        int skip = utf8.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        var body = utf8.AsSpan(skip);
        var values = new Dictionary<string, (int Start, int End)>(StringComparer.OrdinalIgnoreCase);
        int open = -1, close = -1, lastEnd = -1, lastKey = -1;
        try
        {
            var reader = new Utf8JsonReader(body, Options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isColors = reader.ValueTextEquals("colors"u8);
                reader.Read();
                if (!isColors || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                // The last "colors" wins, as the loader keeps it.
                values.Clear();
                open = (int)reader.TokenStartIndex;
                lastEnd = -1;
                lastKey = -1;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    int keyStart = (int)reader.TokenStartIndex;
                    string key = reader.GetString()!;
                    reader.Read();
                    int start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    int end = (int)reader.BytesConsumed;
                    if (ThemeKeys.TryParseColor(key, out _))
                    {
                        values[key] = (start, end);
                    }

                    lastKey = keyStart;
                    lastEnd = end;
                }

                close = (int)reader.TokenStartIndex;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (open < 0)
        {
            return null;
        }

        string newline = body.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        var edits = new List<(int Start, int End, string Text)>();
        var missing = new List<(string Key, string Hex)>();
        foreach (var (key, hex) in changes)
        {
            if (values.Keys.LastOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } spelled)
            {
                var (start, end) = values[spelled];
                edits.Add((start, end, "\"" + hex + "\""));
            }
            else
            {
                missing.Add((key, hex));
            }
        }

        if (missing.Count > 0)
        {
            string indent = lastKey >= 0 ? IndentAt(body, lastKey) : IndentAt(body, open) + "  ";
            var added = new StringBuilder();
            foreach (var (key, hex) in missing)
            {
                added.Append(added.Length > 0 || lastEnd >= 0 ? "," : "").Append(newline).Append(indent).Append('"').Append(key).Append("\": \"").Append(hex).Append('"');
            }

            if (lastEnd >= 0)
            {
                edits.Add((lastEnd, lastEnd, added.ToString()));
            }
            else
            {
                // An empty object: the entries go after its brace, the closing one on a line of its own.
                edits.Add((open + 1, close, added.Append(newline).Append(IndentAt(body, open)).ToString()));
            }
        }

        return Apply(utf8, skip, edits);
    }

    /// <summary><paramref name="utf8"/> with its top-level <c>name</c>'s value set to <paramref name="name"/> (a theme name: no character needs escaping); null when it has none or the reader refuses it.</summary>
    public static byte[]? SetName(byte[] utf8, string name)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(name);
        int skip = utf8.AsSpan().StartsWith(Bom) ? Bom.Length : 0;
        (int Start, int End)? found = null;
        try
        {
            var reader = new Utf8JsonReader(utf8.AsSpan(skip), Options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isName = reader.ValueTextEquals("name"u8);
                reader.Read();
                int start = (int)reader.TokenStartIndex;
                reader.Skip();
                if (isName)
                {
                    found = (start, (int)reader.BytesConsumed);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return found is { } at ? Apply(utf8, skip, [(at.Start, at.End, "\"" + name + "\"")]) : null;
    }

    /// <summary>The white space that starts the line holding <paramref name="at"/>.</summary>
    private static string IndentAt(ReadOnlySpan<byte> body, int at)
    {
        int lineStart = body[..at].LastIndexOf((byte)'\n') + 1;
        int end = lineStart;
        while (end < at && body[end] is (byte)' ' or (byte)'\t')
        {
            end++;
        }

        return Encoding.UTF8.GetString(body[lineStart..end]);
    }

    /// <summary>The edits (offsets past the mark) laid into a copy of <paramref name="utf8"/>.</summary>
    private static byte[] Apply(byte[] utf8, int skip, List<(int Start, int End, string Text)> edits)
    {
        using var output = new MemoryStream(utf8.Length + 64);
        int at = 0;
        foreach (var (start, end, text) in edits.OrderBy(e => e.Start))
        {
            output.Write(utf8, at, skip + start - at);
            output.Write(Encoding.UTF8.GetBytes(text));
            at = skip + end;
        }

        output.Write(utf8, at, utf8.Length - at);
        return output.ToArray();
    }
}
