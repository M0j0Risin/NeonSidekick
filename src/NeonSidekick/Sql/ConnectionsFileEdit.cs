using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>
/// The byte-level edits of a <c>{ "connections": { "&lt;name&gt;": { … } } }</c> file that keep its comments and layout
/// (2026-09-30, out of <see cref="SqlConfigFile"/> when <c>oracle.json</c> (<see cref="Oracle.OracleConfigFile"/>) took the
/// same shape): a connection's <c>password</c> written over or inserted, a whole connection added, and (2026-10-05, the wizards' edit) one replaced. Each finds its
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
    public static string? WritePassword(string path, string name, string value, string section = DefaultSection, string noun = DefaultNoun) =>
        WritePassword(path, name, value, expected: null, section, noun);

    /// <summary>
    /// <see cref="WritePassword(string, string, string, string, string)"/>, and with <paramref name="expected"/> only over that very value
    /// (2026-10-24, the database review's A16: the encrypt-in-place read the plain password outside the lock, then wrote its encryption over
    /// whatever the file held by then — a password the user had just set in between was replaced by the stale one). A token that no longer
    /// holds <paramref name="expected"/> is left alone and <see cref="PasswordChanged"/> answered: the next read sees the new one.
    /// </summary>
    public static string? WritePassword(string path, string name, string value, string? expected, string section = DefaultSection, string noun = DefaultNoun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        lock (LockFor(path))
        {
            try
            {
                // An encryption in place (expected given) waits for no other process (2026-10-24, the review of the fixes: it runs inside a
                // catalog load, the screen's too, and a held lock stalled it 5 s); the password stays plain and the next read tries again.
                using var _ = CrossProcess(path, expected is null ? null : TimeSpan.Zero);
                return WritePasswordLocked(path, name, value, expected, section, noun);
            }
            catch (IOException ex)
            {
                return LogText.Excerpt(ex.Message);
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }
    }

    /// <summary><see cref="WritePassword(string, string, string, string?, string, string)"/>'s answer when the token no longer held the value expected.</summary>
    public const string PasswordChanged = "the password changed in the file meanwhile";

    /// <summary><see cref="WriteValue"/>'s answer when a key along the path is given twice (the K8/C7 guard: the reader keeps the last, a splice would write the first).</summary>
    public const string KeyRepeated = "a key along the path is given twice";

    /// <summary><see cref="WriteValue"/>'s answer when the path doesn't end at a string value.</summary>
    public const string ValueNotInFile = "no such value in the file";

    /// <summary>
    /// Writes <paramref name="value"/> over the string at <paramref name="keyPath"/> (2026-11-19, feature idea #29: <c>mcp.json</c>'s
    /// <c>mcpServers.&lt;name&gt;.headers|env.&lt;key&gt;</c>), touching nothing else, and only while it still holds <paramref name="expected"/>
    /// (else <see cref="PasswordChanged"/>, silent). Every step matches ordinally and untrimmed, as the MCP reader binds them, never as
    /// <see cref="Locate"/> matches the families' case-blind, trimmed names. A key given twice anywhere along the path is
    /// <see cref="KeyRepeated"/>. It never inserts, and waits for no other process (it runs inside a load). Null on success, else why not.
    /// </summary>
    public static string? WriteValue(string path, IReadOnlyList<string> keyPath, string value, string expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(keyPath);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(expected);
        if (keyPath.Count == 0)
        {
            return ValueNotInFile;
        }

        lock (LockFor(path))
        {
            try
            {
                using var _ = CrossProcess(path, TimeSpan.Zero);
                byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
                int bom = Bom(bytes);
                var found = LocateValue(bytes.AsSpan(bom), keyPath);
                if (found.Error is { } error)
                {
                    return error;
                }

                if (!string.Equals(found.Current, expected, StringComparison.Ordinal))
                {
                    return PasswordChanged;
                }

                string quoted = "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
                Splice(path, bytes, bom + found.Start, bom + found.End, quoted, wide);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return LogText.Excerpt(ex.Message);
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }
    }

    /// <summary>One value for <see cref="WriteValues"/>: written over the string at <see cref="KeyPath"/> while it still holds <see cref="Expected"/>.</summary>
    public readonly record struct ValueWrite(IReadOnlyList<string> KeyPath, string Value, string Expected);

    /// <summary>
    /// <see cref="WriteValue"/> for several values at once (2026-11-20, the mcp-ask review's #42, #94): one read, every splice applied from
    /// the end, one atomic write, under the same two locks and with the same checks, so sealing a server's tokens is one write, not one per
    /// value. An item whose value changed meanwhile, whose path is gone or repeats a key is left out with its answer, and the rest are still
    /// written. Returns one <c>(index, error)</c> per item left out, empty when all were written; a failure of the whole file is every
    /// item's. Waits for no other process (it runs inside a load).
    /// </summary>
    public static IReadOnlyList<(int Index, string Error)> WriteValues(string path, IReadOnlyList<ValueWrite> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        var errors = new List<(int Index, string Error)>();
        lock (LockFor(path))
        {
            try
            {
                using var _ = CrossProcess(path, TimeSpan.Zero);
                byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
                int bom = Bom(bytes);
                var splices = new List<(int Start, int End, string Insert)>();
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.KeyPath.Count == 0)
                    {
                        errors.Add((i, ValueNotInFile));
                        continue;
                    }

                    var found = LocateValue(bytes.AsSpan(bom), item.KeyPath);
                    if (found.Error is { } error)
                    {
                        errors.Add((i, error));
                        continue;
                    }

                    if (!string.Equals(found.Current, item.Expected, StringComparison.Ordinal))
                    {
                        errors.Add((i, PasswordChanged));
                        continue;
                    }

                    splices.Add((bom + found.Start, bom + found.End, "\"" + JsonEncodedText.Encode(item.Value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\""));
                }

                if (splices.Count > 0)
                {
                    SpliceAll(path, bytes, splices, wide);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                string detail = LogText.Excerpt(ex.Message);
                errors.Clear();
                for (int i = 0; i < items.Count; i++)
                {
                    errors.Add((i, detail));
                }
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }

        return errors;
    }

    /// <summary><see cref="SetEntryString"/>'s answer when the file holds no object entry of that name under the section.</summary>
    public const string EntryNotInFile = "no such entry in the file";

    /// <summary>
    /// Sets <c>&lt;section&gt;.&lt;name&gt;.&lt;key&gt;</c> to the string <paramref name="value"/> (2026-11-20, plan idempotent-mixing-ripple: an MCP
    /// server's <c>ask</c> from <c>/mcp</c> › Servers), touching nothing else. A key already there is replaced whatever its token (a bad
    /// <c>"ask": 5</c> too); a missing one is inserted after the entry's last value, on its own line with that property's indentation and the
    /// file's own line break when the entry spans lines (the shape of a hand edit), else inline; a trailing comma is kept and not doubled.
    /// Every step matches ordinally and untrimmed, as the MCP reader binds them, never as <see cref="RepeatedKeys"/> and
    /// <see cref="RepeatedNames"/> fold case: <c>MCP_DOCKER</c> and <c>mcp_docker</c> are two servers to MCP, and a stray <c>"Ask"</c> is never
    /// read. A step given twice is <see cref="KeyRepeated"/>, a missing entry <see cref="EntryNotInFile"/>. A user's act, so it waits the usual
    /// <see cref="CrossProcessWait"/> for another process's edit. Null on success, else why not.
    /// </summary>
    public static string? SetEntryString(string path, string section, string name, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (LockFor(path))
        {
            try
            {
                using var _ = CrossProcess(path);
                byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
                int bom = Bom(bytes);
                var spot = LocateEntryKey(bytes.AsSpan(bom), section, name, key);
                if (spot.Error is { } error)
                {
                    return error;
                }

                string quoted = Quoted(value);
                Splice(path, bytes, bom + spot.Start, bom + spot.End, spot.Replace ? quoted : spot.Prefix + Quoted(key) + ": " + quoted + spot.Suffix, wide);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return LogText.Excerpt(ex.Message);
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }

        static string Quoted(string text) => "\"" + JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
    }

    /// <summary>Where <see cref="SetEntryString"/> writes: the token to replace, or the empty range to insert at with what goes either side; or why not.</summary>
    private readonly record struct EntrySpotKey(int Start, int End, bool Replace, string Prefix, string Suffix, string? Error);

    private static EntrySpotKey LocateEntryKey(ReadOnlySpan<byte> json, string section, string name, string key)
    {
        var missing = new EntrySpotKey(0, 0, false, "", "", EntryNotInFile);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return missing;
        }

        EntrySpotKey? found = null;
        bool sectionSeen = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isSection = string.Equals(reader.GetString(), section, StringComparison.Ordinal);
            reader.Read();
            if (!isSection || reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                if (isSection && sectionSeen)
                {
                    return missing with { Error = KeyRepeated };
                }

                sectionSeen |= isSection;
                continue;
            }

            if (sectionSeen)
            {
                return missing with { Error = KeyRepeated };
            }

            sectionSeen = true;
            bool entrySeen = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isEntry = string.Equals(reader.GetString(), name, StringComparison.Ordinal);
                reader.Read();
                if (!isEntry)
                {
                    reader.Skip();
                    continue;
                }

                if (entrySeen)
                {
                    return missing with { Error = KeyRepeated };
                }

                entrySeen = true;
                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                var inEntry = ScanEntry(ref reader, json, key);
                if (inEntry.Error is not null)
                {
                    return inEntry;
                }

                found = inEntry;
            }
        }

        return found ?? missing;
    }

    // One entry's object, from its opening brace to its closing one: the key's whole token when it is there (given twice: a repeat), else
    // where it goes after the last value, laid out as the entry is.
    private static EntrySpotKey ScanEntry(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, string key)
    {
        int open = (int)reader.TokenStartIndex;
        int lastName = -1;
        int lastEnd = -1;
        EntrySpotKey? replace = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            int nameStart = (int)reader.TokenStartIndex;
            bool isKey = string.Equals(reader.GetString(), key, StringComparison.Ordinal);
            reader.Read();
            int valueStart = (int)reader.TokenStartIndex;
            reader.Skip();
            int valueEnd = (int)reader.BytesConsumed;
            if (isKey)
            {
                if (replace is not null)
                {
                    return new EntrySpotKey(0, 0, false, "", "", KeyRepeated);
                }

                replace = new EntrySpotKey(valueStart, valueEnd, true, "", "", null);
            }

            lastName = nameStart;
            lastEnd = valueEnd;
        }

        if (replace is { } spot)
        {
            return spot;
        }

        if (lastName < 0)
        {
            // An empty object: inside its braces, a space either side when they touch.
            return new EntrySpotKey(open + 1, open + 1, false, " ", json[open + 1] == (byte)'}' ? " " : "", null);
        }

        int at = lastEnd;
        int next = at;
        while (next < json.Length && json[next] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            next++;
        }

        bool trailingComma = next < json.Length && json[next] == (byte)',';
        if (trailingComma)
        {
            at = next + 1;   // after the comma the reader allowed; no second one
        }

        // The last property's own line: only whitespace before its name means the entry is laid out a property a line.
        int lineStart = lastName;
        while (lineStart > 0 && json[lineStart - 1] is not ((byte)'\n' or (byte)'\r'))
        {
            lineStart--;
        }

        bool ownLine = lineStart > open;
        for (int i = lineStart; i < lastName && ownLine; i++)
        {
            ownLine = json[i] is (byte)' ' or (byte)'\t';
        }

        if (!ownLine)
        {
            return new EntrySpotKey(at, at, false, trailingComma ? " " : ", ", "", null);
        }

        string indent = Encoding.UTF8.GetString(json[lineStart..lastName]);
        string lineBreak = lineStart >= 2 && json[lineStart - 1] == (byte)'\n' && json[lineStart - 2] == (byte)'\r' ? "\r\n" : "\n";
        return new EntrySpotKey(at, at, false, (trailingComma ? "" : ",") + lineBreak + indent, "", null);
    }

    /// <summary>Where <see cref="WriteValue"/> writes: the string token's byte range and its value, or why not.</summary>
    private readonly record struct ValueSpot(int Start, int End, string? Current, string? Error);

    private static ValueSpot LocateValue(ReadOnlySpan<byte> json, IReadOnlyList<string> keyPath)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new ValueSpot(0, 0, null, ValueNotInFile);
        }

        var spot = ScanObject(ref reader, keyPath, 0);
        return spot.Error is null && spot.End == 0 ? new ValueSpot(0, 0, null, ValueNotInFile) : spot;
    }

    // One object at path level `level`, read to its end brace: a key matching that step (ordinal) twice is a repeat; a match at the last
    // step must be a string; a match before it must be an object, read the same way. End == 0 with no error means not found here.
    private static ValueSpot ScanObject(ref Utf8JsonReader reader, IReadOnlyList<string> keyPath, int level)
    {
        var found = new ValueSpot(0, 0, null, null);
        bool matched = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool match = string.Equals(reader.GetString(), keyPath[level], StringComparison.Ordinal);
            reader.Read();
            if (!match)
            {
                reader.Skip();
                continue;
            }

            if (matched)
            {
                return new ValueSpot(0, 0, null, KeyRepeated);
            }

            matched = true;
            if (level == keyPath.Count - 1)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    int start = (int)reader.TokenStartIndex;
                    found = new ValueSpot(start, start + reader.ValueSpan.Length + 2, reader.GetString(), null);
                }
                else
                {
                    reader.Skip();
                }

                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var inner = ScanObject(ref reader, keyPath, level + 1);
            if (inner.Error is not null)
            {
                return inner;
            }

            found = inner;
        }

        return found;
    }

    private static string? WritePasswordLocked(string path, string name, string value, string? expected, string section, string noun)
    {
        try
        {
            byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
            int bom = Bom(bytes);

            // A key given twice in the entry is refused (2026-10-23, the admin review's C7): the reader keeps the last "password", the splice
            // below would write into the first, and the plain one stayed on disk for ever.
            if (RepeatedKeys(bytes.AsSpan(bom), section).TryGetValue(name.Trim(), out string? twice))
            {
                return Computers.ComputerText.KeyTwice(twice);
            }

            if (Locate(bytes.AsSpan(bom), name, section) is not { } spot)
            {
                return SqlText.ConnectionNotInFile(name, noun);
            }

            if (expected is not null && !string.Equals(spot.Current, expected, StringComparison.Ordinal))
            {
                return PasswordChanged;
            }

            string quoted = "\"" + JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";
            Splice(path, bytes, bom + spot.Start, bom + spot.End, spot.Replace ? quoted : spot.Prefix + "\"password\": " + quoted + spot.Suffix, wide);
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
        lock (LockFor(path))
        {
            try
            {
                using var _ = CrossProcess(path);
                return AddConnectionLocked(path, name.Trim(), entryJson, emptyText, section, noun);
            }
            catch (IOException ex)
            {
                return LogText.Excerpt(ex.Message);
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }
    }

    private static string? AddConnectionLocked(string path, string name, string entryJson, string emptyText, string section, string noun)
    {
        try
        {
            EnsureExistsLocked(path, emptyText);
            byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
            int bom = Bom(bytes);
            var json = bytes.AsSpan(bom);
            if (RepeatedSection(json, section) is { } twice)
            {
                return SqlText.SectionTwice(twice);
            }

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

            Splice(path, bytes, bom + spot.Start, bom + spot.End, insert, wide);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LogText.Excerpt(ex.Message);
        }
    }

    /// <summary>
    /// Replaces connection <paramref name="oldName"/> in <paramref name="path"/> with <paramref name="newName"/> and
    /// <paramref name="entryJson"/> (2026-10-05, the user's ask: the add-connection wizards edit too): the bytes from the entry's
    /// name to the end of its object spliced, the entry's own indent kept, every comment and other entry untouched. The password
    /// is the caller's to write after (<c>WritePassword</c>), as for an add. Refused when <paramref name="oldName"/> is not an
    /// entry, or <paramref name="newName"/> is another one (trimmed, case-insensitive). Null on success, else why not.
    /// </summary>
    public static string? ReplaceConnection(string path, string oldName, string newName, string entryJson, string section = DefaultSection, string noun = DefaultNoun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(entryJson);
        lock (LockFor(path))
        {
            try
            {
                using var _ = CrossProcess(path);
                return ReplaceConnectionLocked(path, oldName.Trim(), newName.Trim(), entryJson, section, noun);
            }
            catch (IOException ex)
            {
                return LogText.Excerpt(ex.Message);
            }
            finally
            {
                Computers.ConfigCache.Invalidate(path);
            }
        }
    }

    private static string? ReplaceConnectionLocked(string path, string oldName, string newName, string entryJson, string section, string noun)
    {
        try
        {
            if (!File.Exists(path))
            {
                return SqlText.ConnectionNotInFile(oldName, noun);
            }

            byte[] bytes = ForEdit(File.ReadAllBytes(path), out var wide);
            int bom = Bom(bytes);
            var json = bytes.AsSpan(bom);
            if (RepeatedSection(json, section) is { } twice)
            {
                return SqlText.SectionTwice(twice);
            }

            var spot = LocateEntry(json, oldName, newName, section, noun);
            if (spot.Error is { } refused)
            {
                return refused;
            }

            int lineStart = json[..spot.Start].LastIndexOfAny((byte)'\n', (byte)'\r') + 1;
            int indent = 0;
            while (lineStart + indent < spot.Start && json[lineStart + indent] is (byte)' ' or (byte)'\t')
            {
                indent++;
            }

            string newline = json.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
            string pad = Encoding.UTF8.GetString(json.Slice(lineStart, indent));
            string entry = "\"" + JsonEncodedText.Encode(newName, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\": "
                + string.Join(newline + pad, entryJson.ReplaceLineEndings("\n").Split('\n'));
            Splice(path, bytes, bom + spot.Start, bom + spot.End, entry, wide);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LogText.Excerpt(ex.Message);
        }
    }

    /// <summary>
    /// The entry names under <paramref name="section"/> that appear more than once, ignoring case and spaces around them (2026-10-22, review
    /// K8: the reader kept the last of two, while the password splice wrote into the first, so a plain password stayed and every load wrote
    /// the file again; review K2: "eng" and "Eng" loaded as two shares that one name could not tell apart). Empty for JSON it can't read.
    /// </summary>
    public static IReadOnlySet<string> RepeatedNames(ReadOnlySpan<byte> json, string section)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return repeated;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isSection = reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase);
                reader.Read();
                if (!isSection || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!.Trim();
                    if (!seen.Add(name))
                    {
                        repeated.Add(name);
                    }

                    reader.Read();
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // The deserializer says what is wrong with the file.
        }

        return repeated;
    }

    /// <summary>
    /// The root key spelled <paramref name="section"/> when the root holds it more than once in any case (2026-10-24, the database review's
    /// N5: the splices took the first <c>connections</c> and the deserializer the last, so an encryption went into the section never read and
    /// a wizard's add never appeared); null when once or not at all, or for JSON it can't read.
    /// </summary>
    public static string? RepeatedSection(ReadOnlySpan<byte> json, string section)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            int seen = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase) && ++seen > 1)
                {
                    return section;
                }

                reader.Read();
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // The deserializer says what is wrong with the file.
        }

        return null;
    }

    /// <summary>Where <see cref="ReplaceConnection"/> splices: the entry's name token to the end of its value, or why not.</summary>
    private readonly record struct EntrySpot(int Start, int End, string? Error);

    /// <summary>
    /// The byte range of <c>&lt;section&gt;.&lt;oldName&gt;</c> in <paramref name="json"/> (its name's quote to its value's end), refused
    /// when the root or the section is not an object, the entry is missing, or another entry is called <paramref name="newName"/>.
    /// </summary>
    private static EntrySpot LocateEntry(ReadOnlySpan<byte> json, string oldName, string newName, string section, string noun)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return new EntrySpot(0, 0, SqlText.FileNotAnObject);
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isSection = reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase);
            reader.Read();
            if (!isSection)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return new EntrySpot(0, 0, section == DefaultSection ? SqlText.ConnectionsNotAnObject : SqlText.SectionNotAnObject(section));
            }

            EntrySpot? found = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string name = reader.GetString()!.Trim();
                int start = (int)reader.TokenStartIndex;
                reader.Read();
                reader.Skip();
                if (found is null && string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    found = new EntrySpot(start, (int)reader.BytesConsumed, null);
                }
                else if (string.Equals(name, newName, StringComparison.OrdinalIgnoreCase))
                {
                    return new EntrySpot(0, 0, SqlText.ConnectionAlreadyInFile(newName, noun));
                }
            }

            return found ?? new EntrySpot(0, 0, SqlText.ConnectionNotInFile(oldName, noun));
        }

        return new EntrySpot(0, 0, SqlText.ConnectionNotInFile(oldName, noun));
    }

    /// <summary>
    /// Writes <paramref name="emptyText"/> to <paramref name="path"/> when no file is there (the folder made first), or when the file holds
    /// only whitespace (2026-10-24, the database review's N15: an emptied file was never filled, so every add failed on it for good); true
    /// when it wrote. Throws on an IO failure — the caller's notice.
    /// </summary>
    public static bool EnsureExists(string path, string emptyText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(emptyText);
        lock (LockFor(path))
        {
            using var _ = CrossProcess(path);
            return EnsureExistsLocked(path, emptyText);
        }
    }

    private static bool EnsureExistsLocked(string path, string emptyText)
    {
        if (File.Exists(path) && !string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(ForEdit(File.ReadAllBytes(path), out _)).TrimStart('\uFEFF')))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WorkingDirectory.WriteAtomically(path, Encoding.UTF8.GetBytes(emptyText));
        Computers.ConfigCache.Invalidate(path);
        return true;
    }

    private static int Bom(byte[] bytes) => bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;

    /// <summary>
    /// A file's bytes as UTF-8 for the edits (2026-10-24, the database review's N3: the loads read a UTF-16 file through its byte-order mark,
    /// but every splice ran a UTF-8 reader over its raw bytes and failed, so a plain password in it was never encrypted and a wizard could
    /// not add to it): a UTF-16 or UTF-32 file transcoded, <paramref name="wide"/> its encoding for the write back; a UTF-8 file as it is.
    /// </summary>
    public static byte[] ForEdit(byte[] raw, out Encoding? wide)
    {
        ArgumentNullException.ThrowIfNull(raw);
        wide = null;
        var span = raw.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            wide = new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            wide = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            wide = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }
        else if (span.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            wide = new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        if (wide is null)
        {
            return raw;
        }

        int preamble = wide.GetPreamble().Length;
        return Encoding.UTF8.GetBytes(wide.GetString(raw, preamble, raw.Length - preamble));
    }

    /// <summary>
    /// <paramref name="bytes"/> with <c>[start, end)</c> replaced by <paramref name="insert"/>, written back whole through
    /// <see cref="Files.WorkingDirectory.WriteAtomically"/> (2026-10-24, the database review's B16: a temp file and a move by hand gave the file
    /// the folder's inherited ACL, broke a hard link and wrote over a read-only file; the shared replace keeps all three), in
    /// <paramref name="wide"/> when the file was UTF-16 or UTF-32 (N3).
    /// </summary>
    private static void Splice(string path, byte[] bytes, int start, int end, string insert, Encoding? wide)
    {
        byte[] inserted = Encoding.UTF8.GetBytes(insert);
        var rewritten = new byte[bytes.Length - (end - start) + inserted.Length];
        bytes.AsSpan(0, start).CopyTo(rewritten);
        inserted.CopyTo(rewritten.AsSpan(start));
        bytes.AsSpan(end).CopyTo(rewritten.AsSpan(start + inserted.Length));
        byte[] written = wide is null ? rewritten : [.. wide.GetPreamble(), .. wide.GetBytes(Encoding.UTF8.GetString(rewritten))];
        Files.WorkingDirectory.WriteAtomically(path, written);
    }

    /// <summary>
    /// <see cref="Splice"/>'s twin for several ranges (2026-11-20, <see cref="WriteValues"/>): each <c>[Start, End)</c> replaced by its text,
    /// applied from the last so the earlier offsets still hold, then one atomic write in the file's own encoding. The ranges never overlap
    /// (each is one string token).
    /// </summary>
    private static void SpliceAll(string path, byte[] bytes, List<(int Start, int End, string Insert)> splices, Encoding? wide)
    {
        byte[] rewritten = bytes;
        foreach (var (start, end, insert) in splices.OrderByDescending(s => s.Start))
        {
            byte[] inserted = Encoding.UTF8.GetBytes(insert);
            var next = new byte[rewritten.Length - (end - start) + inserted.Length];
            rewritten.AsSpan(0, start).CopyTo(next);
            inserted.CopyTo(next.AsSpan(start));
            rewritten.AsSpan(end).CopyTo(next.AsSpan(start + inserted.Length));
            rewritten = next;
        }

        byte[] written = wide is null ? rewritten : [.. wide.GetPreamble(), .. wide.GetBytes(Encoding.UTF8.GetString(rewritten))];
        Files.WorkingDirectory.WriteAtomically(path, written);
    }

    /// <summary>How long an edit waits for another process's edit of the same file (a test shortens it).</summary>
    internal static TimeSpan CrossProcessWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The edit's hold on <paramref name="path"/> across processes (2026-10-24, the database review's N4: the lock below is this process's
    /// alone, so two app instances sharing the home's <c>configs</c> each read, spliced and wrote, and one's write — a wizard's add, its
    /// secret already saved — was lost): <c>&lt;file&gt;.lock</c> opened for this process alone and removed when the edit ends, waited for up to
    /// <see cref="CrossProcessWait"/>; past it, an <see cref="IOException"/> naming the file.
    /// </summary>
    private static IDisposable CrossProcess(string path, TimeSpan? wait = null)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!Directory.Exists(folder))
        {
            return Nothing.Instance;
        }

        string lockPath = path + ".lock";
        var deadline = DateTime.UtcNow + (wait ?? CrossProcessWait);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException ex)
            {
                throw new IOException(SqlText.FileBusy(path), ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // A folder the app may read but not write (2026-10-24, the review of the fixes): an IO failure like any other, so the family's
                // load, which never throws, keeps a plain password plain rather than failing.
                throw new IOException(ex.Message, ex);
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> s_locks = new(StringComparer.Ordinal);

    /// <summary>The one lock every read-modify-write of <paramref name="path"/> takes (F6), by its full path.</summary>
    private static object LockFor(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        return s_locks.GetOrAdd(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full, _ => new object());
    }

    /// <summary>
    /// The entries under <paramref name="section"/> that hold one key twice (keys ignore case, as the deserializer reads them), each with
    /// the key (2026-10-23, the admin review's C7). Empty for JSON it can't read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RepeatedKeys(ReadOnlySpan<byte> json, string section)
    {
        var repeated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return repeated;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isSection = reader.GetString()!.Equals(section, StringComparison.OrdinalIgnoreCase);
                reader.Read();
                if (!isSection || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!.Trim();
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }

                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString()!;
                        if (!keys.Add(key))
                        {
                            repeated.TryAdd(name, key);
                        }

                        reader.Read();
                        reader.Skip();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The deserializer says what is wrong with the file.
        }

        return repeated;
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

    /// <summary>Where <see cref="WritePassword(string, string, string, string, string)"/> writes: the byte range to replace (empty for an insert), for an insert what goes either side of the new key, and the value there now (null for none).</summary>
    private readonly record struct PasswordSpot(int Start, int End, bool Replace, string Prefix, string Suffix, string? Current = null);

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
                        return new PasswordSpot(start, start + length, true, "", "", reader.TokenType == JsonTokenType.Null ? null : reader.GetString());
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
