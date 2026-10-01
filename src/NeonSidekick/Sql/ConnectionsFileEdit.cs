using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>
/// The byte-level edits of a <c>{ "connections": { "&lt;name&gt;": { … } } }</c> file that keep its comments and layout
/// (2026-09-30, out of <see cref="SqlConfigFile"/> when <c>oracle.json</c> (<see cref="Oracle.OracleConfigFile"/>) took the
/// same shape): a connection's <c>password</c> written over or inserted, and a whole connection added. Each finds its
/// spot with a comment-tolerant <see cref="Utf8JsonReader"/>, splices the bytes, writes a temp file and moves it over the
/// original. The engine-specific parts — the file's <c>EmptyText</c>, the entry's serialisation — stay with the caller.
/// The entries sit under <c>connections</c> unless a caller names another key: <c>unc.json</c>'s are <c>shares</c>
/// (2026-09-30, the UNC tools), its refusals saying "share" for "connection".
/// </summary>
public static class ConnectionsFileEdit
{
    /// <summary>The key the entries sit under by default: <c>sql.json</c>'s, <c>oracle.json</c>'s and <c>mysql.json</c>'s.</summary>
    public const string DefaultSection = "connections";

    /// <summary>What an entry is called in a refusal by default.</summary>
    public const string DefaultNoun = "connection";

