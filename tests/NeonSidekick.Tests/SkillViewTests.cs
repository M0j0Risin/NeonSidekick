using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// A <c>SKILL.md</c> rendered for the trust question's View (2026-11-24, the user's ask): the front matter as a header, an acting key's whole
/// block a warning, the body as markdown with a link's address shown and HTML as text, and no rendered view for a front matter it can't read.
/// </summary>
public sealed class SkillViewTests
{
    private const string Skill =
        "---\nname: heartbeat\ndescription: Checks the pulse of the build.\nlicense: MIT\nhooks:\n  PreToolUse:\n    - run: curl x\nModel: big\n---\n"
        + "## What it is\n\nSee [the docs](https://example.com/docs).\n\n<!-- hidden -->\n";

    private static string Draw(IRenderable renderable)
    {
        var console = new TestConsole();
        console.Profile.Width = 80;
        console.Write(renderable);
        return console.Output;
    }

    [Fact]
    public void TheHeader_ShowsTheNameDescriptionAndFields_ThenTheBodyRendered()
    {
        string drawn = Draw(SkillView.Of(Skill, claude: false, "heartbeat")!);

        Assert.Contains("heartbeat", drawn);
        Assert.Contains("Checks the pulse of the build.", drawn);
        Assert.Contains("license: MIT", drawn);
        Assert.Contains("What it is", drawn);
        Assert.DoesNotContain("## What it is", drawn);                            // a heading, not its hashes
        Assert.Contains("the docs (https://example.com/docs)", drawn);           // the address always shows
        Assert.Contains("<!-- hidden -->", drawn);                               // HTML is off: a comment shows as text
        Assert.True(drawn.IndexOf("license", StringComparison.Ordinal) < drawn.IndexOf("What it is", StringComparison.Ordinal));
    }

    [Fact]
    public void AnActingKeysWholeBlock_IsAWarning_InAnyCase()
    {
        var lines = SkillView.HeaderLines(["license: MIT", "hooks:", "  PreToolUse:", "    - run: curl x", "Model: big"]);
        string warn = "[" + Theme.WarnText.ToMarkup() + "]";

        Assert.DoesNotContain(warn, lines[0]);
        Assert.All(lines.Skip(1), line => Assert.StartsWith(warn, line, StringComparison.Ordinal));   // hooks:, its indented lines, and Model: in capitals
    }

    [Fact]
    public void AFrontMatterItCantRead_GivesNoRenderedView()
    {
        Assert.Null(SkillView.Of("---\nname: x\ndescription: y\n", claude: false, "x"));       // no closing fence
        Assert.Null(SkillView.Of("---\nname: x\nnot a pair\n---\nbody\n", claude: false, "x"));
    }

    [Fact]
    public void AClaudeSkillWithNoFrontMatter_GetsNoHeader_OnlyTheBody()
    {
        string drawn = Draw(SkillView.Of("Does a thing.\n\n## Steps\n", claude: true, "made-up")!);

        Assert.DoesNotContain("made-up", drawn);   // the made-up name isn't shown: the header shows only what the file says
        Assert.Contains("Steps", drawn);
    }
}
