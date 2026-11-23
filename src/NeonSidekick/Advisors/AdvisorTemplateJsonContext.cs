using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeonSidekick.Advisors;

/// <summary>
/// An advisor template's <c>advisor.json</c> (2026-11-22, the advisor templates): the role it advises in, a line on when to ask it, and the
/// avatar a profile made from it wears. Each optional: a home template written by hand may leave any out.
/// </summary>
public sealed class AdvisorTemplateFile
{
    public string? Role { get; set; }

    public string? Description { get; set; }

    public string? Avatar { get; set; }
}

/// <summary>
/// The source-generated context for <c>advisor.json</c> (2026-11-22), the hand-edited configs' shape (<c>DuckDbJsonContext</c>'s):
/// lower-case keys as the shipped files have them, any case read, comments and a trailing comma tolerated, no <c>null</c> key written.
/// Reflection serialisation is off.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AdvisorTemplateFile))]
public sealed partial class AdvisorTemplateJsonContext : JsonSerializerContext;
