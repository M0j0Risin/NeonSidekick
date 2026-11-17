using System.Net;
using System.Text.Json;
using NeonSidekick.Files;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The phone's uploads at the session (2026-11-16, plan lucid-roaming-lantern #39): read by their bytes (a picture stripped and fitted, a PDF by its
/// header, anything else a 415; a HEIF brand the codecs don't read, a photo past the pixel cap and an unreadable picture each a 422), held per page
/// (4, then a 409; 60 MB in all, the waiting lines' counted, then a 507), let go after 15 minutes or on a profile switch (an upload across it a 409),
/// taken by a send only from its own page, given back with a withdrawn or dropped line under its own page, and refused with a command.
/// </summary>
public sealed partial class WebUiTests
{
    private const string PageA = "pageAAAAAAAAAAAAAAAAAA";
    private const string PageB = "pageBBBBBBBBBBBBBBBBBB";

    private static byte[] PdfBody(int size = 64)
    {
        byte[] bytes = new byte[size];
        "%PDF-1.4\n"u8.CopyTo(bytes);
        return bytes;
    }

    private WebChip Held(string page, byte[]? body = null)
    {
        var reply = _session.Attach(page, body ?? PdfBody(), _session.AttachEpoch, IPAddress.Loopback);
        Assert.Equal(200, reply.Status);
        return reply.Chip!;
    }

