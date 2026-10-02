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
/// <para>One file per preset since 2026-10-02 (the user's call, the themes folder's shape; one <c>voice_presets.json</c>, embedded
/// and replaced whole by a home file of that name, until then). The built-ins are <c>assets\voices\built-in\&lt;name&gt;.json</c>,
/// embedded as <see cref="BuiltInResourcePrefix"/><c>&lt;name&gt;.json</c>, in <see cref="BuiltInNames"/>' order. The user's are
/// <c>&lt;home&gt;\voices\*.json</c> (<see cref="DirectoryName"/>): the file's name is the preset's, and a file named like a built-in
/// (any case) takes its place in the list; any other follows the built-ins, in name order. A file is one JSON object,
/// <c>{ "TtsVoice": "af_heart", "TtsVoice2": "am_eric", "TtsVoiceMix": 80, "TtsSpeed": 1.2 }</c>, read through
/// <see cref="JsonDocument"/> (comments and trailing commas allowed). A file with a blank voice, a mix or speed outside the settings'
/// ranges, or no JSON object in it is skipped with a warning, never clamped — an override that is skipped leaves its built-in in
/// place. Subfolders are not read. Never throws.</para>
///
/// <para>The row's value is rendered with every pane draw, so <see cref="Load"/> caches the list per home and re-reads only when
/// the folder's files change (a name, a write time or a length), so a file dropped in or edited shows the next time the row draws.</para>
/// </summary>
public static class VoicePresets
{
    /// <summary>The folder of the user's presets under the home.</summary>
    public const string DirectoryName = "voices";

    /// <summary>The csproj's <c>LogicalName</c> prefix for the built-in files.</summary>
    public const string BuiltInResourcePrefix = "voices/built-in/";

    /// <summary>The built-ins, in the picker's order (the order of the one file they came from until 2026-10-02).</summary>
    public static readonly IReadOnlyList<string> BuiltInNames = ["amanda", "neon", "richard", "hunter", "larry", "jack", "willow"];

    private const string Category = "VoicePresets";

    private static readonly Lock Gate = new();
    private static string? _cachedHome;
    private static string? _cachedStamp;
    private static IReadOnlyList<VoicePreset>? _cached;
    private static IReadOnlyList<VoicePreset>? _embedded;

    /// <summary>The presets in force for <paramref name="homeDirectory"/>: the built-ins with its <see cref="DirectoryName"/> folder's files over and after them.</summary>
    public static IReadOnlyList<VoicePreset> Load(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string folder = Path.Combine(homeDirectory, DirectoryName);
        var files = Files(folder);
        string stamp = Stamp(files);
        lock (Gate)
        {
            if (_cached is not null && string.Equals(_cachedHome, homeDirectory, StringComparison.OrdinalIgnoreCase) && _cachedStamp == stamp)
            {
                return _cached;
            }

            var loaded = files.Count == 0 ? Embedded() : Merge(Embedded(), files);
            _cachedHome = homeDirectory;
            _cachedStamp = stamp;
            _cached = loaded;
            return loaded;
        }
    }

    /// <summary>The built-ins, parsed once. One the build lost, or that does not read, is left out (and warned about).</summary>
    public static IReadOnlyList<VoicePreset> Embedded()
    {
        lock (Gate)
        {
            if (_embedded is not null)
            {
                return _embedded;
            }

            var presets = new List<VoicePreset>(BuiltInNames.Count);
            foreach (string name in BuiltInNames)
            {
                string resource = BuiltInResourcePrefix + name + ".json";
                using var stream = typeof(VoicePresets).Assembly.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    DiagnosticLog.Warn(Category, $"No embedded {resource}.");
                    continue;
                }

                using var reader = new StreamReader(stream);
                if (Parse(name, reader.ReadToEnd(), resource) is { } preset)
                {
                    presets.Add(preset);
                }
            }

            return _embedded = presets;
        }
    }

    /// <summary>The <c>*.json</c> files right in <paramref name="folder"/>, in name order; none for a folder that is missing or unreadable (warned about).</summary>
    private static IReadOnlyList<FileInfo> Files(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {folder}; using the built-in presets.", ex);
            return [];
        }
    }

    /// <summary>What the cache compares: every file's name, write time and length.</summary>
    private static string Stamp(IReadOnlyList<FileInfo> files) =>
        string.Join('|', files.Select(f => f.Name + ":" + f.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + f.Length.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The built-ins with each readable file over the one of its name, then the rest of the files' presets in name order.</summary>
    private static IReadOnlyList<VoicePreset> Merge(IReadOnlyList<VoicePreset> builtIns, IReadOnlyList<FileInfo> files)
    {
        var list = builtIns.ToList();
        var added = new List<VoicePreset>();
        foreach (var file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file.Name).Trim();
            if (name.Length == 0 || FromFile(name, file.FullName) is not { } preset)
            {
                continue;
            }

            int index = list.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                list[index] = preset with { Name = list[index].Name };   // an override keeps the built-in's place and name
            }
            else
            {
                added.Add(preset);
            }
        }

        list.AddRange(added);
        return list;
    }

    private static VoicePreset? FromFile(string name, string path)
    {
        try
        {
            return Parse(name, File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {path}; it is skipped.", ex);
            return null;
        }
    }

    /// <summary>
    /// The preset <paramref name="name"/> in <paramref name="json"/>, one file's object; null (warned about, <paramref name="source"/>
    /// naming it in the log) when it is not a JSON object or its values are out of the settings' ranges.
    /// </summary>
    public static VoicePreset? Parse(string name, string json, string source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (ToPreset(name, document.RootElement) is { } preset)
            {
                return preset;
            }

            DiagnosticLog.Warn(Category, $"Skipped the preset {name} in {source}: it needs a TtsVoice, a TtsVoiceMix of {AppSettingsData.MinTtsVoiceMix}–{AppSettingsData.MaxTtsVoiceMix} and a TtsSpeed of {AppSettingsData.MinTtsSpeed.ToString(CultureInfo.InvariantCulture)}–{AppSettingsData.MaxTtsSpeed.ToString(CultureInfo.InvariantCulture)}.");
            return null;
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"Skipped the preset {name}: {source} is not valid JSON.", ex);
            return null;
        }
    }

    private static VoicePreset? ToPreset(string name, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(name) || value.ValueKind != JsonValueKind.Object
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

        return new VoicePreset(name.Trim(), voice.GetString()!.Trim(), voice2, mix, speed);
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
