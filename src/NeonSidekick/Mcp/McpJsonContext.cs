using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Mcp;

/// <summary>
/// The source-generated context for <c>mcp.json</c> (2026-09-20); a sibling of <c>MemoryJsonContext</c>.
/// Reflection serialisation is off. The file is the ecosystem's shape — a dictionary of server objects
/// keyed by name — which is why this is the one JSON file in the app that is not a flat POCO: the
/// generator handles <c>Dictionary&lt;string, T&gt;</c> without reflection, and a config pasted from another
/// MCP client must load unchanged. Camel-case keys, comments and a trailing comma are tolerated on the
/// way in; the way out is indented.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(McpConfigFile))]
[JsonSerializable(typeof(McpServerConfig))]
[JsonSerializable(typeof(McpOAuthConfig))]
[JsonSerializable(typeof(Dictionary<string, McpTokenEntry>))]
[JsonSerializable(typeof(ModelContextProtocol.Authentication.TokenContainer))]
public sealed partial class McpJsonContext : JsonSerializerContext;
