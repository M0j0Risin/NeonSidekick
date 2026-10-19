using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Images;
using NeonSidekick.Screen;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The picture folders' words (2026-10-18, the user's ask): <c>/camera purge</c>, <c>/screen purge</c>, <c>/splash view|thumbs|purge</c> and the new
/// <c>/pasted view|thumbs|purge</c> — their parses, their argument lists, their classes under a reply. Pure.
/// </summary>
public sealed class PictureFolderCommandTests
{
    private static readonly ChatScreen.ArgumentSources Sources = new(() => [], "default", [], _ => [], _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false), _ => new Files.MentionResult(Files.FileOutcome.Ok, [], false));

    [Theory]
    [InlineData("", PictureFolderVerb.Bare)]
    [InlineData("view", PictureFolderVerb.View)]
    [InlineData(" THUMBS ", PictureFolderVerb.Thumbs)]
    [InlineData("Purge", PictureFolderVerb.Purge)]
    [InlineData("bogus", PictureFolderVerb.Unknown)]
    [InlineData("view now", PictureFolderVerb.Unknown)]
    public void TheThreeWords_Parse_AnythingElseIsUnknown(string args, PictureFolderVerb verb)
    {
        Assert.Equal(verb, SplashCommand.Parse(args));
        Assert.Equal(verb, PastedCommand.Parse(args));
    }

    [Fact]
    public void SlashSplash_TakesWords_AndSlashPasted_IsACommand()
    {
        Assert.Equal((SlashCommand.Splash, "view"), SlashCommands.Parse("/splash view"));   // Overloaded until 2026-10-18
        Assert.Equal((SlashCommand.Pasted, "purge"), SlashCommands.Parse("/PASTED purge"));
        Assert.Equal((SlashCommand.Pasted, ""), SlashCommands.Parse("/pasted"));
        Assert.Contains(PastedCommand.Word, SlashCommands.Words);
        Assert.Contains(SlashCommands.Completions, c => c.Text == PastedCommand.Word);
        Assert.False(SlashCommands.IsBasic(SlashCommands.HelpEntries.Single(e => e.Command == PastedCommand.Word)));   // the Advanced tab
    }

    [Fact]
    public void TheArgumentLists_OfferTheThreeWords_NoneAfterASpace()
    {
        Assert.Equal(["view", "thumbs", "purge"], SplashCommand.Complete("").Select(c => c.Text));
        Assert.Equal(["purge"], PastedCommand.Complete("pu").Select(c => c.Text));
        Assert.Empty(PastedCommand.Complete("view x"));
        Assert.Equal(["view", "thumbs", "purge"], ChatScreen.ArgumentItems("/splash", "", Sources).Select(c => c.Text));
        Assert.Equal(["view", "thumbs", "purge"], ChatScreen.ArgumentItems("/pasted", "", Sources).Select(c => c.Text));
    }

    [Fact]
    public void CameraPurge_IsAWord_AndAWordAfterItIsTheUsage()
    {
        Assert.Equal(CameraVerb.Purge, CameraCommand.Parse("purge").Verb);
        Assert.Equal(CameraVerb.Purge, CameraCommand.Parse(" PURGE ").Verb);
        var after = CameraCommand.Parse("purge all");
        Assert.Equal((CameraVerb.Unknown, CameraText.Usage), (after.Verb, after.Error));
        Assert.Contains(CameraCommand.Words, w => w.Word == "purge");
        Assert.EndsWith("| thumbs | purge]", CameraText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenPurge_IsItsOwnWord_NeverAFolderWord()
    {
        Assert.True(ScreenText.IsPurgeWord(" purge "));
        Assert.False(ScreenText.IsFolderWord("purge"));   // the folder words are the quick ones under a reply
        Assert.False(ScreenText.IsPurgeWord("window:purge"));
        Assert.Contains(ScreenText.CompletionWords, w => w.Word == ScreenText.PurgeWord);
        Assert.EndsWith("| view | thumbs | purge]", ScreenText.Usage, StringComparison.Ordinal);
    }

    /// <summary>The purges wait for the reply; the windows open over it; a bad word's error is at once; the bare /splash still stops the reply.</summary>
    [Theory]
    [InlineData(SlashCommand.Camera, "purge", MidTurnClass.Deferred)]   // every other /camera word but snap is quick
    [InlineData(SlashCommand.Screen, "purge", MidTurnClass.Deferred)]
    [InlineData(SlashCommand.Comfy, "purge", MidTurnClass.Deferred)]
    [InlineData(SlashCommand.Splash, "", MidTurnClass.Cancel)]
    [InlineData(SlashCommand.Splash, "view", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Splash, "thumbs", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Splash, "purge", MidTurnClass.Deferred)]
    [InlineData(SlashCommand.Splash, "bogus", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Pasted, "view", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Pasted, "thumbs", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Pasted, "purge", MidTurnClass.Deferred)]
    [InlineData(SlashCommand.Pasted, "", MidTurnClass.Quick)]
    [InlineData(SlashCommand.Comfy, "", MidTurnClass.Pane)]   // 2026-10-18: the bare word is /tools comfy's page
    [InlineData(SlashCommand.Comfy, "status", MidTurnClass.Deferred)]
    public void UnderAReply_EachFormHasItsClass(SlashCommand command, string args, MidTurnClass expected) =>
        Assert.Equal(expected, ChatScreen.MidTurnPolicy(command, args));

    [Fact]
    public void TheWords_ArePinned()
    {
        Assert.Equal("Usage: /splash, or /splash view|thumbs|purge", SplashCommand.Usage);
        Assert.Equal("Usage: /pasted view|thumbs|purge", PastedCommand.Usage);
        Assert.Equal("(the built-in splash pictures show until this folder has some; drop pictures here, or ask the model to set_splash_image)", SplashCommand.BuiltInNotice);
        Assert.Contains(NeonSidekick.Llm.Tools.SetSplashImageTool.ToolName, SplashCommand.BuiltInNotice, StringComparison.Ordinal);
        Assert.Equal("/tools comfy", ChatScreen.ComfyToolsLine);
    }
}
