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
        _settings.Update(d => d.ModelAvatar = "🦊");
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
        _settings.Update(d => d.ModelAvatar = "🐙");
        PushLine("/avatar");
        PushLine("fox");                      // the best of a search: the fox
        PushLine("/avatar clear");
        PushLine("/avatar clear");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(["🐙 neon", "🦊 neon", "neon"], _titles);
        Assert.Equal("", _settings.Current.ModelAvatar);
        Assert.Contains("Model avatar: 🦊 fox", output);
        Assert.Contains("Model avatar: " + SettingsMenu.NoAvatarLabel, output);
        Assert.Contains(SettingsMenu.UnchangedNotice, output);   // the second clear
    }

    /// <summary>
    /// The user avatar (2026-10-12, the user's ask): a sent line opens with it in place of ›, its second line indented under the
    /// words; none (the default) keeps ›.
    /// </summary>
    [Fact]
    public async Task ASentLine_OpensWithTheUserAvatar_InPlaceOfThePrompt()
    {
        Assert.Null(new AppSettingsData().UserAvatar);
        _settings.Update(d => d.UserAvatar = "🦉");
        _chat.EnqueueText("Hello.");
        PushLine("hi there");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("🦉 hi there", output);
        Assert.DoesNotContain(InputLine.PromptGlyph + "hi there", output);
    }

    [Fact]
    public void TheUserGlyph_IsTheAvatarAndASpace_OrThePrompt_AndASecondLineSitsUnderTheWords()
    {
        Assert.Equal("🦉 ", InputLine.UserGlyph("🦉"));
        Assert.Equal(InputLine.PromptGlyph, InputLine.UserGlyph(null));
        Assert.Equal(InputLine.PromptGlyph, InputLine.UserGlyph(""));
        Assert.Equal(InputLine.PromptGlyph, InputLine.UserGlyph("owl"));   // not an emoji: none
        Assert.Equal("🦉 one\n   two", Markup.Remove(InputLine.SubmittedMarkup("one\ntwo", UserLineStyle.Bold, "🦉 ")));
        Assert.Equal(InputLine.PromptGlyph + "one\n" + InputLine.ContinuationIndent + "two", Markup.Remove(InputLine.SubmittedMarkup("one\ntwo", UserLineStyle.Bold)));
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

    /// <summary>
    /// The draft row's emoji button (2026-10-11, the user's ask): a click on the ☺ opens the emoji picker over the draft; ESC leaves
    /// the draft as it was, a search and Enter put the emoji at its cursor, on the Recent list too, and Enter sends the line.
    /// </summary>
    [Fact]
    public async Task TheEmojiButton_OpensThePicker_AndThePickLandsInTheDraft()
    {
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _settings.Update(d => d.EmojiPicker = true);
        _chat.EnqueueText("Purr.");
        string cat = EmojiSearch.Rank("cat", EmojiCatalog.Shipped.Entries).First().Emoji;
        StepsWhenIdle(
            input => { input.Push(Keys.Char('h'), Keys.Char('i'), Keys.Char(' ')); input.PushClick(118, 100); },   // the ☺: column 118 of 120
            input => input.Push(Keys.Escape),                                                                         // the picker: nothing picked
            input => input.PushClick(118, 100),
            input => input.Push(Keys.Char('c'), Keys.Char('a'), Keys.Char('t'), Keys.Enter),                          // a search, its first emoji
            Key(Keys.Enter),                                                                                          // the line, sent
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EmojiText.PickerTitle, output);
        Assert.Equal("hi " + cat, _chat.Requests[0].Last(m => m.Role == Microsoft.Extensions.AI.ChatRole.User).Text);
        Assert.Equal(cat, EmojiPrefs.Load(_settings.StorageDirectory).Recent[0]);
    }
}