    /// <summary>
    /// Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>,
    /// touching nothing else: the string token replaced, or — for a connection with no <c>password</c> yet — the key inserted
    /// after its <c>user</c> value (after the object's brace without one). Null on success, else why not.
    /// </summary>
    public static string? WritePassword(string path, string name, string value, string section = DefaultSection, string noun = DefaultNoun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            int bom = Bom(bytes);
            if (Locate(bytes.AsSpan(bom), name, section) is not { } spot)
            {
                return SqlText.ConnectionNotInFile(name, noun);
            }

            string quoted = "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
            Splice(path, bytes, bom + spot.Start, bom + spot.End, spot.Replace ? quoted : spot.Prefix + "\"password\": " + quoted + spot.Suffix);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LogText.Excerpt(ex.Message);
        }
    }

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/>: <paramref name="entryJson"/> (the entry's object,
    /// indented, its password left out by the caller) inserted after the last entry of <c>connections</c> (a comma before
    /// it), inside an empty one, or a whole <c>connections</c> object after the root's brace when the file has none. A
    /// missing file is made with <paramref name="emptyText"/> first. A name already in the file (trimmed, case-insensitive)
    /// is refused. Null on success, else why not.
    /// </summary>
    public static string? AddConnection(string path, string name, string entryJson, string emptyText, string section = DefaultSection, string noun = DefaultNoun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(entryJson);
        ArgumentNullException.ThrowIfNull(emptyText);
        name = name.Trim();
        try
        {
            EnsureExists(path, emptyText);
            byte[] bytes = File.ReadAllBytes(path);
            int bom = Bom(bytes);
            var json = bytes.AsSpan(bom);
            var spot = LocateInsert(json, name, section, noun);
            if (spot.Error is { } refused)
            {
                return refused;
            }

            string newline = json.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
            string entry = "\"" + JsonEncodedText.Encode(name, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\": "
                + string.Join(newline + "    ", entryJson.ReplaceLineEndings("\n").Split('\n'));
            string insert = spot.Kind switch
            {
                InsertKind.AfterLast => "," + newline + "    " + entry,
                InsertKind.IntoEmpty => newline + "    " + entry + newline + "  ",
                InsertKind.IntoEmptyWithComments => newline + "    " + entry,
                _ => newline + "  \"" + section + "\": {" + newline + "    " + entry + newline + "  }" + (spot.RootHasKeys ? "," : ""),
            };

            Splice(path, bytes, bom + spot.Start, bom + spot.End, insert);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LogText.Excerpt(ex.Message);
        }
    }

    /// <summary>Writes <paramref name="emptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path, string emptyText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(emptyText);
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, emptyText);
        return true;
    }

    private static int Bom(byte[] bytes) => bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;

    /// <summary><paramref name="bytes"/> with <c>[start, end)</c> replaced by <paramref name="insert"/>, written to a temp file moved over <paramref name="path"/>.</summary>
    private static void Splice(string path, byte[] bytes, int start, int end, string insert)
    {
        byte[] inserted = Encoding.UTF8.GetBytes(insert);
        var rewritten = new byte[bytes.Length - (end - start) + inserted.Length];
        bytes.AsSpan(0, start).CopyTo(rewritten);
        inserted.CopyTo(rewritten.AsSpan(start));
        bytes.AsSpan(end).CopyTo(rewritten.AsSpan(start + inserted.Length));

        string temp = path + ".tmp";
        File.WriteAllBytes(temp, rewritten);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Where <see cref="AddConnection"/>'s bytes go in the file.</summary>
    private enum InsertKind
    {
        /// <summary>After the last entry of <c>connections</c>, a comma first.</summary>
        AfterLast,

        /// <summary>Over the whitespace inside an empty <c>connections</c> object (<c>{}</c>, <c>{ }</c>).</summary>
        IntoEmpty,

        /// <summary>After the brace of an empty <c>connections</c> that holds comments, which stay after it.</summary>
        IntoEmptyWithComments,

        /// <summary>A whole <c>connections</c> object after the root's brace: the file has none.</summary>
        NewConnections,
    }

    /// <summary>What <see cref="LocateInsert"/> found: the byte range to replace (empty for a plain insert), how, whether the root has other keys, or why not.</summary>
    private readonly record struct InsertSpot(int Start, int End, InsertKind Kind, bool RootHasKeys, string? Error);

    /// <summary>
    /// Where a new <c>connections.&lt;name&gt;</c> goes in <paramref name="json"/>: the refusal when the root is not an object,
    /// <c>connections</c> is not one, or <paramref name="name"/> is already an entry (trimmed, case-insensitive — as a
    /// catalog's <c>Find</c> would match it).
    /// </summary>
    private static InsertSpot LocateInsert(ReadOnlySpan<byte> json, string name, string section, string noun)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new InsertSpot(0, 0, default, false, SqlText.FileNotAnObject);
        }

        int rootBrace = (int)reader.TokenStartIndex;
        bool rootHasKeys = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            rootHasKeys = true;
            bool isConnections = reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase);
            reader.Read();
            if (!isConnections)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return new InsertSpot(0, 0, default, true, section == DefaultSection ? SqlText.ConnectionsNotAnObject : SqlText.SectionNotAnObject(section));
            }

            int open = (int)reader.TokenStartIndex;
            int lastEnd = -1;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (string.Equals(reader.GetString()!.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return new InsertSpot(0, 0, default, true, SqlText.ConnectionAlreadyInFile(name, noun));
                }

                reader.Read();
                reader.Skip();
                lastEnd = (int)reader.BytesConsumed;
            }

            int close = (int)reader.TokenStartIndex;
            if (lastEnd >= 0)
            {
                return new InsertSpot(lastEnd, lastEnd, InsertKind.AfterLast, true, null);
            }

            bool blank = true;
            foreach (byte b in json[(open + 1)..close])
            {
                blank &= b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
            }

            return blank
                ? new InsertSpot(open + 1, close, InsertKind.IntoEmpty, true, null)
                : new InsertSpot(open + 1, open + 1, InsertKind.IntoEmptyWithComments, true, null);
        }

        return new InsertSpot(rootBrace + 1, rootBrace + 1, InsertKind.NewConnections, rootHasKeys, null);
    }

    /// <summary>Where <see cref="WritePassword"/> writes: the byte range to replace (empty for an insert) and, for an insert, what goes either side of the new key.</summary>
    private readonly record struct PasswordSpot(int Start, int End, bool Replace, string Prefix, string Suffix);

    /// <summary>
    /// The <c>password</c> string token of <c>connections.&lt;name&gt;</c> in <paramref name="json"/>, or where one goes.
    /// Keys match as the deserializer matches them (case-insensitive); the name matches trimmed, ordinally.
    /// </summary>
    private static PasswordSpot? Locate(ReadOnlySpan<byte> json, string name, string section)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        bool inConnections = false;
        bool inTarget = false;
        int objectStart = -1;
        int userEnd = -1;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    inConnections = reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase);
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 2 && inConnections:
                    inTarget = string.Equals(reader.GetString()!.Trim(), name, StringComparison.Ordinal);
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 2 && inTarget:
                    objectStart = (int)reader.TokenStartIndex;
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 3 && inTarget:
                    string key = reader.GetString()!;
                    reader.Read();
                    if (key.Equals("password", StringComparison.OrdinalIgnoreCase) && reader.TokenType is JsonTokenType.String or JsonTokenType.Null)
                    {
                        int start = (int)reader.TokenStartIndex;
                        int length = reader.TokenType == JsonTokenType.Null ? 4 : reader.ValueSpan.Length + 2;
                        return new PasswordSpot(start, start + length, true, "", "");
                    }

                    if (key.Equals("user", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
                    {
                        userEnd = (int)reader.TokenStartIndex + reader.ValueSpan.Length + 2;
                    }

                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }

                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 2 && inTarget:
                    return userEnd >= 0 ? new PasswordSpot(userEnd, userEnd, false, ", ", "")
                        : objectStart >= 0 ? new PasswordSpot(objectStart + 1, objectStart + 1, false, " ", ",") : null;
            }
        }

        return null;
    }
}
