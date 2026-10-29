using System.Text.Json.Serialization;

namespace NeonSidekick.Files;

/// <summary>What one <see cref="CheckpointEntry"/> records, so a restore can undo it (2026-10-28, the checkpoints plan).</summary>
public enum CheckpointOp
{
    /// <summary>A file was there before it was replaced, appended to or deleted: its bytes (or <see cref="CheckpointEntry.NotSaved"/>).</summary>
    Content,

    /// <summary>Nothing was there before a file or folder was made: a restore deletes it (a folder only once empty).</summary>
    Absent,

    /// <summary>A file or folder moved to an empty <see cref="CheckpointEntry.To"/>: a restore moves it back.</summary>
    Moved,

    /// <summary>A folder was there before it was deleted: a restore makes it again.</summary>
    Folder,

    /// <summary>A symbolic link or junction was there before it was deleted: a restore makes the link again, never its target's content.</summary>
    Link,
}

/// <summary>What stands at a path: nothing, a file, a folder or a link.</summary>
public enum CheckpointKind
{
    Absent,
    File,
    Folder,
    Link,
}

/// <summary>
/// One step of a turn's op log (2026-10-28, the checkpoints plan), in the order the writes came; a restore replays them backwards. Paths are
/// relative to the checkpoint's root with <c>/</c> separators, in their on-disk case.
/// </summary>
public sealed class CheckpointEntry
{
    public CheckpointOp Op { get; set; }

    public string Path { get; set; } = "";

    /// <summary>A move's destination.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }

    /// <summary>An <see cref="CheckpointOp.Absent"/> folder, a moved folder or a folder link.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Directory { get; set; }

    /// <summary>A saved file's name in the checkpoint's tree (<c>0001</c>…); null while the turn runs it holds the blob's id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; set; }

    /// <summary>A content's size in bytes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Size { get; set; }

    /// <summary>A saved content's SHA-256 (hex), or a moved file's.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }

    /// <summary>A content's file attributes (read-only, hidden…).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Attributes { get; set; }

    /// <summary>A content's Unix mode, on a Mac.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Mode { get; set; }

    /// <summary>A content's internet mark, base64; null for none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mark { get; set; }

    /// <summary>A link's target.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    /// <summary>Why a content's bytes weren't kept (too big, a cap, an online-only file, the store failed); null when they were.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotSaved { get; set; }

    /// <summary>A copy (2026-10-28, review P11-12): the commit names its blobs on its own, never on the journal's live entries.</summary>
    public CheckpointEntry Clone() => (CheckpointEntry)MemberwiseClone();
}

/// <summary>
/// What a path held when the turn ended (its after-state, taken at the seal): content, not write time, decides a conflict, so a second
/// <c>/undo</c> never mistakes its own earlier restore for the user's edit. A file past the hashing budget keeps its size and write time.
/// </summary>
public sealed class CheckpointState
{
    /// <summary>
    /// The <see cref="Written"/> of a file that couldn't be read (2026-10-28, review P7-06): never equal to anything, so an unreadable file is
    /// a conflict, never written over unseen.
    /// </summary>
    public const long UnknownWritten = -1;

    public string Path { get; set; } = "";

    public CheckpointKind Kind { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Size { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }

    /// <summary>A file's write time (UTC ticks), set whether hashed or not (2026-10-28, review P1-06), <see cref="UnknownWritten"/> when unreadable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Written { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    /// <summary>
    /// Whether <paramref name="other"/> is the same thing: kinds, and a file's hash when both have one, else its size and write time when both
    /// are known (2026-10-28, review P1-06: a seal past the hash budget kept only the write time, which a plan that hashed never matched, a false
    /// "changed since"), else not (P7-06: unknown content is never the same). A link's target, a missing one read as empty (P6-05).
    /// </summary>
    public bool Matches(CheckpointState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            CheckpointKind.File => Size == other.Size && (Hash is not null && other.Hash is not null
                ? string.Equals(Hash, other.Hash, StringComparison.OrdinalIgnoreCase)
                : Written is { } written && written != UnknownWritten && other.Written == written),
            CheckpointKind.Link => string.Equals(Target ?? "", other.Target ?? "", StringComparison.Ordinal),
            _ => true,
        };
    }
}

/// <summary>
/// One turn's checkpoint in one root (<c>neon-checkpoint.json</c> in the commit's tree, 2026-10-28, the checkpoints plan). <see cref="Version"/>
/// is 1; a build that finds a newer one leaves the checkpoint alone.
/// </summary>
public sealed class CheckpointRecord
{
    /// <summary>The version this build writes and reads.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The checkpoint id (the turn's <c>neon.checkpoint</c> stamp), shared by a pair of roots.</summary>
    public string Id { get; set; } = "";

    /// <summary>The root's full path when the turn ran.</summary>
    public string Root { get; set; } = "";

    /// <summary>The session's number written invariant, or <c>none</c> with <c>Session logging</c> off.</summary>
    public string Session { get; set; } = CheckpointStore.NoSession;

    /// <summary>The turn's ordinal in its session (0 when unknown).</summary>
    public int Turn { get; set; }

    /// <summary>The start of the turn's message (about 40 characters), for the model note and the report.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>When the turn ended, by the app's clock.</summary>
    public DateTimeOffset Time { get; set; }

    /// <summary>The tools the turn ran, in first-run order.</summary>
    public List<string> Tools { get; set; } = new();

    public List<CheckpointEntry> Entries { get; set; } = new();

    public List<CheckpointState> After { get; set; } = new();

    /// <summary>The bytes this checkpoint's saved contents hold, what retention sums.</summary>
    public long SavedBytes { get; set; }

    /// <summary>Some of the turn's changes weren't kept (a write after the seal, the entry cap).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Incomplete { get; set; }
}

/// <summary>The source-generated context for <see cref="CheckpointRecord"/>; indented, so <c>git show</c> reads it. Reflection serialization is off.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CheckpointRecord))]
public sealed partial class CheckpointJsonContext : JsonSerializerContext;
