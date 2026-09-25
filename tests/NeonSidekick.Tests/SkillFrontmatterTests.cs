using NeonSidekick.Skills;

namespace NeonSidekick.Tests;

public class SkillFrontmatterTests
{
    private static SkillFrontmatter Parse(string text, out string body)
    {
        Assert.True(SkillFrontmatter.TryParse(text, out var frontmatter, out body, out var problem), problem);
        return frontmatter!;
    }

    [Fact]
    public void MinimalFile_NameDescriptionAndBody()
    {
        var fm = Parse("---\nname: pdf-processing\ndescription: Extract PDF text, fill forms, merge files. Use when handling PDFs.\n---\n\n# PDF Processing\n\nSteps here.\n", out string body);

        Assert.Equal("pdf-processing", fm.Name);
        Assert.Equal("Extract PDF text, fill forms, merge files. Use when handling PDFs.", fm.Description);
        Assert.Empty(fm.OtherLines);
        Assert.Equal("# PDF Processing\n\nSteps here.", body);
    }

    [Fact]
    public void OptionalFields_AreCarriedThroughAsLines_TheMetadataMapIncluded()
    {
        var fm = Parse("---\nname: x\ndescription: Does x.\nlicense: Apache-2.0\nmetadata:\n  author: example-org\n  version: \"1.0\"\nallowed-tools: Bash(git:*) Read\n---\nBody.", out string body);

        Assert.Equal(["license: Apache-2.0", "metadata:", "  author: example-org", "  version: \"1.0\"", "allowed-tools: Bash(git:*) Read"], fm.OtherLines);
        Assert.Equal("Body.", body);
    }

    [Theory]
    [InlineData("description: \"Use when: the user asks, \\\"please\\\".\"", "Use when: the user asks, \"please\".")]
    [InlineData("description: 'It''s for PDFs.'", "It's for PDFs.")]
    [InlineData("description: Use this skill when: the user asks about PDFs", "Use this skill when: the user asks about PDFs")]
    [InlineData("description: Does x. # a comment", "Does x.")]
    [InlineData("description: >\n  Folded over\n  two lines.", "Folded over two lines.")]
    [InlineData("description: |\n  Literal over\n  two lines.", "Literal over two lines.")]
    [InlineData("description: >-\n  Stripped.\n", "Stripped.")]
    [InlineData("description:   spaced   out  ", "spaced out")]
    public void Description_Scalars_AreReadAndFlattened(string line, string expected)
    {
        var fm = Parse("---\nname: x\n" + line + "\n---\nb", out _);
        Assert.Equal(expected, fm.Description);
    }

    [Fact]
    public void CrlfAndBom_AreFolded_AndCommentsAndBlankLinesSkipped()
    {
        var fm = Parse("﻿---\r\n# the head\r\n\r\nname: x\r\ndescription: Does x.\r\n---\r\nline one\r\nline two\r\n", out string body);

        Assert.Equal("x", fm.Name);
        Assert.Equal("line one\nline two", body);
    }

    [Theory]
    [InlineData("name: x\ndescription: y\n---\nb", SkillFrontmatter.NoFenceProblem)]
    [InlineData("", SkillFrontmatter.NoFenceProblem)]
    [InlineData("---\nname: x\ndescription: y\nb", SkillFrontmatter.NoClosingFenceProblem)]
    [InlineData("---\ndescription: y\n---\nb", SkillFrontmatter.NoNameProblem)]
    [InlineData("---\nname: x\n---\nb", SkillFrontmatter.NoDescriptionProblem)]
    [InlineData("---\nname: x\ndescription:\n---\nb", SkillFrontmatter.NoDescriptionProblem)]
    [InlineData("---\nname: x\njust words\ndescription: y\n---\nb", "frontmatter line 3 is not a key: value pair")]
    [InlineData("---\nname:x\ndescription: y\n---\nb", "frontmatter line 2 is not a key: value pair")]
    [InlineData("---\n  indented: first\nname: x\ndescription: y\n---\nb", "frontmatter line 2 is not a key: value pair")]
    public void Problems_ArePinned(string text, string expected)
    {
        Assert.False(SkillFrontmatter.TryParse(text, out var frontmatter, out _, out var problem));
        Assert.Null(frontmatter);
        Assert.Equal(expected, problem);
    }

