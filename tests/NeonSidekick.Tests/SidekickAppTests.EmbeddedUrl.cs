using System.Net;
using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// An embedded URL from <c>--url</c> or <c>NEONSIDEKICK_LLM_URL</c> switches <c>Embedded servers enabled</c> on for the run (2026-10-21, the
/// user's call: the switch is off by default since, and <c>--url embedded</c> alone still runs the embedded model).
/// </summary>
public partial class SidekickAppTests
{
    [Fact]
    public void EmbeddedByUrl_AnEmbeddedOverride_SwitchesItOn_NothingElseDoes()
    {
        Assert.True(SidekickApp.EmbeddedByUrl(new AppSettingsData(), "embedded").EmbeddedLlmServer);
        Assert.True(SidekickApp.EmbeddedByUrl(new AppSettingsData(), EmbeddedEndpoint.BaseUrl.ToString()).EmbeddedLlmServer);
        Assert.False(SidekickApp.EmbeddedByUrl(new AppSettingsData(), "http://127.0.0.1:1234/v1").EmbeddedLlmServer);
        Assert.False(SidekickApp.EmbeddedByUrl(new AppSettingsData(), null).EmbeddedLlmServer);
        // A saved embedded URL alone changes nothing: the rule reads the override, never the saved value.
        Assert.False(SidekickApp.EmbeddedByUrl(new AppSettingsData { LlmUrl = "embedded" }, null).EmbeddedLlmServer);
    }

    [Fact]
    public void TheVariable_NamingTheEmbeddedModel_HoldsTheSwitchOn_AndSaysSo()
    {
        _settings.Update(d => d.EmbeddedLlmServer = false);
        var app = App(new EnvironmentOverrides(n => n == EnvironmentOverrides.LlmUrlVariable ? "embedded" : null));

        Assert.True(app.EffectiveSettings.EmbeddedLlmServer);
        Assert.False(_settings.Current.EmbeddedLlmServer);   // never saved
        Assert.Equal(EnvironmentOverrides.LlmUrlVariable, app.OverriddenBy(SettingsField.EmbeddedLlmServer));
    }

    [Fact]
    public void AnotherUrl_LeavesTheSwitchAsSaved_AndNamesNoOverride()
    {
        _settings.Update(d => d.EmbeddedLlmServer = false);
        var app = App(new EnvironmentOverrides(n => n == EnvironmentOverrides.LlmUrlVariable ? "http://127.0.0.1:1234/v1" : null));

        Assert.False(app.EffectiveSettings.EmbeddedLlmServer);
        Assert.Null(app.OverriddenBy(SettingsField.EmbeddedLlmServer));
    }

    [Fact]
    public async Task Headless_UrlEmbedded_OnAFreshProfile_RunsTheInstalledModel()
    {
        _settings.Update(d => d.EmbeddedLlmServer = false);   // the fresh default, under PreFlipDefaults' old one
        var embedded = new FakeEmbeddedLlm().Installed("gemma-4-e2b");
        _http.Map("http://127.0.0.1:59999/props", HttpStatusCode.OK, "{\"default_generation_settings\":{\"n_ctx\":32768}}");

        string output = await Headless("", options: SidekickOptions.Parse(["--url", "embedded"]), embeddedLlm: () => embedded);

        Assert.Contains("LLM: " + EmbeddedEndpoint.BaseUrl, output);
        Assert.Equal(["gemma-4-e2b"], embedded.Starts);
        Assert.False(_settings.Current.EmbeddedLlmServer);   // the run's, never saved
    }
}
