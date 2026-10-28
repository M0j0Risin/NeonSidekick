using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Plans;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/undo</c>'s and <c>/rewind … files</c>' words (2026-10-28, the checkpoints plan): the arguments, the model's note, the completion, the
/// mid-turn and Web UI classes, and which tools a checkpoint covers.
/// </summary>
public sealed class UndoTextTests
{
    [Theory]
    [InlineData("", 1, false, false)]
    [InlineData("3", 3, false, false)]
    [InlineData(" purge ", 0, true, false)]
    [InlineData("PURGE all", 0, true, true)]
    public void Parse_ReadsACountOrAPurge(string args, int count, bool purge, bool all)
    {
        Assert.Equal(new UndoArgs(count, purge, all), UndoText.Parse(args));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("two")]
    [InlineData("purge some")]
    [InlineData("2 3")]
    public void Parse_AnythingElse_IsTheUsageError(string args)
    {
        Assert.Null(UndoText.Parse(args));
    }

    [Theory]
    [InlineData("", 1, false)]
    [InlineData("files", 1, true)]
    [InlineData("3 files", 3, true)]
    [InlineData(" 2 ", 2, false)]
    public void RewindParseArgs_TakesTheFilesWordOffTheEnd(string args, int count, bool files)
    {
        Assert.Equal(new RewindArgs(count, files), RewindText.ParseArgs(args));
    }

    [Theory]
    [InlineData("0 files")]
    [InlineData("profiles")]
    [InlineData("files 3")]
    public void RewindParseArgs_AnythingElse_IsTheUsageError(string args)
    {
        Assert.Null(RewindText.ParseArgs(args));
    }

    [Fact]
    public void NothingNotice_NamesTheSetting_OnlyWhileItIsOff()
    {
        Assert.Equal(UndoText.NothingToUndo, UndoText.NothingNotice(off: false));
        Assert.EndsWith("; File checkpoints is off in /tools › Files › File)", UndoText.NothingNotice(off: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ModelNote_IsPinned_AndTwoMerge_IntoOneLine()
    {
        string first = UndoText.ModelNote(["fix the build\nplease"], ["a.cs", "b.cs"], ["c.cs"]);
        Assert.Equal(ConversationHistory.UndoNotePreamble + "the file changes you made answering \"fix the build\" are undone; these files are back as they were before: a.cs, b.cs; these were kept as they are: c.cs]", first);
        Assert.True(ConversationHistory.IsUndoNote(new Microsoft.Extensions.AI.TextContent(first)));

        string second = UndoText.ModelNote(["rename it"], ["d.cs"], []);
        string merged = UndoText.Merge(first, second);

        Assert.Equal(second, UndoText.Merge(null, second));
        Assert.StartsWith(first.TrimEnd(']') + "; then the file changes you made answering \"rename it\"", merged, StringComparison.Ordinal);
        Assert.EndsWith("d.cs]", merged, StringComparison.Ordinal);
        Assert.Single(merged, c => c == ']');
    }

    [Fact]
    public void Excerpt_CutsAtAbout40()
    {
        Assert.Equal("short", UndoText.Excerpt("  short  \nmore"));
        string cut = UndoText.Excerpt(new string('x', 60));
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.Equal(41, cut.Length);
    }

    [Fact]
    public void Megabytes_RoundsUp_AndNeverSaysZero()
    {
        Assert.Equal("1 MB", UndoText.Megabytes(1));
        Assert.Equal("2 MB", UndoText.Megabytes((1024 * 1024) + 1));
        Assert.Equal("2,048 MB", UndoText.Megabytes(2048L * 1024 * 1024));
    }

    [Fact]
    public void Completion_OffersPurge_ThenAll_AndFilesAfterARewindsNumber()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new MentionResult(FileOutcome.Ok, [], false), _ => new MentionResult(FileOutcome.Ok, [], false));

        Assert.Equal(["purge"], ChatScreen.ArgumentItems("/undo", "p", sources).Select(i => i.Text));
        Assert.Equal(["purge all"], ChatScreen.ArgumentItems("/undo", "purge a", sources).Select(i => i.Text));
        Assert.Equal(["3 files"], ChatScreen.ArgumentItems("/rewind", "3 f", sources).Select(i => i.Text));
        Assert.Equal(["files"], ChatScreen.ArgumentItems("/rewind", "f", sources).Select(i => i.Text));
        Assert.Empty(ChatScreen.ArgumentItems("/rewind", "x f", sources));
        Assert.Contains(SlashCommands.Completions, c => c.Text == "/undo");
    }

    [Fact]
    public void MidTurn_UndoStopsTheReply_APurgeWaits()
    {
        Assert.Equal(MidTurnClass.Cancel, ChatScreen.MidTurnPolicy(SlashCommand.Undo, ""));
        Assert.Equal(MidTurnClass.Cancel, ChatScreen.MidTurnPolicy(SlashCommand.Undo, "2"));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Undo, "purge all"));
    }

    [Fact]
    public void TheWebUi_RunsUndo_ButRefusesItsPurges()
    {
        var on = new WebCommandSettings(Commands: true, FileTools: true, ObsidianTools: true);

        Assert.True(WebCommands.Offers(SlashCommand.Undo));
        Assert.Equal(WebCommandVerdict.Run, WebCommands.Check(SlashCommand.Undo, "2", "/undo 2", on));
        var purge = WebCommands.Check(SlashCommand.Undo, "purge all", "/undo purge all", on);
        Assert.False(purge.Allowed);
        Assert.Equal(WebUi.WebUiText.OpensPane("/undo purge", null), purge.Reason);
    }

    [Fact]
    public void EveryChangingTool_IsTrackedUntrackedOrFileless_ExactlyOnce()
    {
        var sets = new[] { CheckpointTools.Tracked, CheckpointTools.Untracked.Keys.ToHashSet(StringComparer.Ordinal), CheckpointTools.NoFiles };

        Assert.All(PlanTools.Mutating, name => Assert.True(sets.Count(s => s.Contains(name)) == 1, name + ": add it to one of CheckpointTools' lists"));
        Assert.All(sets.SelectMany(s => s), name => Assert.True(PlanTools.Mutating.Contains(name) || PlanTools.ReadOnly.Contains(name), name + " is no tool"));
        Assert.Equal(sets.Sum(s => s.Count), sets.SelectMany(s => s).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void KindsAmong_NamesEachUntrackedKindOnce_AnMcpToolAsMcp()
    {
        Assert.Equal([UntrackedKind.Shell, UntrackedKind.Git, UntrackedKind.Mcp], CheckpointTools.KindsAmong(["write_file", "gitlib_commit", "run_command", "process", "server__tool"]));
        Assert.Empty(CheckpointTools.KindsAmong(["write_file", "delete", "start_timer"]));
    }
}