    [Fact]
    public void ADotsLine_ClosesTheFrontmatterToo_AndTheLastDuplicateKeyWins()
    {
        var fm = Parse("---\nname: a\nname: b\ndescription: y\n...\nbody", out string body);
        Assert.Equal("b", fm.Name);
        Assert.Equal("body", body);
    }

    [Theory]
    [InlineData("pdf-processing", true)]
    [InlineData("data-analysis", true)]
    [InlineData("a", true)]
    [InlineData("x1", true)]
    [InlineData("PDF-Processing", false)]
    [InlineData("-pdf", false)]
    [InlineData("pdf-", false)]
    [InlineData("pdf--processing", false)]
    [InlineData("pdf processing", false)]
    [InlineData("pdf_processing", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidName_IsTheSpecificationsRule(string? name, bool valid)
    {
        Assert.Equal(valid, SkillFrontmatter.IsValidName(name));
    }

    [Fact]
    public void IsValidName_CapsAtSixtyFour()
    {
        Assert.True(SkillFrontmatter.IsValidName(new string('a', 64)));
        Assert.False(SkillFrontmatter.IsValidName(new string('a', 65)));
        Assert.Equal(64, SkillFrontmatter.MaxNameLength);
        Assert.Equal(1024, SkillFrontmatter.MaxDescriptionLength);
    }

    [Fact]
    public void Write_IsTheFencesTheTwoKeysTheOtherLinesABlankLineAndTheBody()
    {
        string text = SkillFrontmatter.Write("haiku", "Writes haiku.", ["license: MIT"], "# Haiku\r\n\r\nFive, seven, five.\r\n");

        Assert.Equal("---\nname: haiku\ndescription: Writes haiku.\nlicense: MIT\n---\n\n# Haiku\n\nFive, seven, five.\n", text);
    }

    [Theory]
    [InlineData("Writes haiku.", "Writes haiku.")]
    [InlineData("Use when: the user asks.", "\"Use when: the user asks.\"")]
    [InlineData("Say \"hi\" \\ wave", "Say \"hi\" \\ wave")]   // quotes and backslashes inside a plain scalar read back as they are
    [InlineData("\"starts quoted", "\"\\\"starts quoted\"")]
    [InlineData("- a list", "\"- a list\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("42", "\"42\"")]
    [InlineData("ends with a colon:", "\"ends with a colon:\"")]
    [InlineData("a # comment", "\"a # comment\"")]
    [InlineData("", "\"\"")]
    public void Scalar_IsPlainWhenSafe_ElseDoubleQuoted(string value, string expected)
    {
        Assert.Equal(expected, SkillFrontmatter.Scalar(value));
    }

    [Theory]
    [InlineData("Writes haiku.")]
    [InlineData("Use when: the user asks, \"please\".")]
    [InlineData("A back\\slash and a # hash")]
    [InlineData("- leading dash")]
    [InlineData("42")]
    public void Write_RoundTrips_ThroughTryParse(string description)
    {
        string text = SkillFrontmatter.Write("x", description, ["metadata:", "  author: me"], "body");
        var fm = Parse(text, out string body);

        Assert.Equal("x", fm.Name);
        Assert.Equal(description, fm.Description);
        Assert.Equal(["metadata:", "  author: me"], fm.OtherLines);
        Assert.Equal("body", body);
    }

    [Fact]
    public void Flatten_FoldsWhitespaceRuns()
    {
        Assert.Equal("a b c", SkillFrontmatter.Flatten("  a\n\n b\t\tc \r\n"));
        Assert.Equal("", SkillFrontmatter.Flatten(" \n "));
    }
}
