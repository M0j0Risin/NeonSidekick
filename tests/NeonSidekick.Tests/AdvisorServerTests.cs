using NeonSidekick.Advisors;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>An advisor's server kind and model as the lists name them (2026-10-10, <see cref="AdvisorServer"/>), and its line.</summary>
public class AdvisorServerTests
{
    [Theory]
    [InlineData("http://claude-code.localhost/v1", AdvisorServer.ClaudeCode)]
    [InlineData("claude-code", AdvisorServer.ClaudeCode)]
    [InlineData("http://embedded.localhost/v1", AdvisorServer.Embedded)]
    [InlineData("embedded", AdvisorServer.Embedded)]
    [InlineData("http://docker.localhost/vllm/v1", AdvisorServer.Docker)]
    [InlineData("docker:vllm", AdvisorServer.Docker)]
    [InlineData("https://api.anthropic.com/v1", AdvisorServer.Anthropic)]
    [InlineData("https://api.openai.com/v1", AdvisorServer.OpenAI)]
    [InlineData("http://192.168.1.40:8000/v1", AdvisorServer.Http)]
    [InlineData("http://localhost:30000/v1", AdvisorServer.Http)]
    [InlineData("", AdvisorServer.Scan)]
    [InlineData("  ", AdvisorServer.Scan)]
    [InlineData(null, AdvisorServer.Scan)]
    public void TheKind_IsReadFromTheSavedUrl(string? url, string kind) => Assert.Equal(kind, AdvisorServer.Kind(url));

    [Fact]
    public void TheWords_AreTheOnesTheUserListed()
    {
        Assert.Equal(["claudecode", "embedded", "docker", "anthropic", "openai", "http"],
            [AdvisorServer.ClaudeCode, AdvisorServer.Embedded, AdvisorServer.Docker, AdvisorServer.Anthropic, AdvisorServer.OpenAI, AdvisorServer.Http]);
    }

    [Fact]
    public void TheModel_IsTheSavedOne_ABlankOneSonnetOnClaudeCode_ElseTheServersDefault()
    {
        Assert.Equal("Qwen3-32B", AdvisorServer.Model(new AppSettingsData { LlmUrl = "http://h:8000/v1", LlmModel = " Qwen3-32B " }));
        Assert.Equal("sonnet", AdvisorServer.Model(new AppSettingsData { LlmUrl = "claude-code" }));
        Assert.Equal("opus", AdvisorServer.Model(new AppSettingsData { LlmUrl = "claude-code", LlmModel = "opus" }));
        Assert.Equal(AdvisorServer.DefaultModel, AdvisorServer.Model(new AppSettingsData { LlmUrl = "http://h:8000/v1" }));
        Assert.Equal(new AdvisorServerFacts("docker", "m"), AdvisorServer.Of(new AppSettingsData { LlmUrl = "docker:vllm", LlmModel = "m" }));
    }

