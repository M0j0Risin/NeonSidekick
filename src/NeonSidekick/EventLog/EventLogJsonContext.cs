using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.EventLog;

/// <summary>
/// The source-generated context for <c>eventlog.json</c> (2026-10-15, <see cref="Unc.UncJsonContext"/>'s twin): a dictionary of computer
/// objects keyed by name. Reflection serialisation is off. Camel-case keys, comments and a trailing comma are tolerated on the way in.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EventLogConfigFile))]
[JsonSerializable(typeof(EventLogComputerConfig))]
public sealed partial class EventLogJsonContext : JsonSerializerContext;
