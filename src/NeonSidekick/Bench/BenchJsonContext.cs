using System.Text.Json.Serialization;

namespace NeonSidekick.Bench;

/// <summary>The source-generated context for <c>tests.json</c> (2026-09-28); verdicts as their names, so the file reads. Reflection serialisation is off.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(BenchHistoryFile))]
public sealed partial class BenchJsonContext : JsonSerializerContext;
