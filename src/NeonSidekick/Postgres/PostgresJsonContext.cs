using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Postgres;

/// <summary>
/// The source-generated context for <c>postgres.json</c> (2026-10-04), <see cref="Sql.SqlJsonContext"/>'s twin: a dictionary
/// of connection objects keyed by name. Reflection serialisation is off. Camel-case keys, comments and a trailing comma
/// are tolerated on the way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PostgresConfigFile))]
[JsonSerializable(typeof(PostgresConnectionConfig))]
public sealed partial class PostgresJsonContext : JsonSerializerContext;
