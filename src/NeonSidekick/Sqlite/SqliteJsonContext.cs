using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Sqlite;

/// <summary>
/// The source-generated context for <c>sqlite.json</c> (2026-10-04), <c>MySqlJsonContext</c>'s twin: a dictionary of database
/// entries keyed by name. Reflection serialisation is off. Camel-case keys, comments and a trailing comma are tolerated on the way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SqliteConfigFile))]
[JsonSerializable(typeof(SqliteDatabaseConfig))]
public sealed partial class SqliteJsonContext : JsonSerializerContext;
