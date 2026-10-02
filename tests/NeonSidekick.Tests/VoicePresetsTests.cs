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
    public void TheEmbeddedList_IsTheBuiltIns_InTheirOrder()
    {
        var presets = VoicePresets.Load(_home);

        Assert.Equal(["amanda", "neon", "richard", "hunter", "larry", "jack", "willow"], presets.Select(p => p.Name));
        Assert.Equal(new VoicePreset("hunter", "am_adam", "am_michael", 70, 1.3), presets[3]);
        Assert.Same(VoicePresets.Embedded(), presets);
    }

    private string Voices => Path.Combine(_home, VoicePresets.DirectoryName);

    private void Put(string file, string json)
    {
        Directory.CreateDirectory(Voices);
        File.WriteAllText(Path.Combine(Voices, file), json);
    }

    // ── The shipped collection (assets/voices, 2026-10-02) ──────────────────

    /// <summary>The repository's <c>assets/voices</c>: the built-ins' files, one folder per category, the rendered samples.</summary>
    private static string Assets()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "assets", "voices");
    }

    private const string BuiltInFolder = "built-in";
    private const string SamplesFolder = "samples";

    /// <summary>Kokoro v1.0's voices (KokoroSharp 0.8.0's <c>voices\VOICES.md</c>): what a preset may name.</summary>
    private static readonly HashSet<string> KokoroVoices =
    [
        "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
        "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael", "am_onyx", "am_puck", "am_santa",
        "bf_alice", "bf_emma", "bf_isabella", "bf_lily", "bm_daniel", "bm_fable", "bm_george", "bm_lewis",
        "ef_dora", "em_alex", "em_santa", "ff_siwis", "hf_alpha", "hf_beta", "hm_omega", "hm_psi", "if_sara", "im_nicola",
        "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo", "pf_dora", "pm_alex", "pm_santa",
        "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi", "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang",
    ];

    private static IEnumerable<string> CategoryFolders() =>
        Directory.EnumerateDirectories(Assets()).Where(d => Path.GetFileName(d) is not (BuiltInFolder or SamplesFolder));

    [Fact]
    public void TheBuiltIns_AreOneEmbeddedFilePerPreset_TheAssetsFolderWhole()
    {
        // assets/voices/built-in holds one file per built-in; every one is embedded, in the order, and parses as the embedded one.
        string builtIns = Path.Combine(Assets(), BuiltInFolder);
        Assert.Equal(VoicePresets.BuiltInNames.Order(StringComparer.Ordinal), Directory.GetFiles(builtIns, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        Assert.Equal(VoicePresets.BuiltInNames, VoicePresets.Embedded().Select(p => p.Name));
        foreach (var preset in VoicePresets.Embedded())
        {
            Assert.Equal(preset, VoicePresets.Parse(preset.Name, File.ReadAllText(Path.Combine(builtIns, preset.Name + ".json")), "test"));
        }

        Assert.Equal("voices", VoicePresets.DirectoryName);
    }

    /// <summary>
    /// Every category's files copied into one voices folder, the way the README says to install them, and loaded: none skipped,
    /// none named like a built-in or like another, every voice one Kokoro has, and the accents English first.
    /// </summary>
    [Fact]
    public void TheCollection_LoadsWhole_FromOneFolder_NoNameTwice_EveryVoiceKokoros()
    {
        Directory.CreateDirectory(Voices);
        var names = new List<string>();
        foreach (string folder in CategoryFolders())
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                Assert.False(File.Exists(Path.Combine(Voices, Path.GetFileName(file))), name + " is in two folders");
                Assert.DoesNotContain(name, VoicePresets.BuiltInNames);
                Assert.Matches("^[a-z]+$", name);
                File.Copy(file, Path.Combine(Voices, Path.GetFileName(file)));
                names.Add(name);
            }
        }

        var presets = VoicePresets.Load(_home);

        Assert.True(names.Count >= 40, names.Count + " presets");
        Assert.Equal(VoicePresets.BuiltInNames.Concat(names.Order(StringComparer.OrdinalIgnoreCase)), presets.Select(p => p.Name));   // none skipped
        Assert.All(presets, p => Assert.Contains(p.Voice, KokoroVoices));
        Assert.All(presets.Where(p => p.Voice2.Length > 0), p => Assert.Contains(p.Voice2, KokoroVoices));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(Assets(), "accents"), "*.json"))
        {
            var accent = presets.Single(p => p.Name == Path.GetFileNameWithoutExtension(file));
            Assert.True(accent.Voice[0] is 'a' or 'b' && accent.Mix >= 55, accent.Name + ": the English voice leads, at 55 % or more");   // the engine phonemises by the primary
            Assert.False(accent.Voice2[0] is 'a' or 'b', accent.Name + ": an accent blends in a non-English voice");
        }
    }

    [Fact]
    public void EveryPreset_HasARenderedSample_AndTheAtlas()
    {
        // tools/VoiceSamples.cs writes them; a preset added or renamed without rerunning it fails here.
        foreach (string folder in CategoryFolders().Append(Path.Combine(Assets(), BuiltInFolder)))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            {
                string sample = Path.Combine(Assets(), SamplesFolder, Path.GetFileName(folder), Path.GetFileNameWithoutExtension(file) + ".wav");
                Assert.True(File.Exists(sample), "no sample " + sample);
                Assert.True(new FileInfo(sample).Length > 16_000, "a silent sample " + sample);   // more than half a second at 16 kHz
            }
        }

        string atlas = File.ReadAllText(Path.Combine(Assets(), "Voice Atlas.html"));
        Assert.Equal(Directory.EnumerateFiles(Path.Combine(Assets(), SamplesFolder), "*.wav", SearchOption.AllDirectories).Count(), CountOf(atlas, "<audio "));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void AFolderFile_NamedLikeABuiltIn_TakesItsPlace_AnyOtherFollows_InNameOrder()
    {
        Put("Neon.json", """{ "TtsVoice": "af_sky", "TtsVoice2": "", "TtsVoiceMix": 100, "TtsSpeed": 1.0 }""");
        Put("zed.json", """{ "TtsVoice": "bf_emma", "TtsVoice2": "bm_lewis", "TtsVoiceMix": 40, "TtsSpeed": 0.9, }""");
        Put("ada.json", """
            // comments are fine
            { "TtsVoice": "af_bella", "TtsVoiceMix": 100, "TtsSpeed": 1.1 }
            """);
        Put("notes.txt", "not a preset");

        var presets = VoicePresets.Load(_home);

        Assert.Equal(["amanda", "neon", "richard", "hunter", "larry", "jack", "willow", "ada", "zed"], presets.Select(p => p.Name));
        Assert.Equal(new VoicePreset("neon", "af_sky", "", 100, 1.0), presets[1]);   // the built-in's name and place, the file's values
        Assert.Equal(new VoicePreset("zed", "bf_emma", "bm_lewis", 40, 0.9), presets[^1]);
    }

    [Fact]
    public void AFileItCannotTake_IsSkipped_AndAnOverrideThatIsSkipped_LeavesTheBuiltIn()
    {
        Put("blank.json", """{ "TtsVoice": " ", "TtsVoice2": "am_eric", "TtsVoiceMix": 50, "TtsSpeed": 1.0 }""");
        Put("loud.json", """{ "TtsVoice": "af_sky", "TtsVoiceMix": 101, "TtsSpeed": 1.0 }""");
        Put("fast.json", """{ "TtsVoice": "af_sky", "TtsVoiceMix": 50, "TtsSpeed": 2.5 }""");
        Put("list.json", "[]");
        Put("hunter.json", "{ not json");

        var presets = VoicePresets.Load(_home);

        Assert.Equal(VoicePresets.Embedded(), presets);
        Assert.Null(VoicePresets.Parse("x", "[]", "test"));
    }

    [Fact]
    public void AnEditedOrRemovedFile_IsReadAgain_AnUnchangedFolderIsNot()
    {
        Put("extra.json", """{ "TtsVoice": "af_sky", "TtsVoiceMix": 100, "TtsSpeed": 1.0 }""");
        var first = VoicePresets.Load(_home);
        Assert.Same(first, VoicePresets.Load(_home));
        Assert.Equal("extra", first[^1].Name);

        Put("extra.json", """{ "TtsVoice": "af_nova", "TtsVoiceMix": 100, "TtsSpeed": 1.25 }""");
        File.SetLastWriteTimeUtc(Path.Combine(Voices, "extra.json"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(new VoicePreset("extra", "af_nova", "", 100, 1.25), VoicePresets.Load(_home)[^1]);

        File.Delete(Path.Combine(Voices, "extra.json"));
        Assert.Same(VoicePresets.Embedded(), VoicePresets.Load(_home));
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
