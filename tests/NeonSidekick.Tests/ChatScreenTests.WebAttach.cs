using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Images;
using NeonSidekick.Llm;
using NeonSidekick.Pdf;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Pictures and PDFs from the phone on the screen (2026-11-16, plan lucid-roaming-lantern #39): a held upload sent with a line reaches the turn
/// (a picture as an image part, a PDF with its text), saved once under <i>Pasted images folder</i> as <c>phone-…</c> without its GPS, its
/// orientation kept; the model's text names the saved file, the › row and the page's feed read the bare label; a folder that refuses the write
/// sends the picture all the same and leaves a PDF off, the page told in its own words with no PC path; a withdrawn line gives its chip back and
/// its resend saves no second file; a PDF the send-time check refuses goes back to the page's draft with its chip.
/// </summary>
public partial class ChatScreenTests
{
    private const string AttachingPage = "pageAAAAAAAAAAAAAAAAAA";

    private WebChip PhoneUpload(byte[] body)
    {
        var reply = Web.Attach(AttachingPage, body, Web.AttachEpoch, null);
        Assert.Equal(200, reply.Status);
        return reply.Chip!;
    }

    private WebReply PhoneSend(string text, params WebChip[] chips) =>
        Web.Send(new WebSendRequest { Text = text, Page = AttachingPage, Attachments = [.. chips.Select(c => c.Id)] }, null);

    private static byte[] PhonePdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n% from the phone\n%%EOF\n");

    [Fact]
    public async Task APhonePicture_ReachesTheTurn_SavedWithoutItsGps_ItsLabelNamingTheFile()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("A test card.");
        StepsWhenIdle(
            i =>
            {
                Assert.Equal(WebOutcome.Ok, PhoneSend("what's this?", PhoneUpload(ImageFixtures.ExifJpeg())).Outcome);
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync();

        string saved = Assert.Single(Directory.GetFiles(PastedFolder));
        Assert.Matches(@"^phone-\d{8}-\d{6}\.jpg$", Path.GetFileName(saved));
        Assert.False(ImageFixtures.HasGps(File.ReadAllBytes(saved)));
        var sent = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal(PasteBlocks.SavedLabel(1, saved) + " what's this?", sent.Text);
        var part = Assert.Single(sent.Contents.OfType<DataContent>());
        Assert.False(ImageFixtures.HasGps(part.Data.ToArray()));
        Assert.Contains("[Image #1] what's this?", output);
        Assert.Contains(Web.Feed.Snapshot(m => m), e => e.Kind == "user" && e.Text == "[Image #1] what's this?");
        Assert.DoesNotContain(Web.Feed.Snapshot(m => m), e => e.Text.Contains(saved, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Web.ChipsOf(AttachingPage));
    }

    [Fact]
    public async Task APhonesPortraitPhoto_IsSavedWithItsOrientation()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Upright.");
        StepsWhenIdle(
            i => PhoneSend("", PhoneUpload(ImageFixtures.SidewaysJpeg(40, 20, 6))),
            i => PushLine(i, "/exit"));

        await RunAsync();

        Assert.Equal(6, MetadataStripper.ExifOrientation(File.ReadAllBytes(Assert.Single(Directory.GetFiles(PastedFolder)))));
        Assert.Equal(PasteBlocks.SavedLabel(1, Assert.Single(Directory.GetFiles(PastedFolder))), LastUser(_chat.Requests[0]));
    }

    [Fact]
    public async Task APhonePdf_ReachesTheTurn_WithItsText_SavedAsItCame()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("It is a manual.");
        BrowserReads("The warranty lasts two years.");
        StepsWhenIdle(
            i => PhoneSend("read it", PhoneUpload(PhonePdf())),
            i => PushLine(i, "/exit"));

        string output = await RunAsync();

        string saved = Assert.Single(Directory.GetFiles(PastedFolder));
        Assert.Matches(@"^phone-\d{8}-\d{6}\.pdf$", Path.GetFileName(saved));
        Assert.Equal(PhonePdf(), File.ReadAllBytes(saved));
        var sent = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal(PdfText.DocumentSentLabel(1, saved) + " read it", sent.Text);
        Assert.Contains("The warranty lasts two years.", ConversationHistory.DocumentText(Assert.Single(sent.Contents.OfType<DataContent>()))!, StringComparison.Ordinal);
        Assert.Contains(PdfText.DocumentLabel(1, Path.GetFileName(saved)) + " read it", output);
    }

    /// <summary>A save the folder refuses: the picture goes unnamed, the PDF is left off; the PC's warnings name why, the page's words no PC path.</summary>
    [Fact]
    public async Task AFolderThatRefusesTheWrite_SendsThePicture_LeavesThePdfOff_AndTellsThePageWithoutAPath()
    {
        _settings.Update(d => d.TtsOutput = false);
        Directory.CreateDirectory(_settings.ProfileImagesDirectory);
        File.WriteAllText(PastedFolder, "in the way");
        _chat.EnqueueText("A test card.");
        WebEventHub.Subscription? page = null;
        StepsWhenIdle(
            i =>
            {
                page = Web.Events.Subscribe(AttachingPage, "loadAAAAAAAAAAAAAAAAAA");
                PhoneSend("both", PhoneUpload(ImageFixtures.ExifJpeg()), PhoneUpload(PhonePdf()));
            },
            i => PushLine(i, "/exit"));

        string output = await RunAsync();

        var sent = _chat.Requests[0].Last(m => m.Role == ChatRole.User);
        Assert.Equal("[Image #1] both", sent.Text);
        Assert.Single(sent.Contents.OfType<DataContent>());
        Assert.Contains("[Image #1] was not saved (", output);
        Assert.Contains("[PDF #2] from the phone was not saved (", output);
        var said = new List<string>();
        while (page!.Reader.TryRead(out var item))
        {
            if (item.Name == "chips" && JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebChips)!.Message is { } message)
            {
                said.Add(message);
            }
        }

        Assert.Equal([WebUiText.PhoneNotSaved(1, pdf: false), WebUiText.PhoneNotSaved(2, pdf: true)], said);
        Assert.All(said, s => Assert.DoesNotContain(_settings.ProfileImagesDirectory, s, StringComparison.OrdinalIgnoreCase));
        page.Dispose();
    }

    [Fact]
    public async Task AWithdrawnPhoneLine_GivesItsChipBack_AndItsResend_SavesNoSecondFile()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("never shown");
        _chat.EnqueueText("A test card.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0)
            {
                Assert.Equal(WebOutcome.Ok, Web.Stop(null).Outcome);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);   // the Stop ends it long before
            }
        };
        WebChip? chip = null;
        IReadOnlyList<WebChip>? back = null;
        StepsWhenIdle(
            i => PhoneSend("what's this?", chip = PhoneUpload(ImageFixtures.ExifJpeg())),
            i =>
            {
                back = Web.ChipsOf(AttachingPage);
                PhoneSend("again", chip!);
            },
            i => PushLine(i, "/exit"));

        await RunAsync();

        Assert.Equal(chip!.Id, Assert.Single(back!).Id);
        string saved = Assert.Single(Directory.GetFiles(PastedFolder));
        Assert.Equal(PasteBlocks.SavedLabel(1, saved) + " again", LastUser(_chat.Requests[^1]));
    }

    /// <summary>A PDF the send-time check refuses: the page's line goes back to its draft with its chip, never onto the PC's row.</summary>
    [Fact]
    public async Task APhonePdfRefusedAtTheSend_GoesBackToThePagesDraft_WithItsChip()
    {
        _settings.Update(d => d.TtsOutput = false);
        BrowserReads("", "");
        WebEventHub.Subscription? subscription = null;
        WebChip? chip = null;
        IReadOnlyList<WebChip>? back = null;
        StepsWhenIdle(
            i =>
            {
                subscription = Web.Events.Subscribe();
                PhoneSend("read it", chip = PhoneUpload(PhonePdf()));
            },
            i =>
            {
                back = Web.ChipsOf(AttachingPage);
                PushLine(i, "/exit");
            });

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Equal([("withdrawn", "read it")], TextBacks(subscription!));
        Assert.Equal(chip!.Id, Assert.Single(back!).Id);
        subscription!.Dispose();
    }
}
