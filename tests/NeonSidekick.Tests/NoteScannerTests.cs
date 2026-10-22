using NeonSidekick.Obsidian;

namespace NeonSidekick.Tests;

/// <summary>The Obsidian-flavoured Markdown the vault tools read (2026-09-22): every link form, tags, headings, block ids, and what is skipped.</summary>
public sealed class NoteScannerTests
{
    private static NoteScan Scan(params string[] lines) => NoteScanner.Scan(lines);

    [Fact]
    public void Wikilinks_Embeds_Subpaths_AndAliases()
    {
        var scan = Scan("See [[Plan]], [[Projects/Roadmap#Q3|the roadmap]] and ![[diagram.png]] and [[Plan#^abc123]].");
        Assert.Collection(scan.Links,
            l => { Assert.Equal(NoteLinkKind.Wiki, l.Kind); Assert.Equal("Plan", l.Target); Assert.Equal("", l.Subpath); Assert.Null(l.Alias); Assert.Equal("[[Plan]]", l.Raw); Assert.Equal(4, l.Column); Assert.Equal(8, l.Length); },
            l => { Assert.Equal("Projects/Roadmap", l.Target); Assert.Equal("#Q3", l.Subpath); Assert.Equal("the roadmap", l.Alias); },
            l => { Assert.Equal(NoteLinkKind.Embed, l.Kind); Assert.Equal("diagram.png", l.Target); Assert.Equal("![[diagram.png]]", l.Raw); Assert.True(l.IsEmbed); },
            l => { Assert.Equal("Plan", l.Target); Assert.Equal("#^abc123", l.Subpath); });
    }

    [Fact]
    public void MarkdownLinks_AreDecoded_AndUrlsWithSchemesAreNot_Links()
    {
        var scan = Scan("[the plan](Projects/My%20Plan.md#Goals) [web](https://example.com) [mail](mailto:a@b.c) [top](#Heading) ![pic](img/a.png)");
        Assert.Collection(scan.Links,
            l => { Assert.Equal(NoteLinkKind.Markdown, l.Kind); Assert.Equal("Projects/My Plan.md", l.Target); Assert.Equal("#Goals", l.Subpath); Assert.Equal("the plan", l.Alias); },
            l => { Assert.Equal(NoteLinkKind.MarkdownEmbed, l.Kind); Assert.Equal("img/a.png", l.Target); });
    }

    [Fact]
    public void CodeFences_CodeSpans_Comments_AndFrontmatter_AreSkipped()
    {
        var scan = Scan(
            "---",
            "tags: [x]",
            "link: \"[[NotALink]]\"",
            "---",
            "```",
            "[[InFence]] #infence",
            "```",
            "`[[InSpan]]` %%[[InComment]]%% [[Real]]",
            "%% open",
            "[[StillComment]]",
            "closed %% #after");
        // A property's wikilink is a link (2026-10-22, review O4: Obsidian reads and renames it), marked as one; nothing else there is.
        Assert.Equal(["NotALink", "Real"], scan.Links.Select(l => l.Target));
        Assert.Equal([true, false], scan.Links.Select(l => l.InProperties));
        Assert.Equal(["after"], scan.Tags.Select(t => t.Name));
        Assert.Equal(5, scan.BodyLine);
    }

    [Fact]
    public void Tags_FollowObsidiansRules()
    {
        var scan = Scan("#project and #project/alpha, not#this, not #123, but #y2026 and #under_score-dash. url http://x.com/#frag");
        Assert.Equal(["project", "project/alpha", "y2026", "under_score-dash"], scan.Tags.Select(t => t.Name));
    }

    [Fact]
    public void Headings_AndBlockIds()
    {
        var scan = Scan("# Title", "text", "## Goals ##", "#NotAHeading", "a line ^block-1", "####### seven");
        Assert.Equal([(1, "Title", 1), (2, "Goals", 3)], scan.Headings.Select(h => (h.Level, h.Text, h.Line)));
        Assert.Equal(["block-1"], scan.BlockIds);
        Assert.Equal(["NotAHeading"], scan.Tags.Select(t => t.Name));
    }

    [Fact]
    public void ATablesEscapedPipe_IsNotPartOfTheTarget()
    {
        var link = Assert.Single(Scan("| x | [[Plan#Goals\\|goals]] |").Links);
        Assert.Equal("Plan", link.Target);
        Assert.Equal("#Goals", link.Subpath);
        Assert.Equal("goals", link.Alias);
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("board.canvas", true)]
    [InlineData("2026.09.22", false)]
    [InlineData("v1.2 plan", false)]
    [InlineData("Folder.v2/Note", false)]
    [InlineData(".hidden", false)]
    public void HasFileExtension_TellsATypeFromADottedName(string path, bool expected) =>
        Assert.Equal(expected, VaultPaths.HasFileExtension(path));

    [Fact]
    public void Encode_AndDecode_RoundTripAPathWithSpaces()
    {
        Assert.Equal("My%20Notes/Plan%20%281%29.md", NoteScanner.Encode("My Notes/Plan (1).md"));
        Assert.Equal("My Notes/Plan (1).md", NoteScanner.Decode("My%20Notes/Plan%20%281%29.md"));
        Assert.Equal("100%bad", NoteScanner.Decode("100%bad"));
    }
}
