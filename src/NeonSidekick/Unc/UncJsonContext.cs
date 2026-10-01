using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Unc;

/// <summary>
/// The source-generated context for <c>unc.json</c> (2026-09-30), <see cref="Sql.SqlJsonContext"/>'s twin: a dictionary of share
/// objects keyed by name. Reflection serialisation is off. Camel-case keys, comments and a trailing comma are tolerated on the
/// way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UncConfigFile))]
[JsonSerializable(typeof(UncShareConfig))]
public sealed partial class UncJsonContext : JsonSerializerContext;
