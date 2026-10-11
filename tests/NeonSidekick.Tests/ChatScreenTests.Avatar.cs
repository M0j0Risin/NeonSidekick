using NeonSidekick.App;
using NeonSidekick.Emojis;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// The profile's avatar on the screen (2026-10-10, the user's ask): it opens the profile's replies, stands on a <c>/botchat</c>
/// speaker line, goes before the name in the window title, and <c>/avatar</c> sets, clears and says it.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task AReply_OpensWithTheAvatar_InPlaceOfTheDot()
    {
        _settings.Update(d => d.Avatar = "🦊");
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("🦊 Hello.", output);
        Assert.DoesNotContain(TranscriptRenderer.AssistantGlyph + "Hello.", output);
        Assert.Equal("🦊 neon", _titles[0]);
    }

    [Fact]
    public async Task AvatarCommand_WithoutThePane_TakesAWord_ClearsIt_AndTheTitleFollows()
    {
        // The test console types no emoji, so the avatar starts as one; without the pane /avatar reads a typed emoji or word.
        _settings.Update(d => d.Avatar = "🐙");
        PushLine("/avatar");
        PushLine("fox");                      // the best of a search: the fox
        PushLine("/avatar clear");
        PushLine("/avatar clear");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["🐙 neon", "🦊 neon", "neon"], _titles);
        Assert.Equal("", _settings.Current.Avatar);
        Assert.Contains("Avatar: 🦊 fox", output);
        Assert.Contains("Avatar: " + SettingsMenu.NoAvatarLabel, output);
        Assert.Contains(SettingsMenu.UnchangedNotice, output);   // the second clear
    }

    [Theory]
    [InlineData("neon", "🦊", "🦊 neon")]
    [InlineData("neon", "", "neon")]
    [InlineData("neon", "fox", "neon")]   // not an emoji: no avatar
    [InlineData("averyveryverylongprofilename1234", "🦊", "🦊 averyveryverylongprof…")]
    public void WindowTitle_PutsTheAvatarFirst_WithinTheCut(string profile, string avatar, string title)
    {
        Assert.Equal(title, ChatScreen.WindowTitle(profile, avatar));
        Assert.True(TextCells.Width(title) <= ChatScreen.WindowTitleCells);
    }

    [Fact]
    public void TheGlyph_IsTheAvatarAndASpace_OrTheDot()
    {
        Assert.Equal("🦊 ", TranscriptRenderer.GlyphFor("🦊"));
        Assert.Equal("👍🏽 ", TranscriptRenderer.GlyphFor(" 👍🏽 "));
        Assert.Equal(TranscriptRenderer.AssistantGlyph, TranscriptRenderer.GlyphFor(""));
        Assert.Equal(TranscriptRenderer.AssistantGlyph, TranscriptRenderer.GlyphFor(null));
        Assert.Equal(TranscriptRenderer.AssistantGlyph, TranscriptRenderer.GlyphFor("two words"));
    }

    [Fact]
    public void ASpeakerLine_WearsTheBotsAvatar_InPlaceOfTheDiamond()
    {
        string withAvatar = Markup.Remove(TranscriptRenderer.SpeakerMarkup("ada", Color.Red, "🦊"));
        Assert.Equal("🦊 ada", withAvatar);
        Assert.Equal(TranscriptRenderer.SpeakerGlyph + "ada", Markup.Remove(TranscriptRenderer.SpeakerMarkup("ada", Color.Red)));
    }

    [Fact]
    public void AReplyBlock_WithAnAvatar_IndentsItsWrappedLines_UnderIt()
    {
        var console = new TestConsole().Width(24);
        console.Write(new ReplyBlock("one two three four five six seven eight", glyph: true, glyphText: "🦊 "));
        var lines = console.Output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("🦊 one", lines[0]);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("   ", line));
        Assert.All(lines.Skip(1), line => Assert.NotEqual(' ', line[3]));
        Assert.True(lines.Length > 1);
    }

    [Fact]
    public void ThePlainPath_WritesTheGlyphItWasGiven()
    {
        var console = new TestConsole().Width(80);
        var transcript = new TranscriptRenderer(console);
        transcript.BeginAssistant(markdown: false, TranscriptRenderer.GlyphFor("🐙"));
        transcript.AppendDelta("Hi.");
        transcript.EndAssistant();
        Assert.StartsWith("🐙 Hi.", console.Output);
    }
}
