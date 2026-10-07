using System.Text.Json.Serialization;

namespace NeonSidekick.YouTube;

/// <summary>The source-generated context for <c>youtube.json</c>, the saved videos (2026-10-07). Reflection serialisation is off.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(YouTubeLibraryFile))]
public sealed partial class YouTubeJsonContext : JsonSerializerContext;
