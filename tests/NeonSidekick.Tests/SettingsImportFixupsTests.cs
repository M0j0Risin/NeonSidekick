using System.Text.Json;
using System.Text.Json.Nodes;
using NeonSidekick.Settings;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>What an imported <c>profile.json</c> gets reset to on the computer it lands on (2026-11-13, plan parsed-yawning-papert). Both systems driven by a fake context.</summary>
public sealed class SettingsImportFixupsTests
{
    private static readonly string Here = Path.GetTempPath();

    private static ImportEnvironment Environment(string thisOs = SettingsArchive.WindowsOs, Func<string, string?>? reveal = null, Func<string, bool>? directoryExists = null) =>
        new(thisOs, _ => false, directoryExists ?? (path => path == Here), reveal ?? (_ => null), thisOs == SettingsArchive.MacOs ? "zsh" : "powershell", "1.0.0");

    private static FixupContext Context(string thisOs = SettingsArchive.WindowsOs, string sourceOs = SettingsArchive.WindowsOs, Func<string, string?>? reveal = null,
        Dictionary<string, string>? renames = null) =>
        new(Environment(thisOs, reveal), sourceOs, renames ?? new(StringComparer.OrdinalIgnoreCase));

    private static (JsonObject Root, IReadOnlyList<string> Reset) Apply(string json, FixupContext context)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        return (root, SettingsImportFixups.Apply(root, context).Reset);
    }

    [Fact]
    public void AnUnreadableLlmKey_GoesBackToItsPlaceholder_AndTheOthersToBlank()
    {
        var (root, reset) = Apply("""{ "LlmApiKey": "dpapi:AAAA", "AnthropicApiKey": "dpapi:BBBB", "YouTubeApiKey": "plain" }""", Context());

        Assert.Equal("empty", (string?)root["LlmApiKey"]);
        Assert.Equal("", (string?)root["AnthropicApiKey"]);
        Assert.Equal("plain", (string?)root["YouTubeApiKey"]);
        Assert.Equal(["LlmApiKey", "AnthropicApiKey"], reset);
    }

    [Fact]
    public void AReadableKey_AndThePlaceholder_AreKept()
    {
        var (root, reset) = Apply("""{ "LlmApiKey": "empty", "OpenAIApiKey": "dpapi:CCCC" }""", Context(reveal: _ => "sk-x"));

        Assert.Equal("empty", (string?)root["LlmApiKey"]);
        Assert.Equal("dpapi:CCCC", (string?)root["OpenAIApiKey"]);
        Assert.Empty(reset);
    }

    [Theory]
    [InlineData(SettingsArchive.WindowsOs, "keychain:AAAA")]
    [InlineData(SettingsArchive.MacOs, "dpapi:AAAA")]
    public void AForeignBlob_IsBlankedByItsPrefix_WithoutADecrypt(string thisOs, string value)
    {
        bool asked = false;
        var (root, reset) = Apply($$"""{ "HomeAssistantToken": "{{value}}" }""", Context(thisOs, thisOs, reveal: _ => { asked = true; return "x"; }));

        Assert.Equal("", (string?)root["HomeAssistantToken"]);
        Assert.Equal(["HomeAssistantToken"], reset);
        Assert.False(asked);
    }

    [Fact]
    public void TheWebUiToken_IsAlwaysBlanked()
    {
        var (root, reset) = Apply("""{ "WebUiToken": "abc" }""", Context(reveal: v => v));

        Assert.Equal("", (string?)root["WebUiToken"]);
        Assert.Equal(["WebUiToken"], reset);
    }

    /// <summary>A typed HTTPS address is the old PC's (the review's #65); the words and blank stay.</summary>
    [Theory]
    [InlineData("https://old-pc.tail1234.ts.net", true)]
    [InlineData("auto", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData("not an address", false)]
    public void ATypedHttpsAddress_GoesBackToAuto(string value, bool reset)
    {
        var (root, keys) = Apply(new JsonObject { ["WebUiHttpsAddress"] = value }.ToJsonString(), Context());

        Assert.Equal(reset ? WebUi.WebUiHttpsSetting.Default : value, (string?)root["WebUiHttpsAddress"]);
        Assert.Equal(reset, keys.Contains("WebUiHttpsAddress"));
    }

    [Fact]
    public void PathsNotOnThisComputer_Reset_AndOnesThatAre_Stay()
    {
        string missing = Path.Combine(Here, "nope-" + Guid.NewGuid().ToString("N"));
        var (root, reset) = Apply(
            new JsonObject { ["WorkingDirectory"] = missing, ["ObsidianVault"] = missing, ["WebBrowserPath"] = "relative.exe", ["ClaudeCodeExecutable"] = "", ["ShortcutOffered"] = true }.ToJsonString(),
            Context());

        Assert.Equal("", (string?)root["WorkingDirectory"]);
        Assert.Equal("", (string?)root["ObsidianVault"]);
        Assert.Equal("", (string?)root["WebBrowserPath"]);
        Assert.Equal("", (string?)root["ClaudeCodeExecutable"]);
        Assert.False(root.ContainsKey("ShortcutOffered"));
        Assert.Equal(["WorkingDirectory", "ObsidianVault", "WebBrowserPath"], reset);

        var (kept, none) = Apply(new JsonObject { ["WorkingDirectory"] = " " + Here + " ", ["ObsidianVault"] = Here }.ToJsonString(), Context());
        Assert.Equal(" " + Here + " ", (string?)kept["WorkingDirectory"]);
        Assert.Equal(Here, (string?)kept["ObsidianVault"]);
        Assert.Empty(none);
    }

    /// <summary>A share is kept unprobed on Windows (the review's #55) and reset on a Mac, where a share is a <c>/Volumes</c> mount.</summary>
    [Fact]
    public void AShare_IsKeptOnWindows_WithoutAProbe_AndResetOnAMac()
    {
        bool probed = false;
        var windows = new FixupContext(Environment(directoryExists: _ => { probed = true; return false; }), SettingsArchive.WindowsOs, new Dictionary<string, string>());
        var (kept, none) = Apply("""{ "WorkingDirectory": "\\\\server\\share\\work", "ObsidianVault": "//server/vault" }""", windows);
        Assert.Equal(@"\\server\share\work", (string?)kept["WorkingDirectory"]);
        Assert.Equal("//server/vault", (string?)kept["ObsidianVault"]);
        Assert.Empty(none);
        Assert.False(probed);

        var (mac, reset) = Apply("""{ "WorkingDirectory": "\\\\server\\share\\work" }""", Context(SettingsArchive.MacOs, SettingsArchive.WindowsOs));
        Assert.Equal("", (string?)mac["WorkingDirectory"]);
        Assert.Contains("WorkingDirectory", reset);
    }

    /// <summary>A working folder inside another profile's folder here is reset (the review's #64); the profile's own stays.</summary>
    [Fact]
    public void AWorkingFolderInAnotherProfile_Resets()
    {
        string root = Path.Combine(Here, "home-" + Guid.NewGuid().ToString("N"), "profiles");
        string work = Path.Combine(root, "work", "files");
        var environment = Environment(directoryExists: _ => true);
        string json = new JsonObject { ["WorkingDirectory"] = work }.ToJsonString();

        var (moved, reset) = Apply(json, new FixupContext(environment, SettingsArchive.WindowsOs, new Dictionary<string, string>(), root, "work-imported"));
        Assert.Equal("", (string?)moved["WorkingDirectory"]);
        Assert.Equal(["WorkingDirectory"], reset);

        var (own, none) = Apply(json, new FixupContext(environment, SettingsArchive.WindowsOs, new Dictionary<string, string>(), root, "work"));
        Assert.Equal(work, (string?)own["WorkingDirectory"]);
        Assert.Empty(none);
    }

    [Fact]
    public void AQuotedClaudePath_IsTestedAsItsReaderReadsIt()
    {
        string exe = Path.Combine(Here, "claude.exe");
        var context = Context() with { Environment = Environment() with { FileExists = path => path == exe } };
        var (root, reset) = Apply(new JsonObject { ["ClaudeCodeExecutable"] = "\"" + exe + "\"" }.ToJsonString(), context);

        Assert.Equal("\"" + exe + "\"", (string?)root["ClaudeCodeExecutable"]);
        Assert.Empty(reset);
    }

    [Fact]
    public void AcrossSystems_TheShellPipeAndEditorsReset_AndOnTheSameSystemTheyStay()
    {
        const string json = """{ "ShellDefault": "cmd", "DockerEnginePipe": "my_pipe", "ImageEditor": "mspaint.exe", "DraftEditor": "notepad" }""";
        var (cross, crossReset) = Apply(json, Context(SettingsArchive.MacOs, SettingsArchive.WindowsOs));
        Assert.Equal("zsh", (string?)cross["ShellDefault"]);
        Assert.Equal(Docker.DockerPipe.DefaultName, (string?)cross["DockerEnginePipe"]);
        Assert.Equal("", (string?)cross["ImageEditor"]);
        Assert.Equal("", (string?)cross["DraftEditor"]);
        Assert.Equal(["ShellDefault", "DockerEnginePipe", "ImageEditor", "DraftEditor"], crossReset);

        var (same, sameReset) = Apply(json, Context());
        Assert.Equal("cmd", (string?)same["ShellDefault"]);
        Assert.Equal("notepad", (string?)same["DraftEditor"]);
        Assert.Empty(sameReset);
    }

    /// <summary>A shell the target always has stays (the review's #52); blank stays blank (#56); bash isn't always on Windows.</summary>
    [Theory]
    [InlineData(SettingsArchive.MacOs, SettingsArchive.WindowsOs, "bash", "bash")]
    [InlineData(SettingsArchive.MacOs, SettingsArchive.WindowsOs, "", "")]
    [InlineData(SettingsArchive.WindowsOs, SettingsArchive.MacOs, "zsh", "powershell")]
    [InlineData(SettingsArchive.WindowsOs, SettingsArchive.MacOs, "bash", "powershell")]
    [InlineData(SettingsArchive.WindowsOs, SettingsArchive.MacOs, "cmd", "cmd")]
    public void AcrossSystems_AShellTheTargetAlwaysHas_Stays(string thisOs, string sourceOs, string shell, string expected)
    {
        var (root, _) = Apply(new JsonObject { ["ShellDefault"] = shell, ["DockerEnginePipe"] = "" }.ToJsonString(), Context(thisOs, sourceOs));

        Assert.Equal(expected, (string?)root["ShellDefault"]);
        Assert.Equal("", (string?)root["DockerEnginePipe"]);
    }

    /// <summary>The forbidden list's system section swapped across systems (the review's #104), the user's own entries kept.</summary>
    [Fact]
    public void AcrossSystems_TheForbiddenList_TakesThisSystemsSection()
    {
        var saved = new JsonArray([.. ForbiddenStrings.SharedDefaults.Concat(ForbiddenStrings.WindowsDefaults).Append("my-own-tool --wipe").Select(s => (JsonNode?)JsonValue.Create(s))]);
        var (root, reset) = Apply(new JsonObject { ["ShellPoliceForbiddenStrings"] = saved }.ToJsonString(), Context(SettingsArchive.MacOs, SettingsArchive.WindowsOs));

        var list = root["ShellPoliceForbiddenStrings"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Contains("my-own-tool --wipe", list);
        Assert.All(ForbiddenStrings.MacDefaults, entry => Assert.True(ForbiddenStrings.Contains(list, entry), entry));
        Assert.All(ForbiddenStrings.SharedDefaults, entry => Assert.True(ForbiddenStrings.Contains(list, entry), entry));
        foreach (string windowsOnly in ForbiddenStrings.WindowsDefaults.Where(e => !ForbiddenStrings.Contains(ForbiddenStrings.SharedDefaults, e) && !ForbiddenStrings.Contains(ForbiddenStrings.MacDefaults, e)))
        {
            Assert.False(ForbiddenStrings.Contains(list, windowsOnly), windowsOnly);
        }

        Assert.Equal(ForbiddenStrings.Sorted(list), list);
        Assert.Equal(["ShellPoliceForbiddenStrings"], reset);
    }

    [Fact]
    public void TheForbiddenList_EmptyMissingOrSameSystem_IsLeftAlone()
    {
        var (empty, emptyReset) = Apply("""{ "ShellPoliceForbiddenStrings": [] }""", Context(SettingsArchive.MacOs, SettingsArchive.WindowsOs));
        Assert.Empty(empty["ShellPoliceForbiddenStrings"]!.AsArray());
        Assert.Empty(emptyReset);

        var (missing, missingReset) = Apply("{}", Context(SettingsArchive.MacOs, SettingsArchive.WindowsOs));
        Assert.False(missing.ContainsKey("ShellPoliceForbiddenStrings"));
        Assert.Empty(missingReset);

        var (same, sameReset) = Apply("""{ "ShellPoliceForbiddenStrings": ["x"] }""", Context());
        Assert.Equal(["x"], same["ShellPoliceForbiddenStrings"]!.AsArray().Select(n => (string)n!));
        Assert.Empty(sameReset);
    }

    /// <summary>This PC's monitor cabling goes silently (the review's #23); the input names stay; the file counts as changed.</summary>
    [Fact]
    public void TheMonitorCabling_GoesSilently_AndCountsAsAChange()
    {
        var root = JsonNode.Parse("""{ "MonitorPcInputs": { "a": 15 }, "MonitorInputsSeen": { "a": [15] }, "MonitorInputNames": { "a": "PC" } }""")!.AsObject();

        var result = SettingsImportFixups.Apply(root, Context());

        Assert.False(root.ContainsKey("MonitorPcInputs"));
        Assert.False(root.ContainsKey("MonitorInputsSeen"));
        Assert.True(root.ContainsKey("MonitorInputNames"));
        Assert.Empty(result.Reset);
        Assert.True(result.Changed);
    }

    [Fact]
    public void NothingToFix_IsNotAChange()
    {
        var result = SettingsImportFixups.Apply(JsonNode.Parse("""{ "TtsSpeed": 1.2 }""")!.AsObject(), Context());

        Assert.False(result.Changed);
        Assert.Empty(result.Reset);
    }

    /// <summary>Advisors follow the import's renames, as a correction, not a reset (the review's #38).</summary>
    [Fact]
    public void Advisors_FollowTheImportsRenames_AndLocalNamesStay()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["neon"] = "neon-imported" };
        var root = JsonNode.Parse("""{ "AdvisorList": [ { "Profile": "Neon", "Role": "r" }, { "Profile": "work", "Role": "" } ] }""")!.AsObject();

        var result = SettingsImportFixups.Apply(root, Context(renames: renames));

        var list = root["AdvisorList"]!.AsArray();
        Assert.Equal("neon-imported", (string?)list[0]!["Profile"]);
        Assert.Equal("work", (string?)list[1]!["Profile"]);
        Assert.Empty(result.Reset);
        Assert.True(result.Changed);
    }

    [Fact]
    public void KeysAreFoundIgnoringCase_AndUnknownKeysKept()
    {
        var (root, reset) = Apply("""{ "llmapikey": "dpapi:A", "Future": [1, 2] }""", Context());

        Assert.Equal("empty", (string?)root["llmapikey"]);
        Assert.NotNull(root["Future"]);
        Assert.Equal(["LlmApiKey"], reset);
    }

    /// <summary>Every machine fact a plain reset keeps is either fixed by the import or carried on purpose (the review's #31).</summary>
    [Fact]
    public void EveryResetKeptSetting_IsHandled_OrCarriedOnPurpose()
    {
        var known = SettingsImportFixups.Handled.Concat(SettingsImportFixups.CarriedOnPurpose).Concat(SettingsSecrets.Keys.Select(k => k.Key)).ToHashSet(StringComparer.Ordinal);

        Assert.All(Profiles.ResetKeptSettings, key => Assert.Contains(key, known));
    }

    /// <summary>The import's secrets are exactly what the loader encrypts, plus the Web UI token (the review's #13).</summary>
    [WindowsFact]
    public void TheSecretKeys_AreWhatProtectAtRestEncrypts_PlusTheWebUiToken()
    {
        // Every string setting given a plain value (one that doesn't read, an enum's, is put back), with no reflection: through the JSON.
        var plain = JsonSerializer.SerializeToNode(new AppSettingsData(), SettingsJsonContext.Default.AppSettingsData)!.AsObject();
        foreach (string key in plain.Where(p => p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String).Select(p => p.Key).ToList())
        {
            var was = plain[key]!.DeepClone();
            plain[key] = "plain-" + key;
            try
            {
                JsonSerializer.Deserialize(plain.ToJsonString(), SettingsJsonContext.Default.AppSettingsData);
            }
            catch (JsonException)
            {
                plain[key] = was;
            }
        }

        var filled = JsonSerializer.Deserialize(plain.ToJsonString(), SettingsJsonContext.Default.AppSettingsData)!;
        SettingsSecrets.ProtectAtRest(filled);
        var after = JsonSerializer.SerializeToNode(filled, SettingsJsonContext.Default.AppSettingsData)!.AsObject();

        var encrypted = after.Where(p => p.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String && Sql.WindowsCredentials.IsProtected(v.GetValue<string>()))
            .Select(p => p.Key).Order(StringComparer.Ordinal);
        var expected = SettingsSecrets.Keys.Select(k => k.Key).Append(nameof(AppSettingsData.WebUiToken)).Order(StringComparer.Ordinal);
        Assert.Equal(expected, encrypted);
    }
}