    private static WebChips ChipsIn(WebEvent item) => JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebChips)!;

    [Fact]
    public void APicture_IsHeldStrippedAndFitted_WithASmallThumb_AndAPdfByItsHeader()
    {
        var picture = _session.Attach(PageA, ImageFixtures.ExifJpeg(), 0, null);
        Assert.Equal(200, picture.Status);
        Assert.Equal(("picture", true), (picture.Chip!.Kind, picture.Chip.Thumb!.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)));
        var pdf = Held(PageA);
        Assert.Equal(("pdf", null, "64 B"), (pdf.Kind, pdf.Thumb, pdf.Size));
        Assert.Equal([picture.Chip.Id, pdf.Id], _session.ChipsOf(PageA).Select(c => c.Id));
        Assert.Empty(_session.ChipsOf(PageB));
        Assert.Equal(_session.ChipsOf(PageA), _session.Snapshot(PageA).Chips);
        Assert.Equal((ImageFile.MaxFileBytes, ImageFile.MaxPixels, WebUiSession.MaxHeldPerPage), (_session.Snapshot(PageA).AttachMaxBytes, _session.Snapshot(PageA).AttachMaxPixels, _session.Snapshot(PageA).AttachMaxHeld));
    }

    [Fact]
    public void ABigPhotosThumb_IsUnder16KB_And96PxOnItsLongSide()
    {
        Assert.True(ImageFile.TryLoad(ImageFixtures.SidewaysJpeg(4000, 3000, 1), "p.jpg", out var picture, out _, photo: true));
        string thumb = WebAttachSniff.Thumb(picture!)!;
        byte[] bytes = Convert.FromBase64String(thumb[(thumb.IndexOf(',') + 1)..]);
        Assert.True(bytes.Length < 16 * 1024, bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(ImageFile.TryLoad(bytes, "t.jpg", out var small, out _));
        Assert.Equal(96, Math.Max(small!.Width, small.Height));
    }

    [Fact]
    public void TheRefusals_AreChosenByTheBytes()
    {
        Assert.Equal((415, WebUiText.AttachNotPicture), Refusal("just some text"u8.ToArray()));
        Assert.Equal((422, WebUiText.AttachTooManyPixels), Refusal(ImageFileTests.BlackPng(8000, 6000)));
        byte[] brokenJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 1, 2, 3, 4];
        Assert.Equal((422, WebUiText.AttachUnreadable), Refusal(brokenJpeg));
        Assert.Empty(_session.ChipsOf(PageA));
    }

    /// <summary>A HEIF brand WIC can't read gets the iPhone's words (the HEIF codec is a Store extension, not a given).</summary>
    [WindowsFact]
    public void AHeifBrand_TheCodecsDontRead_GetsTheIphonesWords()
    {
        Assert.Equal((422, WebUiText.AttachHeic), Refusal(HeifBytes()));
        Assert.True(WebAttachSniff.IsHeif(HeifBytes()));
    }

    /// <summary>Every HEIF brand ImageIO knows is a HEIF here too (the review's #65), and an AVIF is sniffed as one (#69).</summary>
    [Fact]
    public void EveryHeifBrand_IsHeif_AndAnAvif_IsSniffed()
    {
        foreach (string brand in Images.ImageIOFormats.HeifBrands)
        {
            byte[] bytes = [0, 0, 0, 24, .. "ftyp"u8.ToArray(), .. System.Text.Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0];
            Assert.True(WebAttachSniff.IsHeif(bytes), brand);
            Assert.Equal("image/heic", WebAttachSniff.SourceType(bytes));
        }

        Assert.Equal("image/avif", WebAttachSniff.SourceType([0, 0, 0, 24, .. "ftypavif"u8.ToArray(), 0, 0, 0, 0]));
        Assert.False(WebAttachSniff.IsHeif([0, 0, 0, 24, .. "ftypavif"u8.ToArray(), 0, 0, 0, 0]));
    }

    /// <summary>A Mac reads HEIC itself: a HEIF brand that fails to load is a broken file.</summary>
    [MacFact]
    public void OnAMac_ABrokenHeif_IsUnreadable()
    {
        Assert.Equal((422, WebUiText.AttachUnreadable), Refusal(HeifBytes()));
    }

    private (int, string?) Refusal(byte[] body) => _session.Attach(PageA, body, 0, null) is var r ? (r.Status, r.Message) : default;

    private static byte[] HeifBytes() => [0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0, .. "mif1heic"u8.ToArray(), 1, 2, 3, 4];

    [Fact]
    public void AFifthForAPage_Is409_TheBytesInAll507_AndAnotherPageHasItsOwnFour()
    {
        for (int i = 0; i < WebUiSession.MaxHeldPerPage; i++)
        {
            Held(PageA);
        }

        Assert.Equal((409, WebUiText.AttachFull), Refusal(PdfBody()));
        Assert.Equal("4 at a time; 1 not added.", WebUiText.AttachFull);
        Held(PageB, PdfBody(20_000_000));
        Held(PageB, PdfBody(20_000_000));
        var over = _session.Attach(PageB, PdfBody(20_000_000), 0, null);
        Assert.Equal((507, WebUiText.AttachTooMuch), (over.Status, over.Message));
    }

    [Fact]
    public void AnUploadIsLetGo_AfterItsTime_ThePageToldItsRow()
    {
        var chip = Held(PageA);
        using var subscription = _session.Events.Subscribe(PageA, "loadAAAAAAAAAAAAAAAAAA");
        _time.Advance(WebUiSession.HeldFor - TimeSpan.FromSeconds(1));
        Assert.Equal([chip.Id], _session.ChipsOf(PageA).Select(c => c.Id));
        Drain(subscription);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty(_session.ChipsOf(PageA));
        Assert.Empty(ChipsIn(Drain(subscription).Last(e => e.Name == "chips")).Chips);
    }

    [Fact]
    public void AProfileSwitch_LetsGoOfEveryUpload_AndOneArrivingAcrossIt_Is409()
    {
        Held(PageA);
        long epoch = _session.AttachEpoch;
        _session.ForgetAttachments();
        Assert.Empty(_session.ChipsOf(PageA));
        var late = _session.Attach(PageA, PdfBody(), epoch, null);
        Assert.Equal((409, WebUiText.AttachProfileSwitched), (late.Status, late.Message));
        Assert.Equal(0, _session.HeldCount);
        Assert.Equal(200, _session.Attach(PageA, PdfBody(), _session.AttachEpoch, null).Status);
    }

    [Fact]
    public void ASend_TakesItsOwnPagesIds_AnotherPagesIsSkipped_AndAPictureAloneGoes()
    {
        var mine = Held(PageA);
        var theirs = Held(PageB);
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = "", Page = PageA, Attachments = [mine.Id, theirs.Id] }, null).Outcome);
        Assert.Equal(1, _session.WaitingLines.Single().Pictures);   // the Queue tab counts a picture-only line's picture (the review's #4)
        Assert.True(_session.TryTakeLine(out var line));
        var blanks = Held(PageA);
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = " \n ", Page = PageA, Attachments = [blanks.Id] }, null).Outcome);
        Assert.True(_session.TryTakeLine(out var blankLine));
        Assert.Equal("", blankLine.Text);   // blanks with a picture are no text at all (the review's #77)
        Assert.Equal([mine.Id], line.Attachments!.Select(a => a.Id));
        Assert.Empty(_session.ChipsOf(PageA));
        Assert.Equal([theirs.Id], _session.ChipsOf(PageB).Select(c => c.Id));   // B's chip stays held

        var sneaky = _session.Send(new WebSendRequest { Text = "hi", Page = PageA, Attachments = [theirs.Id] }, null);
        Assert.Equal((WebOutcome.Ok, WebUiText.AttachExpired), (sneaky.Outcome, sneaky.Message));
        Assert.True(_session.TryTakeLine(out var bare));
        Assert.Null(bare.Attachments);
        Assert.Equal([theirs.Id], _session.ChipsOf(PageB).Select(c => c.Id));
    }

    [Fact]
    public void AnExpiredPictureAlone_IsRefused_AndACommandWithAttachments_Too()
    {
        var reply = _session.Send(new WebSendRequest { Id = "x1", Text = " ", Page = PageA, Attachments = ["gone"] }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.AttachExpired), (reply.Outcome, reply.Message));
        Assert.False(_session.LinesReady);

        var chip = Held(PageA);
        _session.CommandSettings = () => CommandsOn;
        var command = _session.Send(new WebSendRequest { Text = "/clear", Page = PageA, Attachments = [chip.Id] }, null);
        Assert.Equal((WebOutcome.Refused, WebUiText.AttachCommand), (command.Outcome, command.Message));
        Assert.Equal([chip.Id], _session.ChipsOf(PageA).Select(c => c.Id));
        Assert.Equal(WebOutcome.Ok, _session.Send(new WebSendRequest { Text = "/clear", AsText = true, Page = PageA, Attachments = [chip.Id] }, null).Outcome);
    }

    [Fact]
    public void AFullQueue_LeavesTheUploadsHeld()
    {
        for (int i = 0; i < WebUiSession.MaxWaiting; i++)
        {
            _session.Send(new WebSendRequest { Text = "line " + i }, null);
        }

        var chip = Held(PageA);
        Assert.Equal(WebOutcome.TooMany, _session.Send(new WebSendRequest { Text = "more", Page = PageA, Attachments = [chip.Id] }, null).Outcome);
        Assert.Equal([chip.Id], _session.ChipsOf(PageA).Select(c => c.Id));
    }

    [Fact]
    public void AWithdrawnLine_GivesItsChipsBack_UnderItsOwnPage_WithItsSavedPath()
    {
        var chip = Held(PageA);
        _session.Send(new WebSendRequest { Text = "what's this?", Page = PageA, Attachments = [chip.Id] }, null);
        Assert.True(_session.TryTakeLine(out var line));
        var saved = line.Attachments!.Select(a => a with { Saved = @"C:\x\phone.pdf" }).ToList();
        using var other = _session.Events.Subscribe(PageB, "loadBBBBBBBBBBBBBBBBBB");
        using var mine = _session.Events.Subscribe(PageA, "loadAAAAAAAAAAAAAAAAAA");
        _session.Withdrawn("what's this?", saved);

        Assert.Equal([chip.Id], _session.ChipsOf(PageA).Select(c => c.Id));
        var aEvents = Drain(mine);
        Assert.Equal(chip.Id, ChipsIn(aEvents.Single(e => e.Name == "chips")).Chips.Single().Id);
        var back = JsonSerializer.Deserialize(aEvents.Single(e => e.Name == "withdrawn").Json.Value, WebUiJsonContext.Default.WebTextBack)!;
        Assert.Equal(("what's this?", chip.Id), (back.Text, back.Chips!.Single().Id));
        Assert.DoesNotContain(Drain(other), e => e.Name == "chips");   // the text is broadcast; the row only to its own page

        // Sent again: the record keeps its saved file.
        _session.Send(new WebSendRequest { Text = "again", Page = PageA, Attachments = [chip.Id] }, null);
        Assert.True(_session.TryTakeLine(out var again));
        Assert.Equal(@"C:\x\phone.pdf", again.Attachments!.Single().Saved);
    }

    [Fact]
    public void DroppedLines_GiveTheirChipsBack_ButNotOnAProfileSwitch()
    {
        var chip = Held(PageA);
        _session.Send(new WebSendRequest { Text = "one", Page = PageA, Attachments = [chip.Id] }, null);
        Assert.Equal(1, _session.DropLines());
        Assert.Equal([chip.Id], _session.ChipsOf(PageA).Select(c => c.Id));

        _session.Send(new WebSendRequest { Text = "two", Page = PageA, Attachments = [chip.Id] }, null);
        Assert.Equal(1, _session.DropLines(giveBack: false));
        _session.ForgetAttachments();
        Assert.Equal(0, _session.HeldCount);
    }

    [Fact]
    public void TheWaitingLinesAttachments_CountTowardTheBytesInAll()
    {
        var first = Held(PageA, PdfBody(20_000_000));
        var second = Held(PageA, PdfBody(20_000_000));
        _session.Send(new WebSendRequest { Text = "go", Page = PageA, Attachments = [first.Id, second.Id] }, null);
        Held(PageA, PdfBody(20_000_000));
        Assert.Equal(507, _session.Attach(PageA, PdfBody(), 0, null).Status);
    }

    [Fact]
    public void AGiveBackPastTheCap_LetsTheOldestGo_AndSaysSo()
    {
        var sent = Held(PageA);
        _session.Send(new WebSendRequest { Text = "go", Page = PageA, Attachments = [sent.Id] }, null);
        Assert.True(_session.TryTakeLine(out var line));
        for (int i = 0; i < WebUiSession.MaxHeldPerPage; i++)
        {
            Held(PageA);
        }

        using var mine = _session.Events.Subscribe(PageA, "loadAAAAAAAAAAAAAAAAAA");
        _session.Withdrawn("go", line.Attachments);
        Assert.Equal(WebUiSession.MaxHeldPerPage, _session.ChipsOf(PageA).Count);
        Assert.Contains(sent.Id, _session.ChipsOf(PageA).Select(c => c.Id));
        Assert.Equal(WebUiText.AttachTrimmed, ChipsIn(Drain(mine).Single(e => e.Name == "chips")).Message);
    }

    [Fact]
    public void AGiveBackAcrossAProfileSwitch_IsDropped()
    {
        // 2026-11-16, the review's #5: a line taken under the last profile hands nothing back under the new one.
        var sent = Held(PageA);
        _session.Send(new WebSendRequest { Text = "go", Page = PageA, Attachments = [sent.Id] }, null);
        Assert.True(_session.TryTakeLine(out var line));
        _session.ForgetAttachments();

        _session.Withdrawn("go", line.Attachments);

        Assert.Equal(0, _session.HeldCount);
    }

    [Fact]
    public void AGiveBackPastTheBytes_LetsGoOfItsOwnPagesUploads_NeverAnotherPages()
    {
        // 2026-11-16, the review's #46: page B's uploads stay; page A's oldest goes to make room for its own give-back.
        Held(PageB, PdfBody(20_000_000));
        Held(PageB, PdfBody(20_000_000));
        var sent = Held(PageA, PdfBody(20_000_000));
        _session.Send(new WebSendRequest { Text = "go", Page = PageA, Attachments = [sent.Id] }, null);
        Assert.True(_session.TryTakeLine(out var line));
        var older = Held(PageA, PdfBody(15_000_000));

        _session.Withdrawn("go", line.Attachments);

        Assert.Equal(2, _session.ChipsOf(PageB).Count);
        Assert.Equal([sent.Id], _session.ChipsOf(PageA).Select(c => c.Id));
        Assert.DoesNotContain(older.Id, _session.ChipsOf(PageA).Select(c => c.Id));
    }

    [Fact]
    public void AFifthUpload_IsRefusedBeforeItsBytesAreRead()
    {
        // 2026-11-16, the review's #41: the page's count is checked first, so bytes that aren't a picture at all get the 409, not a 415.
        for (int i = 0; i < WebUiSession.MaxHeldPerPage; i++)
        {
            Held(PageA);
        }

        Assert.Equal((409, WebUiText.AttachFull), Refusal([1, 2, 3, 4]));
    }

    [Fact]
    public void AChipsForget_LetsGoOfItsOwnPagesOnly()
    {
        var chip = Held(PageA);
        Assert.Equal(WebOutcome.Ok, _session.ForgetAttachment(new WebAttachForget { Id = chip.Id, Page = PageB }, null).Outcome);
        Assert.Single(_session.ChipsOf(PageA));
        Assert.Equal(WebOutcome.Ok, _session.ForgetAttachment(new WebAttachForget { Id = chip.Id, Page = PageA }, null).Outcome);
        Assert.Empty(_session.ChipsOf(PageA));
        Assert.Equal(WebOutcome.Ok, _session.ForgetAttachment(new WebAttachForget { Id = chip.Id, Page = PageA }, null).Outcome);   // quiet
    }

    [Fact]
    public void TheAttachShapes_AreSentInTheNamesThePageReads()
    {
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(new WebAttachResult(true, Chip: new WebChip("i", "picture", "2 MB", "data:x")), WebUiJsonContext.Default.WebAttachResult));
        Assert.Equal(["ok", "chip"], result.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "kind", "size", "thumb"], result.RootElement.GetProperty("chip").EnumerateObject().Select(p => p.Name));
        var send = JsonSerializer.Deserialize("{\"text\":\"x\",\"attachments\":[\"a\",\"b\"]}", WebUiJsonContext.Default.WebSendRequest)!;
        Assert.Equal(["a", "b"], send.Attachments!);
        var start = JsonSerializer.Deserialize("{\"page\":\"p\",\"attachments\":[\"a\"]}", WebUiJsonContext.Default.WebVoiceStart)!;
        Assert.Equal(["a"], start.Attachments!);
        Assert.Equal(WebUiText.AttachButton, WebUiText.Words.AttachButton);
        Assert.Equal(WebUiText.AttachRoom, WebUiText.Words.AttachRoom);
    }
}
