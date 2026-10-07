using NeonSidekick.App;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary><c>/youtube</c>'s words (2026-10-05): a verb only when what follows fits it, a search otherwise; the completion; its class under a reply.</summary>
public class YouTubeCommandTests
{
    [Theory]
    [InlineData("", YouTubeVerb.Status, "", null, null)]
    [InlineData("status", YouTubeVerb.Status, "", null, null)]
    [InlineData("lofi beats", YouTubeVerb.Search, "lofi beats", null, null)]
    [InlineData("close encounters", YouTubeVerb.Search, "close encounters", null, null)]      // a verb that takes nothing, with words after: a search
    [InlineData("pause music", YouTubeVerb.Search, "pause music", null, null)]
    [InlineData("search pause", YouTubeVerb.Search, "pause", null, null)]
    [InlineData("play", YouTubeVerb.Resume, "", null, null)]
    [InlineData("RESUME", YouTubeVerb.Resume, "", null, null)]
    [InlineData("play aqz-KE-bpKQ", YouTubeVerb.Play, "aqz-KE-bpKQ", "aqz-KE-bpKQ", null)]
    [InlineData("play aqz-KE-bpKQ 1:30", YouTubeVerb.Play, "aqz-KE-bpKQ 1:30", "aqz-KE-bpKQ", 90.0)]
    [InlineData("play https://youtu.be/aqz-KE-bpKQ?t=42", YouTubeVerb.Play, "https://youtu.be/aqz-KE-bpKQ?t=42", "aqz-KE-bpKQ", 42.0)]
    [InlineData("play that funky music", YouTubeVerb.Search, "play that funky music", null, null)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", YouTubeVerb.Play, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ", null)]
    [InlineData("aqz-KE-bpKQ", YouTubeVerb.Search, "aqz-KE-bpKQ", null, null)]                // a bare word is words: play takes ids
    [InlineData("pause", YouTubeVerb.Pause, "", null, null)]
    [InlineData("seek 2m", YouTubeVerb.Seek, "2m", null, 120.0)]
    [InlineData("seek the truth", YouTubeVerb.Search, "seek the truth", null, null)]
    [InlineData("volume 40%", YouTubeVerb.Volume, "40%", null, 40.0)]
    [InlineData("mute", YouTubeVerb.Mute, "", null, null)]
    [InlineData("unmute", YouTubeVerb.Unmute, "", null, null)]
    [InlineData("close", YouTubeVerb.Close, "", null, null)]
    [InlineData("save", YouTubeVerb.Save, "", null, null)]                                       // the saved videos (2026-10-07)
    [InlineData("save https://youtu.be/aqz-KE-bpKQ", YouTubeVerb.Save, "https://youtu.be/aqz-KE-bpKQ", "aqz-KE-bpKQ", null)]
    [InlineData("save aqz-KE-bpKQ", YouTubeVerb.Save, "aqz-KE-bpKQ", "aqz-KE-bpKQ", null)]
    [InlineData("save the whales", YouTubeVerb.Search, "save the whales", null, null)]
    [InlineData("saved", YouTubeVerb.Saved, "", null, null)]
    [InlineData("saved by the bell", YouTubeVerb.Search, "saved by the bell", null, null)]
    [InlineData("unsave 2", YouTubeVerb.Unsave, "2", null, null)]
    [InlineData("unsave aqz-KE-bpKQ", YouTubeVerb.Unsave, "aqz-KE-bpKQ", null, null)]
    [InlineData("unsave the date", YouTubeVerb.Search, "unsave the date", null, null)]
    public void Parse_ReadsAVerb_OnlyWhenWhatFollowsFitsIt(string args, YouTubeVerb verb, string text, string? id, double? number)
    {
        var line = YouTubeCommand.Parse(args);

        Assert.Equal(new YouTubeCommandLine(verb, text, id, number), line);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("seek")]
    [InlineData("volume")]
    [InlineData("volume 140")]
    [InlineData("unsave")]
    public void Parse_AVerbWithoutItsArgument_IsTheUsage(string args)
    {
        Assert.Equal(new YouTubeCommandLine(YouTubeVerb.Unknown, Error: YouTubeText.CommandUsage), YouTubeCommand.Parse(args));
    }

    [Fact]
    public void Complete_OffersTheVerbs_OnlyForTheFirstWord()
    {
        Assert.Equal(["pause", "play"], YouTubeCommand.Complete("p").Select(c => c.Text).Order());
        Assert.Equal(YouTubeCommand.Words.Count, YouTubeCommand.Complete("").Count);
        Assert.Empty(YouTubeCommand.Complete("lofi b"));
    }

    [Fact]
    public void TheCommand_IsRegistered_AndRunsOnThePaneUnderAReply()
    {
        Assert.Equal((SlashCommand.YouTube, "pause"), SlashCommands.Parse("/youtube pause"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.YouTube));
        Assert.Contains("/youtube", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/youtube" && e.Summary == YouTubeText.HelpSummary);
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.YouTube, "big buck bunny"));
        Assert.Equal(MidTurnClass.Pane, ChatScreen.MidTurnPolicy(SlashCommand.YouTube, ""));
    }
}
