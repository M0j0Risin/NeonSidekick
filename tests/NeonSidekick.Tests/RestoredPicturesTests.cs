using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

/// <summary>A restored conversation's pictures by turn (2026-10-10, the user's ask: <c>/sessions</c> and <c>/rewind</c> bring them back).</summary>
public class RestoredPicturesTests
{
    private static ImageAttachment Picture(string path) => new(path, [9, 9], "image/png", 1, 1);

    private static IEnumerable<string?> Paths(IReadOnlyList<DataContent> pictures) => pictures.Select(ConversationHistory.PicturePath);

    [Fact]
    public void ATurnsToolPictures_AreTools_AndGenerateImagesAreGeneratedToo()
    {
        var history = new ConversationHistory("");
        history.AddUser("draw and look");
        history.AddToolImages([Picture("comfy\\a.png")], GenerateImageTool.ToolName);
        history.AddToolImages([Picture("photo.jpg")], "view_image");
        history.AddAssistant("Done.");

        var turn = Assert.Single(RestoredPictures.Turns(history.Messages));

        Assert.Equal(["comfy\\a.png", "photo.jpg"], Paths(turn.Tools));
        Assert.Equal(["comfy\\a.png"], Paths(turn.Generated));
        Assert.Equal(["comfy\\a.png"], Paths(turn.Strip));
        Assert.Empty(turn.Own);
        Assert.Empty(turn.Imagined);
        Assert.True(turn.Any);
    }

    /// <summary>A message led by an /imagine note: the first pictures, as many as its labels, are the user's, the rest /imagine's; without a note all are the user's.</summary>
    [Fact]
    public void AnImagineNote_SplitsTheMessagesPictures_ByItsLabels()
    {
        var history = new ConversationHistory("");
        history.AddUser(ComfyText.ImagineNote("generated 2 pictures") + "\n\nlook [Image #1]", [Picture("C:\\mine.png"), Picture("comfy\\b.png"), Picture("comfy\\c.png")]);
        history.AddAssistant("Nice.");
        history.AddUser("and these [Image #1] [Image #2]", [Picture("C:\\x.png"), Picture("C:\\y.png")]);
        history.AddAssistant("Also nice.");

        var turns = RestoredPictures.Turns(history.Messages);

        Assert.Equal(2, turns.Count);
        Assert.Equal(["C:\\mine.png"], Paths(turns[0].Own));
        Assert.Equal(["comfy\\b.png", "comfy\\c.png"], Paths(turns[0].Imagined));
        Assert.Equal(["comfy\\b.png", "comfy\\c.png"], Paths(turns[0].Strip));
        Assert.Equal(["C:\\x.png", "C:\\y.png"], Paths(turns[1].Own));
        Assert.Empty(turns[1].Imagined);
        Assert.Empty(turns[1].Strip);
    }

    [Fact]
    public void ASavedPastesLabel_StillCountsAsTheUsers()
    {
        var history = new ConversationHistory("");
        history.AddUser(ComfyText.ImagineNote("generated 1 picture") + "\n\nsee " + UI.PasteBlocks.SavedLabel(1, "C:\\pasted\\mine.png"),[Picture("C:\\mine.png"), Picture("comfy\\b.png")]);

        var turn = Assert.Single(RestoredPictures.Turns(history.Messages));

        Assert.Equal(["C:\\mine.png"], Paths(turn.Own));
        Assert.Equal(["comfy\\b.png"], Paths(turn.Imagined));
    }

    /// <summary>Each turn carries its row's ordinal (none when never stamped), a compact's summary is an empty entry, and the list lines up with the rewind's replay.</summary>
    [Fact]
    public void Turns_CarryTheirOrdinals_AndLineUpWithTheReplay()
    {
        var summary = new ChatMessage(ChatRole.User, ConversationCompactor.SummaryPreamble + "we drew cats");
        var first = new ChatMessage(ChatRole.User, "hi");
        ConversationHistory.SetTurnOrdinal(first, 4);
        var history = new ConversationHistory("");
        history.Restore([summary, new ChatMessage(ChatRole.Assistant, "ok"), first, new ChatMessage(ChatRole.Assistant, "hello")]);
        history.AddUser("draw");
        history.AddToolImages([Picture("comfy\\d.png")], GenerateImageTool.ToolName);
        history.AddAssistant("There.");

        var turns = RestoredPictures.Turns(history.Messages);

        Assert.Equal(ConversationRewind.Replay(history.Messages).Count, turns.Count);
        Assert.Equal([null, 4, null], turns.Select(t => t.Ordinal));
        Assert.False(turns[0].Any);
        Assert.False(turns[1].Any);
        Assert.Equal(["comfy\\d.png"], Paths(turns[2].Generated));
    }
}