    [Fact]
    public void Read_FindsTheProfileOnDisk_AndIsNullForAMissingOrUnreadableOne()
    {
        string home = Path.Combine(Path.GetTempPath(), "ns-advisor-server-" + Guid.NewGuid().ToString("N"));
        try
        {
            Profiles.Create(home, "doug", new AppSettingsData { LlmUrl = "docker:vllm", LlmModel = "Qwen3.8-27B" });
            Profiles.Create(home, "broken", new AppSettingsData());
            File.WriteAllText(Profiles.ProfileFile(home, "broken"), "{ not json");

            Assert.Equal(new AdvisorServerFacts("docker", "Qwen3.8-27B"), AdvisorServer.Read(home, "DOUG"));
            Assert.Null(AdvisorServer.Read(home, "gone"));
            Assert.Null(AdvisorServer.Read(home, "broken"));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void TheLine_NamesTheRoleTheKindAndTheModel_OrSaysWhyNot()
    {
        var facts = new AdvisorServerFacts("docker", "Qwen3.8-27B-NVFP4-RTX5090");
        Assert.Equal("doug — expert .NET solution architect · docker · Qwen3.8-27B-NVFP4-RTX5090",
            AdvisorText.Detail(new AdvisorEntry("doug", "expert .NET solution architect"), facts, exists: true));
        Assert.Equal("qa · docker · Qwen3.8-27B-NVFP4-RTX5090", AdvisorText.Detail(new AdvisorEntry("qa"), facts, exists: true));
        Assert.Equal("gone — critic  " + AdvisorText.MissingMark, AdvisorText.Detail(new AdvisorEntry("gone", "critic"), null, exists: false));
        Assert.Equal("bad — critic · " + AdvisorText.UnreadableMark, AdvisorText.Detail(new AdvisorEntry("bad", "critic"), null, exists: true));
        Assert.Equal("  " + AdvisorText.Detail(new AdvisorEntry("qa"), facts, true), AdvisorText.ListLine(new AdvisorEntry("qa"), facts, true));
    }

    /// <summary>The model the link answers on wins where the session knows it, by its short name (2026-11-22, the user's screenshots).</summary>
    [Fact]
    public void Of_NamesTheLinkedModel_WhereKnown_ByItsShortName()
    {
        var docker = new AppSettingsData { LlmUrl = "docker:vllm" };
        Assert.Equal(new AdvisorServerFacts("docker", AdvisorServer.DefaultModel), AdvisorServer.Of(docker));
        Assert.Equal(new AdvisorServerFacts("docker", AdvisorServer.DefaultModel), AdvisorServer.Of(docker, _ => null));
        Assert.Equal(new AdvisorServerFacts("docker", "Qwen3.8-27B-NVFP4-RTX5090"), AdvisorServer.Of(docker, _ => "gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090"));
        var saved = new AppSettingsData { LlmUrl = "http://h:8000/v1", LlmModel = "gittensor-model-hub/Qwen3.8-27B-NVFP4-RTX5090" };
        Assert.Equal("Qwen3.8-27B-NVFP4-RTX5090", AdvisorServer.Of(saved).Model);   // the saved one, shortened too
        Assert.Equal("gemma.gguf", AdvisorServer.Of(saved, _ => @"C:\models\gemma.gguf").Model);
        Assert.Equal("opus", AdvisorServer.Of(new AppSettingsData { LlmUrl = "claude-code", LlmModel = "opus" }, _ => "opus").Model);
    }

    [Fact]
    public void ModelNames_Short_IsTheLastPathSegment_AsTheStatusBarShowsIt()
    {
        Assert.Equal("Qwen3-30B", ModelNames.Short("lyf/Qwen3-30B"));
        Assert.Equal("m.gguf", ModelNames.Short(@"D:\models\m.gguf"));
        Assert.Equal("sonnet", ModelNames.Short("sonnet"));
        Assert.Equal("odd/", ModelNames.Short("odd/"));
        Assert.Equal("", ModelNames.Short(null));
        Assert.Equal(ModelNames.Short("a/b"), App.ChatScreen.ModelLabel("a/b"));
    }

    /// <summary>The lines in columns (2026-11-22, the user's ask): names, roles and kinds padded in cells, descriptions in one column.</summary>
    [Fact]
    public void TheLines_InColumns_PadInCells_AndLineUpTheDescriptions()
    {
        var dba = (new AdvisorEntry("dba", "database administrator", "When a query is slow."), (AdvisorServerFacts?)new AdvisorServerFacts("claudecode", "opus"), true);
        var architect = (new AdvisorEntry("architect", "software architect", "Before a big change."), (AdvisorServerFacts?)new AdvisorServerFacts("docker", "Qwen3.8-27B"), true);
        var qa = (new AdvisorEntry("qa"), (AdvisorServerFacts?)new AdvisorServerFacts("http", "m"), true);
        var gone = (new AdvisorEntry("gone", "critic"), (AdvisorServerFacts?)null, false);
        var bad = (new AdvisorEntry("bad", "critic"), (AdvisorServerFacts?)null, true);

        Assert.Equal(
        [
            "  dba       — database administrator · claudecode · opus        · When a query is slow.",
            "  architect — software architect     · docker     · Qwen3.8-27B · Before a big change.",
            "  qa                                 · http       · m",
            "  gone      — critic                   " + AdvisorText.MissingMark,
            "  bad       — critic                 · " + AdvisorText.UnreadableMark,
        ], AdvisorText.ListLines([dba, architect, qa, gone, bad]));
        Assert.Equal(new AdvisorColumns(9, 22, 10, 11), AdvisorText.Columns([dba, architect, qa, gone, bad]));

        // No roles at all: no role column. A role past the rows' cap is left whole here.
        Assert.Equal(["  qa · http · m"], AdvisorText.ListLines([qa]));
        string longRole = new('r', SettingsMenuRoleCap + 8);
        Assert.Contains(longRole, AdvisorText.ListLines([(new AdvisorEntry("x", longRole), (AdvisorServerFacts?)new AdvisorServerFacts("http", "m"), true)])[0]);

        // A two-cell emoji in a role pads by cells, so the kinds still start in one column.
        var emoji = (new AdvisorEntry("ux", "🎨 designer"), (AdvisorServerFacts?)new AdvisorServerFacts("http", "m"), true);
        var plain = (new AdvisorEntry("ux2", "a designer"), (AdvisorServerFacts?)new AdvisorServerFacts("http", "m"), true);
        var lines = AdvisorText.ListLines([emoji, plain]);
        Assert.Equal(TextCells.Width(lines[0][..lines[0].IndexOf(" · ", StringComparison.Ordinal)]), TextCells.Width(lines[1][..lines[1].IndexOf(" · ", StringComparison.Ordinal)]));
    }

    private const int SettingsMenuRoleCap = App.SettingsMenu.AdvisorRoleWidth;

    [Fact]
    public void AKeptLinesFooter_HasALineARow_CutToTheWidth_AndSaysHowManyMore()
    {
        Assert.Equal(["one", "two two…", "Default…"], MenuPane.FooterLines(new MenuFooter("one\ntwo two two", "Default: x") { KeepLines = true }, 8));
        Assert.Equal(["1", "2", "3", "+3 more", "D: x"], MenuPane.FooterLines(new MenuFooter("1\n2\n3\n4\n5\n6", "D: x") { KeepLines = true }, 8));
        Assert.Equal(["1", "2", "3", "4", "5"], MenuPane.FooterLines(new MenuFooter("1\r\n2\n3\n4\n5") { KeepLines = true }, 8));
        Assert.Equal(["one two two"], MenuPane.FooterLines(new MenuFooter("one\ntwo two"), 20));   // a plain footer still reads a break as a space
        Assert.Equal("+2 more", MenuPane.MoreLines(2));
    }
}
