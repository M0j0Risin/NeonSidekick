using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The settings whose defaults moved under scripts written before, put back as they were — for the fixtures whose scripts were
/// written against the old defaults. The eight that went off by default on 2026-09-29 (the user's call: the file, web, Obsidian,
/// SQL, ComfyUI and Home Assistant tools, <c>Shell command policy</c> <c>off</c> and <c>LLM server scan mode</c> <c>disabled</c>;
/// the scripts offer every tool group, <c>ask</c>, the <c>local</c> scan finding the stub on port 1234), then two on 2026-10-21
/// (the user's calls): <c>Embedded servers enabled</c> off (the embedded scripts were written with it on) and <c>Plan gap max
/// passes</c> 50 (the gap scripts' messages say "of 5" and run to that limit). The fresh-profile picture is pinned by
/// <c>AppSettingsTests.Defaults</c> and the tests that start from <c>new AppSettingsData()</c> on purpose.
/// </summary>
internal static class PreFlipDefaults
{
    /// <summary>Puts the ten back on <paramref name="data"/>.</summary>
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
        data.EmbeddedLlmServer = true;
        data.PlanGapPasses = 5;
    }

    /// <summary>A fresh profile's settings with the ten put back.</summary>
    public static AppSettingsData Data()
    {
        var data = new AppSettingsData();
        Apply(data);
        return data;
    }
}
