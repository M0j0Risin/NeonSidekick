using System.Text.Json.Serialization;

namespace NeonSidekick.Settings;

/// <summary>
/// AOT-safe JSON serialisation context for the settings file.
///
/// <para>Any new persisted type must be added here. Reflection-based serialisation is disabled
/// project-wide (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>), so a type that is
/// missing throws under <c>dotnet test</c> as well as in the published binary.</para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettingsData))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
