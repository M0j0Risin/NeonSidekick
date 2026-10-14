using System.Text.Json.Serialization;

namespace NeonSidekick.Prompts;

/// <summary>The saved prompts' file (2026-10-13, <see cref="SavedPrompts"/>), source-generated for NativeAOT.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedPromptsFile))]
public sealed partial class PromptsJsonContext : JsonSerializerContext;
