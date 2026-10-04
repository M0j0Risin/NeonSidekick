using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

/// <summary>The picture budget (2026-10-03): how many pictures, and megabytes of them, one request may carry.</summary>
public class PictureBudgetTests
{
    private static ImageAttachment Picture(string path, int bytes = 300) => new(path, new byte[bytes], ImageFile.Jpeg, 8, 8);

    /// <summary>A history of <paramref name="carriers"/> generate_image carriers of one picture each, a user turn ahead of each.</summary>
    private static ConversationHistory History(int carriers, int bytes = 300)
    {
        var history = new ConversationHistory("system") { MaxTurns = null };
        for (int i = 1; i <= carriers; i++)
        {
            history.AddUser("draw " + i);
            history.AddToolImages([Picture("comfy_images\\p" + i + ".png", bytes)], "generate_image");
        }

        return history;
    }

    private static int Pictures(IEnumerable<ChatMessage> messages) => messages.Sum(m => m.Contents.Count(c => c is DataContent));

    [Fact]
    public void Defaults_AndWording_ArePinned()
    {
        Assert.Equal(new PictureBudget(20, 24), PictureBudget.Default);
        Assert.True(new PictureBudget(0, 0).IsNone);
        Assert.False(PictureBudget.Default.IsNone);
        Assert.Equal("(picture comfy_images\\a.png taken out to keep the request small)", PictureBudget.LeftOut("comfy_images\\a.png"));
        Assert.Equal("(a picture, taken out to keep the request small)", PictureBudget.LeftOut(null));
        Assert.Equal("31 MB", PictureBudget.FormatMegabytes(31_400_000));
        Assert.Equal("0.4 MB", PictureBudget.FormatMegabytes(400_000));
        Assert.Equal("🖼️ 12 older pictures taken out (31 MB) to keep the request small", PictureBudget.TakenOutNotice(12, 31_000_000));
        Assert.Equal("🖼️ 1 older picture taken out (0.3 MB) to keep the request small", PictureBudget.TakenOutNotice(1, 300_000));
    }

    [Fact]
    public void WireBytes_AreTheBase64Length()
    {
        Assert.Equal(4, PictureBudget.WireBytes(new DataContent(new byte[3], ImageFile.Png)));
        Assert.Equal(8, PictureBudget.WireBytes(new DataContent(new byte[4], ImageFile.Png)));
        Assert.Equal(0, PictureBudget.WireBytes(new DataContent(Array.Empty<byte>(), ImageFile.Png)));
    }

    [Fact]
    public void UnderTheCaps_NothingIsTakenOut()
    {
        var history = History(4);
        var before = history.Messages.ToList();

        var trim = history.ApplyPictureBudget(new PictureBudget(4, 24));

        Assert.Equal(0, trim.Pictures);
        Assert.Equal(before, history.Messages);   // the same instances
    }

    [Fact]
    public void OverTheCount_TheOldestGo_UntilHalfTheCapIsLeft()
    {
        var history = History(5);

        var trim = history.ApplyPictureBudget(new PictureBudget(4, 0));

        Assert.Equal(3, trim.Pictures);                  // 5 → 2, half of 4
        Assert.Equal(2, Pictures(history.Messages));
        var carriers = history.Messages.Where(ConversationHistory.IsImageCarrier).ToList();
        Assert.Equal(5, carriers.Count);                 // still carriers, tags and order kept
        Assert.Equal("(picture comfy_images\\p1.png taken out to keep the request small)", carriers[0].Contents.OfType<TextContent>().Last().Text);
        Assert.Equal("generate_image", ConversationHistory.CarrierSource(carriers[0]));
        Assert.Contains(carriers[3].Contents, c => c is DataContent);
        Assert.Contains(carriers[4].Contents, c => c is DataContent);
        Assert.Equal(5, history.TurnCount);
    }

    [Fact]
    public void OverTheBytes_TheOldestGo_UntilHalfTheFigureIsLeft()
    {
        var history = History(4, bytes: 300_000);   // 400,000 wire bytes each, 1.6 MB in all

        var trim = history.ApplyPictureBudget(new PictureBudget(0, 1));

        Assert.Equal(3, trim.Pictures);              // down to ≤ 0.5 MB: one left
        Assert.Equal(1_200_000, trim.Bytes);
        Assert.Equal(1, Pictures(history.Messages));
    }

    [Fact]
    public void TheNewestPictureMessage_IsNeverTouched()
    {
        var history = new ConversationHistory("system") { MaxTurns = null };
        history.AddUser("old", [Picture("old.png")]);
        history.AddUser("look at these", [Picture("a.png"), Picture("b.png"), Picture("c.png")]);

        var trim = history.ApplyPictureBudget(new PictureBudget(1, 0));

        Assert.Equal(1, trim.Pictures);              // only the old one could go
        Assert.Equal(3, Pictures(history.Messages));
        var first = history.Messages[0];
        Assert.Equal(["old", "(picture old.png taken out to keep the request small)"], first.Contents.OfType<TextContent>().Select(t => t.Text));
    }

    [Fact]
    public void ASecondPass_ChangesNothing_SoThePrefixStaysTheSame()
    {
        var history = History(9);
        var budget = new PictureBudget(6, 0);
        history.ApplyPictureBudget(budget);
        var after = history.Messages.ToList();

        var again = history.ApplyPictureBudget(budget);
        history.AddUser("one more");
        history.AddToolImages([Picture("comfy_images\\p10.png")], "generate_image");
        var third = history.ApplyPictureBudget(budget);

        Assert.Equal(0, again.Pictures);
        Assert.Equal(0, third.Pictures);             // 4 held, under 6: no drop until the cap is passed again
        Assert.Equal(after, history.Messages.Take(after.Count));
    }

    [Fact]
    public void APictureWithNoPath_LeavesThePathlessLine()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("old"), new DataContent(new byte[3], ImageFile.Png)]),
            new(ChatRole.User, [new TextContent("new"), new DataContent(new byte[3], ImageFile.Png)]),
        };

        var trim = PictureBudget.TakeOut(messages, 0, 0);

        Assert.Equal(1, trim.Pictures);
        Assert.Equal("(a picture, taken out to keep the request small)", trim.Messages[0].Contents.OfType<TextContent>().Last().Text);
        Assert.Same(messages[1], trim.Messages[1]);
        Assert.Contains(messages[0].Contents, c => c is DataContent);   // the input is untouched
    }

    [Fact]
    public void Measure_CountsTheUserRolePictures()
    {
        var history = History(3, bytes: 3);

        Assert.Equal((3, 12L), PictureBudget.Measure(history.Messages));
        Assert.Equal((0, 0L), PictureBudget.Measure([new ChatMessage(ChatRole.Assistant, "hi")]));
    }
}
