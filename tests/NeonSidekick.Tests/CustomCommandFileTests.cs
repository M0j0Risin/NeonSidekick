using NeonSidekick.CustomCommands;

namespace NeonSidekick.Tests;

/// <summary>A command file's frontmatter read leniently, Claude Code's way: only from line 1, unknown keys ignored, a bad block a warning.</summary>
public class CustomCommandFileTests
{
    [Fact]
    public void NoFrontmatter_TheWholeFileIsTheBody()
    {
        var file = CustomCommandFile.Read("\n\nReview the code.\n\n");
        Assert.Equal("Review the code.", file.Body);
        Assert.Null(file.Description);
        Assert.Null(file.FrontmatterWarning);
        Assert.True(file.UserInvocable);
    }

    [Fact]
    public void AFenceNotOnLineOne_IsBodyText()
    {
        var file = CustomCommandFile.Read("Intro\n---\ndescription: x\n---\nmore");
        Assert.Null(file.Description);
        Assert.Equal("Intro\n---\ndescription: x\n---\nmore", file.Body);
    }

    [Fact]
    public void TheFourKeys_AreRead_AndOthersIgnored()
    {
        var file = CustomCommandFile.Read("---\ndescription: Review a pull request\nargument-hint: [pr-number] [focus]\narguments: [pr, focus]\nuser-invocable: true\nallowed-tools: Bash(git *)\nmodel: opus\n---\nReview PR $pr for $focus.\n");
        Assert.Equal("Review a pull request", file.Description);
        Assert.Equal("[pr-number] [focus]", file.ArgumentHint);   // read as text, never a flow list
        Assert.Equal(["pr", "focus"], file.Arguments);
        Assert.True(file.UserInvocable);
        Assert.Null(file.FrontmatterWarning);
        Assert.Equal("Review PR $pr for $focus.", file.Body);
    }

    [Theory]
    [InlineData("arguments: [issue, branch]")]
    [InlineData("arguments: issue branch")]
    [InlineData("arguments:\n  - issue\n  - branch")]
    [InlineData("arguments:\n- issue\n- branch")]
    public void Arguments_ReadEveryWay(string line)
    {
        Assert.Equal(["issue", "branch"], CustomCommandFile.Read("---\n" + line + "\n---\nbody").Arguments);
    }

    [Fact]
    public void AColumnZeroListUnderAnUnknownKey_KeepsTheOtherFields()
    {
        var file = CustomCommandFile.Read("---\nallowed-tools:\n- Bash(git add:*)\n- Bash(git status:*)\ndescription: Create a git commit\n---\nCommit it.");
        Assert.Equal("Create a git commit", file.Description);
        Assert.Null(file.FrontmatterWarning);
    }

    [Fact]
    public void ThreeDots_CloseTheFrontmatter()
    {
        var file = CustomCommandFile.Read("---\ndescription: dotted\n...\nthe body");
        Assert.Equal("dotted", file.Description);
        Assert.Equal("the body", file.Body);
    }

    [Fact]
    public void APlainValue_IsCutAtAComment_AndAQuotedOneKeptWhole()
    {
        Assert.Equal("Review PR", CustomCommandFile.Read("---\ndescription: Review PR #42\n---\nx").Description);
        Assert.Equal("Review PR #42", CustomCommandFile.Read("---\ndescription: \"Review PR #42\"\n---\nx").Description);
    }

    [Fact]
    public void FrontmatterThatDoesNotRead_LoadsWithAWarning()
    {
        var file = CustomCommandFile.Read("---\ndescription: fine\nthis line is no pair\n---\nThe body.");
        Assert.Null(file.Description);
        Assert.StartsWith("the frontmatter didn't read", file.FrontmatterWarning);
        Assert.Equal("The body.", file.Body);
    }

    [Fact]
    public void AnUnclosedFence_MakesTheRestTheBody_WithAWarning()
    {
        var file = CustomCommandFile.Read("---\ndescription: open\nSay hello.");
        Assert.Equal(CustomCommandText.UnclosedFrontmatterWarning, file.FrontmatterWarning);
        Assert.Equal("description: open\nSay hello.", file.Body);
    }

    [Fact]
    public void ABomAndCrlf_AreFolded()
    {
        var file = CustomCommandFile.Read("﻿---\r\ndescription: crlf\r\n---\r\nline one\r\nline two\r\n");
        Assert.Equal("crlf", file.Description);
        Assert.Equal("line one\nline two", file.Body);
    }

    [Fact]
    public void UserInvocableFalse_IsRead()
    {
        Assert.False(CustomCommandFile.Read("---\nuser-invocable: false\n---\nx").UserInvocable);
    }

    [Fact]
    public void TheFallbackDescription_IsTheFirstLine_ItsHeadingMarkOff()
    {
        Assert.Equal("Review the diff", CustomCommandFile.FallbackDescription("\n## Review the diff\nthen more"));
        Assert.Null(CustomCommandFile.FallbackDescription("\n\n"));
    }

    [Theory]
    [InlineData("Diff: !`gh pr diff`", true)]
    [InlineData("!`git status`", true)]
    [InlineData("```!\ngh pr view\n```", true)]
    [InlineData("KEY=!`x`", false)]
    [InlineData("Say it! `quoted`", false)]
    public void ShellLines_AreFound_AsClaudeReadsThem(string body, bool shell)
    {
        Assert.Equal(shell, CustomCommandFile.HasShellLine(body));
    }

    [Fact]
    public void TheTemplate_ReadsBackAsACommand_WithNoPlaceholderInItsBody()
    {
        var file = CustomCommandFile.Read(CustomCommandText.Template("triage"));
        Assert.Null(file.FrontmatterWarning);
        Assert.Equal(CustomCommandText.TemplateBody("triage"), file.Body);
        Assert.Equal("say what /triage does (shown in the / list and /help)", file.Description);
        Assert.Equal("<what to type after it>", file.ArgumentHint);
        Assert.DoesNotContain("$", file.Body);
        Assert.Equal(CustomCommandText.TemplateBody("triage"), CustomCommandTemplate.Expand(file.Body, "", file.Arguments).Text);
    }
}
