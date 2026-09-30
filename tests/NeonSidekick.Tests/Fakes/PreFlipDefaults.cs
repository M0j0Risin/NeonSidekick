using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The eight settings that went off by default on 2026-09-29 (the user's call: the file, web, Obsidian, SQL, ComfyUI and
/// Home Assistant tools, <c>Shell command policy</c> <c>off</c> and <c>LLM server scan mode</c> <c>disabled</c>), put back as they
/// were — for the fixtures whose scripts were written against the old defaults (every tool group offered, <c>ask</c>, the
/// <c>local</c> scan finding the stub on port 1234). The fresh-profile picture is pinned by <c>AppSettingsTests.Defaults</c>
/// and the tests that start from <c>new AppSettingsData()</c> on purpose.
/// </summary>
internal static class PreFlipDefaults
{
    /// <summary>Puts the eight back on <paramref name="data"/>.</summary>
    public static void Apply(AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.FileTools = true;
        data.WebTools = true;
        data.ObsidianTools = true;
        data.SqlTools = true;
        data.ComfyTools = true;
        data.HomeAssistantTools = true;
        data.ShellCommandPolicy = "ask";
        data.LlmScanMode = "local";
    }

    /// <summary>A fresh profile's settings with the eight put back.</summary>
    public static AppSettingsData Data()
    {
        var data = new AppSettingsData();
        Apply(data);
        return data;
    }
}
