using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/usage context</c> (2026-11-01, feature idea #1): the pane opens on the Context tab; without a pane its lines print alone, while a bare
/// <c>/usage</c> prints them after the Statistics lines; a group switched off leaves the tab and the estimate falls by its tools; any other
/// word is the usage error; typing <c>/usage </c> offers <c>context</c>.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The <c>/usage</c> pane's title row: its label, then the two tabs (one tab, the label alone, until the Context tab on 2026-11-01).</summary>
    private static string UsagePaneTitle => UsageText.Label + " │ " + UsageText.StatisticsTabTitle + " · " + UsageText.ContextTabTitle + " ";

    [Fact]
    public async Task WithGeometry_UsageContext_OpensOnTheContextTab()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 80;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/usage context");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.PromptLabel, output, StringComparison.Ordinal);
        Assert.Contains(UsageText.NothingSentYet, output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);   // the Statistics tab is not the one shown
    }

    [Fact]
    public async Task UsageContext_WithoutThePane_PrintsTheContextLinesAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage context");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Context: ≈", output, StringComparison.Ordinal);
        Assert.Contains("  · system prompt: ≈", output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_WithoutThePane_PrintsTheStatistics_ThenTheContextLines()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage");
        PushLine("/exit");

        string output = await RunAsync();

        int statistics = output.IndexOf(UsageText.NothingCounted, StringComparison.Ordinal);
        int context = output.IndexOf("  · system prompt: ≈", StringComparison.Ordinal);
        Assert.True(statistics >= 0 && context > statistics);
    }

    [Fact]
    public async Task UsageContext_AGroupSwitchedOff_LeavesTheTab_AndTheEstimateFallsByItsTools()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage context");
        PushLine("/memory off");
        PushLine("/usage context");
        PushLine("/exit");

        string output = await RunAsync();

        var totals = Regex.Matches(output, @"  · Context: ≈([\d,]+) tokens").Select(m => long.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(2, totals.Count);
        var memory = Regex.Match(output, @"  ·   Memory: ≈(\d+)");
        Assert.True(memory.Success);
        Assert.Single(Regex.Matches(output, @"  ·   Memory: "));   // gone the second time
        Assert.True(totals[1] <= totals[0] - long.Parse(memory.Groups[1].Value, CultureInfo.InvariantCulture));   // its tools, and its prompt section too
    }

    [Fact]
    public async Task Usage_AnyOtherWord_IsTheUsageError()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage reset");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(UsageText.UsageError, output, StringComparison.Ordinal);
        Assert.Equal("/usage takes context, or nothing for the pane.", UsageText.UsageError);
    }

    [Fact]
    public void Usage_ArgumentList_OffersContext()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false));

        Assert.Equal(new CompletionItem(UsageText.ContextWord, UsageText.ContextWordNote), Assert.Single(ChatScreen.ArgumentItems("/usage", "", sources)));
        Assert.Single(ChatScreen.ArgumentItems("/usage", "co", sources));
        Assert.Empty(ChatScreen.ArgumentItems("/usage", "x", sources));
    }
}
