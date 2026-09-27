using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests;

public class VoicePresetsTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public VoicePresetsTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheEmbeddedList_IsTheAssetsFile_InItsOrder()
    {
        var presets = VoicePresets.Load(_home);

        Assert.Equal(["amanda", "neon", "richard", "hunter", "larry", "jack", "willow"], presets.Select(p => p.Name));
        Assert.Equal(new VoicePreset("hunter", "am_adam", "am_michael", 70, 1.3), presets[3]);
        Assert.Same(VoicePresets.Embedded(), presets);
    }

    [Fact]
    public void AHomeFile_ReplacesTheList_AndSkipsWhatItCannotTake()
    {
        File.WriteAllText(Path.Combine(_home, VoicePresets.FileName), """
            {
              "solo": { "TtsVoice": "af_sky", "TtsVoiceMix": 100, "TtsSpeed": 1.0 },
              "blank": { "TtsVoice": " ", "TtsVoice2": "am_eric", "TtsVoiceMix": 50, "TtsSpeed": 1.0 },
              "loud": { "TtsVoice": "af_sky", "TtsVoice2": "am_eric", "TtsVoiceMix": 101, "TtsSpeed": 1.0 },
              "fast": { "TtsVoice": "af_sky", "TtsVoice2": "am_eric", "TtsVoiceMix": 50, "TtsSpeed": 2.5 },
              "pair": { "TtsVoice": "bf_emma", "TtsVoice2": "bm_lewis", "TtsVoiceMix": 40, "TtsSpeed": 0.9 },
            }
            """);

        var presets = VoicePresets.Load(_home);

        Assert.Equal([new VoicePreset("solo", "af_sky", "", 100, 1.0), new VoicePreset("pair", "bf_emma", "bm_lewis", 40, 0.9)], presets);
    }

    [Fact]
    public void AHomeFileThatDoesNotParse_FallsBackToTheEmbeddedList()
    {
        File.WriteAllText(Path.Combine(_home, VoicePresets.FileName), "{ not json");

        Assert.Same(VoicePresets.Embedded(), VoicePresets.Load(_home));
        Assert.Null(VoicePresets.Parse("[]", "test"));
    }

    [Fact]
    public void Match_FindsTheFreshProfile_AsNeon_AndNothingOnceOneValueMoves()
    {
        var presets = VoicePresets.Embedded();
        var data = new AppSettingsData();

        Assert.Equal("neon", VoicePresets.Match(presets, data)?.Name);
        data.TtsVoiceMix = 79;
        Assert.Null(VoicePresets.Match(presets, data));
    }

    [Fact]
    public void ApplyTo_WritesTheFour_ThenMatchNamesIt()
    {
        var presets = VoicePresets.Embedded();
        var data = new AppSettingsData();
        var willow = presets.Single(p => p.Name == "willow");

        VoicePresets.ApplyTo(willow, data);

        Assert.Equal(("af_bella", "am_puck", 50, 1.2), (data.TtsVoice, data.TtsVoice2, data.TtsVoiceMix, data.TtsSpeed));
        Assert.Same(willow, VoicePresets.Match(presets, data));
    }
}
