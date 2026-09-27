using System.Globalization;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Speech;

/// <summary>One named voice preset: the four TTS settings it writes, in the settings' own units (the mix the primary voice's percent, the speed a multiplier).</summary>
public sealed record VoicePreset(string Name, string Voice, string Voice2, int Mix, double Speed);

/// <summary>
/// The TTS voice presets (2026-09-27, the user's ask): named blends of <c>TTS voice</c>, <c>TTS voice 2</c>,
/// <c>TTS voice mix</c> and <c>TTS speed</c>, picked on the TTS tab's <c>TTS voice preset</c> row, which writes
/// all four in one save. Nothing about the preset is persisted — the row shows the preset whose four values
/// match the saved ones (<see cref="Match"/>), or <c>(custom)</c>, so a hand edit of any of the four can never
/// leave a stale name behind (the user's call).
///
/// <para>The list is <c>assets\voices\voice_presets.json</c>, embedded as the manifest resource
/// <see cref="ResourceName"/>; a <c>&lt;home&gt;\voice_presets.json</c> replaces it whole (the user's call, so a
/// preset can be added without a build). The file is one JSON object, a key per preset in the order shown,
/// each <c>{ "TtsVoice": "af_heart", "TtsVoice2": "am_eric", "TtsVoiceMix": 80, "TtsSpeed": 1.2 }</c>. Read
/// through <see cref="JsonDocument"/> (no serializer context: the key order is the list's order). An entry with a blank
/// voice or a mix or speed outside the settings' ranges is skipped with a warning, never clamped; a home file
/// that does not parse is warned about and the embedded list used. Never throws.</para>
///
/// <para>The row's value is rendered with every pane draw, so <see cref="Load"/> caches the list per home and
/// re-reads only when the home file appears, disappears or changes its write time.</para>
/// </summary>
public static class VoicePresets
{
    /// <summary>The home override's file name, and the asset's.</summary>
    public const string FileName = "voice_presets.json";

    /// <summary>The csproj's <c>LogicalName</c> for the embedded list.</summary>
    public const string ResourceName = "voices/voice_presets.json";

    private const string Category = "VoicePresets";

    private static readonly Lock Gate = new();
    private static string? _cachedHome;
    private static DateTime? _cachedStamp;
    private static IReadOnlyList<VoicePreset>? _cached;
    private static IReadOnlyList<VoicePreset>? _embedded;

    /// <summary>The presets in force for <paramref name="homeDirectory"/>: its <see cref="FileName"/> when present and readable, else the embedded list.</summary>
    public static IReadOnlyList<VoicePreset> Load(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string path = Path.Combine(homeDirectory, FileName);
        DateTime? stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        lock (Gate)
        {
            if (_cached is not null && string.Equals(_cachedHome, homeDirectory, StringComparison.OrdinalIgnoreCase) && _cachedStamp == stamp)
            {
                return _cached;
            }

            var loaded = (stamp is not null ? FromFile(path) : null) ?? Embedded();
            _cachedHome = homeDirectory;
            _cachedStamp = stamp;
            _cached = loaded;
            return loaded;
        }
    }

    /// <summary>The embedded list, parsed once. Empty (and warned about) only if the build lost the resource.</summary>
    public static IReadOnlyList<VoicePreset> Embedded()
    {
        lock (Gate)
        {
            if (_embedded is not null)
            {
                return _embedded;
            }

            using var stream = typeof(VoicePresets).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                DiagnosticLog.Warn(Category, $"No embedded {ResourceName}.");
                return _embedded = [];
            }

            using var reader = new StreamReader(stream);
            return _embedded = Parse(reader.ReadToEnd(), ResourceName) ?? [];
        }
    }

    private static IReadOnlyList<VoicePreset>? FromFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {path}; using the built-in presets.", ex);
            return null;
        }
    }

    /// <summary>The presets in <paramref name="json"/>, in key order; null (warned about) when it is not a JSON object. <paramref name="source"/> names it in the log.</summary>
    public static IReadOnlyList<VoicePreset>? Parse(string json, string source)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                DiagnosticLog.Warn(Category, $"{source} is not a JSON object; using the built-in presets.");
                return null;
            }

            var presets = new List<VoicePreset>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (ToPreset(entry) is { } preset)
                {
                    presets.Add(preset);
                }
                else
                {
                    DiagnosticLog.Warn(Category, $"Skipped the preset {entry.Name} in {source}: it needs a TtsVoice, a TtsVoiceMix of {AppSettingsData.MinTtsVoiceMix}–{AppSettingsData.MaxTtsVoiceMix} and a TtsSpeed of {AppSettingsData.MinTtsSpeed.ToString(CultureInfo.InvariantCulture)}–{AppSettingsData.MaxTtsSpeed.ToString(CultureInfo.InvariantCulture)}.");
                }
            }

            return presets;
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"{source} is not valid JSON; using the built-in presets.", ex);
            return null;
        }
    }

    private static VoicePreset? ToPreset(JsonProperty entry)
    {
        var value = entry.Value;
        if (string.IsNullOrWhiteSpace(entry.Name) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("TtsVoice", out var voice) || voice.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(voice.GetString())
            || !value.TryGetProperty("TtsVoiceMix", out var mixElement) || mixElement.ValueKind != JsonValueKind.Number || !mixElement.TryGetInt32(out int mix)
            || !value.TryGetProperty("TtsSpeed", out var speedElement) || speedElement.ValueKind != JsonValueKind.Number || !speedElement.TryGetDouble(out double speed))
        {
            return null;
        }

        string voice2 = "";
        if (value.TryGetProperty("TtsVoice2", out var second))
        {
            if (second.ValueKind == JsonValueKind.String)
            {
                voice2 = second.GetString()!.Trim();
            }
            else if (second.ValueKind != JsonValueKind.Null)
            {
                return null;
            }
        }

        if (mix < AppSettingsData.MinTtsVoiceMix || mix > AppSettingsData.MaxTtsVoiceMix
            || double.IsNaN(speed) || speed < AppSettingsData.MinTtsSpeed || speed > AppSettingsData.MaxTtsSpeed)
        {
            return null;
        }

        return new VoicePreset(entry.Name.Trim(), voice.GetString()!.Trim(), voice2, mix, speed);
    }

    /// <summary>The first preset whose four values are <paramref name="data"/>'s (voices ordinal after a trim, a blank second voice matching a blank one, the speed within 1e-9); null = custom.</summary>
    public static VoicePreset? Match(IReadOnlyList<VoicePreset> presets, AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentNullException.ThrowIfNull(data);
        string voice = (data.TtsVoice ?? "").Trim();
        string voice2 = (data.TtsVoice2 ?? "").Trim();
        foreach (var preset in presets)
        {
            if (string.Equals(preset.Voice, voice, StringComparison.Ordinal)
                && string.Equals(preset.Voice2, voice2, StringComparison.Ordinal)
                && preset.Mix == data.TtsVoiceMix
                && Math.Abs(preset.Speed - data.TtsSpeed) < 1e-9)
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>Writes <paramref name="preset"/>'s four values into <paramref name="data"/> — the picker's one save.</summary>
    public static void ApplyTo(VoicePreset preset, AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(data);
        data.TtsVoice = preset.Voice;
        data.TtsVoice2 = preset.Voice2;
        data.TtsVoiceMix = preset.Mix;
        data.TtsSpeed = preset.Speed;
    }
}
