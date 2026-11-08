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
    /// <summary>
    /// The <c>/usage</c> pane's title row: its label, then the five tabs (one tab, the label alone, until the Context tab on 2026-11-01; two until
    /// /sys' Prompt and Tools tabs and the new Skills tab came on 2026-11-07).
    /// </summary>
    private static string UsagePaneTitle => UsageText.Label + " │ " + UsageText.StatisticsTabTitle + " · " + UsageText.ContextTabTitle + " · "
        + SystemPromptSummary.PromptTabTitle + " · " + SystemPromptSummary.ToolsTabTitle + " · " + SystemPromptSummary.SkillsTabTitle + " ";

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

        Assert.Contains("  · Context: ~", output, StringComparison.Ordinal);
        Assert.Contains("  · system prompt: ~", output, StringComparison.Ordinal);
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
        int context = output.IndexOf("  · system prompt: ~", StringComparison.Ordinal);
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

        var totals = Regex.Matches(output, @"  · Context: ~([\d,]+) tokens").Select(m => long.Parse(m.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(2, totals.Count);
        var memory = Regex.Match(output, @"  ·   Memory: ~(\d+)");
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
    }

    /// <summary>
    /// <c>/usage prompt</c> without a pane (2026-11-08, the review: each word on its own, so a wrong tab or a fall-through to the
    /// Statistics lines would fail): the Prompt lines alone, no tool and no skills heading.
    /// </summary>
    [Fact]
    public async Task Usage_WithoutThePane_Prompt_PrintsThePromptAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage prompt");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Persona — default", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Files (", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Skills — ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);
    }

    /// <summary><c>/usage tools</c> without a pane (2026-11-08, the review): the tool lines alone.</summary>
    [Fact]
    public async Task Usage_WithoutThePane_Tools_PrintsTheToolsAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage tools");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Files (19)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Persona — ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Skills — ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);
    }

    /// <summary><c>/usage skills</c> without a pane (2026-11-08, the review: no test typed it): the heading, the block's directive and the line, nothing else.</summary>
    [Fact]
    public async Task Usage_WithoutThePane_Skills_PrintsTheSkillsAlone()
    {
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/usage skills");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Skills — on, none installed", output, StringComparison.Ordinal);
        Assert.Contains("  ·   " + Skills.SkillsPrompt.DirectiveWithoutSkills[..60], output, StringComparison.Ordinal);
        Assert.Contains("  ·   " + SystemPromptSummary.NoSkillsLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Persona — ", output, StringComparison.Ordinal);
        Assert.DoesNotContain("  · Files (", output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);
    }

    /// <summary><c>/usage skills</c> with the pane (2026-11-08, the review): it opens on the Skills tab, not the Prompt tab.</summary>
    [Fact]
    public async Task Usage_WithGeometry_Skills_OpensOnTheSkillsTab()
    {
        _settings.Update(d => { d.TtsOutput = false; d.MenuMaxHeight = "full"; });
        _console.Profile.Height = 80;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/usage skills");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(HeadingRow("── Skills ── on, none installed"), output, StringComparison.Ordinal);
        Assert.DoesNotContain(HeadingRow("── Persona ── default"), output, StringComparison.Ordinal);
        Assert.DoesNotContain(UsageText.NothingCounted, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_ArgumentList_OffersContext()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false));

        Assert.Equal(
        [
            new CompletionItem(UsageText.ContextWord, UsageText.ContextWordNote),
            new CompletionItem(UsageText.PromptWord, UsageText.PromptWordNote),   // the three from /sys in tab order (2026-11-07)
            new CompletionItem(UsageText.ToolsWord, UsageText.ToolsWordNote),
            new CompletionItem(UsageText.SkillsWord, UsageText.SkillsWordNote),
        ], ChatScreen.ArgumentItems("/usage", "", sources));
        Assert.Single(ChatScreen.ArgumentItems("/usage", "co", sources));
        Assert.Equal(UsageText.SkillsWord, Assert.Single(ChatScreen.ArgumentItems("/usage", "s", sources)).Text);
        Assert.Empty(ChatScreen.ArgumentItems("/usage", "x", sources));
    }
}
