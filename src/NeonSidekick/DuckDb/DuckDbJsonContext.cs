using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.DuckDb;

/// <summary>
/// The source-generated context for <c>duckdb.json</c> (2026-10-25), <c>SqliteJsonContext</c>'s twin: a dictionary of database
/// entries keyed by name. Reflection serialisation is off. Camel-case keys, comments and a trailing comma are tolerated on the way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DuckDbConfigFile))]
[JsonSerializable(typeof(DuckDbDatabaseConfig))]
public sealed partial class DuckDbJsonContext : JsonSerializerContext;
