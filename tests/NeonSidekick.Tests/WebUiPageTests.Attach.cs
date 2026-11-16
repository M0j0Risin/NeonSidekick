using System.Text.Json;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

/// <summary>
/// The page's 📎 in a real browser (2026-11-16, plan lucid-roaming-lantern #39): a pick uploads with a chip, one request at a time, and ➤ sends its
/// id; a file over the cap is refused on the page with no request; a refused upload drops its chip and says why; a 429 waits its Retry-After and
/// lands on the next try; the snapshot's row rebuilds a reloaded page's chips; <c>imageSize</c> reads each header (a JPEG's orientation turning
/// it upright); a photo past the pixel cap goes as an upright JPEG of 4096 px; a HEIC the browser decodes goes as a JPEG; the row fits 360 px; a
/// chip wears the theme's line color. A file is picked with no helper of the page's: a <c>File</c> in a <c>DataTransfer</c>, set as the input's
/// files, then a <c>change</c> event.
/// </summary>
public sealed partial class WebUiPageTests
{
    /// <summary>
    /// <c>XMLHttpRequest</c> wrapped (the <c>FakeMic</c> pattern): uploads counted, the most open at once recorded, and while <c>__fail429</c> is
    /// above 0 an upload answered with a 429 and <c>Retry-After: 1</c> without reaching the app.
    /// </summary>
    private const string FakeUpload = """
        (() => {
          const Real = window.XMLHttpRequest;
          const u = { count: 0, open: 0, most: 0 };
          window.__uploads = u;
          window.__fail429 = 0;
          window.XMLHttpRequest = function () {
            const x = new Real();
            let url = '';
            const open = x.open.bind(x);
            x.open = (method, path, ...rest) => { url = path; return open(method, path, ...rest); };
            const send = x.send.bind(x);
            x.send = body => {
              if (!url.startsWith('/api/attach?')) return send(body);
              u.count++;
              if (window.__fail429 > 0) {
                window.__fail429--;
                Object.defineProperty(x, 'status', { value: 429 });
                Object.defineProperty(x, 'responseText', { value: '{"ok":false}' });
                x.getResponseHeader = name => name.toLowerCase() === 'retry-after' ? '1' : null;
                setTimeout(() => x.onload && x.onload(), 10);
                return;
              }
              u.open++;
              u.most = Math.max(u.most, u.open);
              let done = false;
              x.addEventListener('readystatechange', () => { if (x.readyState === 4 && !done) { done = true; u.open--; } });   // before load, so the next upload's send comes after
              return send(body);
            };
            return x;
          };
        })();
        """;

    private async Task<HeadlessPage?> AttachPageAsync(long maxBytes = ImageFile.MaxFileBytes, IReadOnlyList<WebChip>? chips = null, int width = 390, string? init = null)
    {
        if (_page is null)
        {
            return null;
        }

        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, Chips: chips, AttachMaxBytes: maxBytes, AttachMaxPixels: ImageFile.MaxPixels);
        await _page.OnNewDocumentAsync(FakeUpload + (init ?? ""));
        var page = await SignedInAsync("/", width);
        await page!.WaitAsync("words.attachButton === " + JsonSerializer.Serialize(WebUiText.AttachButton), "the snapshot's words");
        return page;
    }

    private static string Pick(params (byte[] Bytes, string Name, string Type)[] files) =>
        "(() => { const dt = new DataTransfer(); "
        + string.Concat(files.Select(f => "dt.items.add(new File([Uint8Array.from(atob('" + Convert.ToBase64String(f.Bytes) + "'), c => c.charCodeAt(0))], "
            + JsonSerializer.Serialize(f.Name) + ", { type: " + JsonSerializer.Serialize(f.Type) + " })); "))
        + "const input = document.getElementById('attach-input'); input.files = dt.files; input.dispatchEvent(new Event('change')); })()";

    private const string Message = "document.getElementById('message').textContent";

    [WindowsFact]
    public async Task APickedPicture_UploadsWithAChip_AndTheSendCarriesItsId()
    {
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        byte[] jpeg = ImageFixtures.ExifJpeg();
        await page.EvalAsync(Pick((jpeg, "a.jpg", "image/jpeg")));
        await page.WaitAsync("chips.length === 1 && chips[0].id !== null && document.querySelectorAll('#draft-chips .draft-chip').length === 1", "the chip landed");
        Assert.Equal(jpeg.Length, _backend.Attached.Single().Bytes);
        Assert.Equal("false", await page.TextAsync("String(document.getElementById('draft-chips').classList.contains('hidden'))"));
        string id = await page.TextAsync("chips[0].id");

        await page.EvalAsync("draft.value = \"what's this?\"; document.getElementById('send').click()");
        Assert.True(SpinWait.SpinUntil(() => !_backend.Sent.IsEmpty, TimeSpan.FromSeconds(10)), "the send never came");
        var sent = _backend.Sent.Single();
        Assert.Equal(("what's this?", id), (sent.Text, Assert.Single(sent.Attachments!)));
        await page.WaitAsync("chips.length === 0 && document.getElementById('draft-chips').classList.contains('hidden')", "the row emptied");
    }

    [WindowsFact]
    public async Task ASendRightAfterAPick_WaitsForTheUpload_AndCarriesItsId()
    {
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        await page.EvalAsync(Pick((ImageFixtures.ExifJpeg(), "a.jpg", "image/jpeg")) + "; draft.value = 'now'; document.getElementById('send').click()");
        Assert.True(SpinWait.SpinUntil(() => !_backend.Sent.IsEmpty, TimeSpan.FromSeconds(10)), "the send never came");
        Assert.Single(_backend.Sent.Single().Attachments!);
    }

    [WindowsFact]
    public async Task ThreePicked_GoOneAtATime_AndAFifthIsNotAdded()
    {
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        byte[] pdf = "%PDF-1.4\n"u8.ToArray();
        await page.EvalAsync(Pick((pdf, "1.pdf", "application/pdf"), (pdf, "2.pdf", "application/pdf"), (pdf, "3.pdf", "application/pdf")));
        await page.WaitAsync("chips.length === 3 && chips.every(c => c.id)", "three chips landed");
        Assert.Equal(1, (await page.EvalAsync("window.__uploads.most")).GetInt32());
        Assert.Equal(3, _backend.Attached.Count);
        Assert.Equal("false", await page.TextAsync("String(document.getElementById('attach').classList.contains('hidden'))"));

        await page.EvalAsync(Pick((pdf, "4.pdf", "application/pdf"), (pdf, "5.pdf", "application/pdf")));
        await page.WaitAsync("chips.length === 4 && chips.every(c => c.id)", "the fourth landed");
        Assert.Equal(WebUiText.AttachRoom.Replace("{0}", "1", StringComparison.Ordinal), await page.TextAsync(Message));
        Assert.Equal("true", await page.TextAsync("String(document.getElementById('attach').classList.contains('hidden'))"));
    }

    [WindowsFact]
    public async Task AFileOverTheCap_IsRefusedOnThePage_WithNoRequest()
    {
        if (await AttachPageAsync(maxBytes: 10) is not { } page)
        {
            return;
        }

        await page.EvalAsync(Pick((new byte[100], "big.pdf", "application/pdf")));
        await page.WaitAsync(Message + " === " + JsonSerializer.Serialize(WebUiText.AttachTooBig), "the page's words");
        Assert.Equal(0, (await page.EvalAsync("window.__uploads.count")).GetInt32());
        Assert.Equal(0, (await page.EvalAsync("chips.length")).GetInt32());
    }

    [WindowsFact]
    public async Task ARefusedUpload_DropsItsChip_AndSaysWhy()
    {
        _backend.AttachReply = (_, _) => new WebAttachReply(422, WebUiText.AttachHeic, null);
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        await page.EvalAsync(Pick(([1, 2, 3], "a.jpg", "image/jpeg")));
        await page.WaitAsync(Message + " === " + JsonSerializer.Serialize(WebUiText.AttachHeic), "the app's words");
        Assert.Equal(0, (await page.EvalAsync("chips.length")).GetInt32());
    }

    [WindowsFact]
    public async Task A429_WaitsItsRetryAfter_AndTheChipLandsOnTheNextTry()
    {
        if (await AttachPageAsync(init: "window.__fail429 = 1;") is not { } page)
        {
            return;
        }

        await page.EvalAsync("window.__fail429 = 1;" + Pick(("%PDF-1.4\n"u8.ToArray(), "a.pdf", "application/pdf")));
        await page.WaitAsync("chips.length === 1 && chips[0].state === 'waiting'", "the chip waiting");
        await page.WaitAsync("chips.length === 1 && chips[0].id !== null", "the chip landed", seconds: 10);
        Assert.Equal(2, (await page.EvalAsync("window.__uploads.count")).GetInt32());
        Assert.Single(_backend.Attached);
    }

    [WindowsFact]
    public async Task AReloadedPage_RebuildsItsRow_FromTheSnapshot_AndAChipsEventReplacesIt()
    {
        var held = new WebChip("held1", "pdf", "3 KB");
        if (await AttachPageAsync(chips: [held]) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('#draft-chips .draft-chip[data-id=\"held1\"]') !== null", "the held chip");
        Assert.Equal(WebUiText.AttachPdf + " 3 KB", await page.TextAsync("document.querySelector('#draft-chips .chip-label').textContent"));

        PublishWhenStreaming(new WebEvent("chips", () => JsonSerializer.Serialize(new WebChips([new WebChip("next", "picture", "2 MB")], WebUiText.AttachTrimmed), WebUiJsonContext.Default.WebChips)));
        await page.WaitAsync("chips.length === 1 && chips[0].id === 'next'", "the event's row");
        Assert.Equal(WebUiText.AttachTrimmed, await page.TextAsync(Message));

        await page.EvalAsync("document.querySelector('#draft-chips .chip-remove').click()");
        Assert.True(SpinWait.SpinUntil(() => !_backend.Forgotten.IsEmpty, TimeSpan.FromSeconds(10)), "the ✕ never told the app");
        Assert.Equal("next", _backend.Forgotten.Single().Id);
    }

    [WindowsFact]
    public async Task ImageSize_ReadsEachHeader_AndAJpegsOrientationTurnsIt()
    {
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        async Task<string> SizeOf(byte[] bytes) =>
            await page.TextAsync("JSON.stringify(imageSize(Uint8Array.from(atob('" + Convert.ToBase64String(bytes) + "'), c => c.charCodeAt(0))))");

        Assert.Equal("{\"width\":20,\"height\":40}", await SizeOf(ImageFixtures.SidewaysJpeg(40, 20, 6)));
        Assert.Equal("{\"width\":40,\"height\":20}", await SizeOf(ImageFixtures.SidewaysJpeg(40, 20, 1)));
        Assert.Equal("{\"width\":5,\"height\":3}", await SizeOf(ImageFileTests.BlackPng(5, 3)));
        Assert.Equal("{\"width\":300,\"height\":200}", await SizeOf(Webp("VP8X", [0, 0, 0, 0, 43, 1, 0, 199, 0, 0])));
        int bits = (300 - 1) | ((200 - 1) << 14);
        Assert.Equal("{\"width\":300,\"height\":200}", await SizeOf(Webp("VP8L", [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), 0, 0, 0, 0, 0])));
        Assert.Equal("{\"width\":300,\"height\":200}", await SizeOf(Webp("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, 44, 1, 200, 0])));
        Assert.Equal("null", await SizeOf(ImageFixtures.SidewaysJpeg(40, 20, 6)[..12]));
        Assert.Equal("null", await SizeOf("not a picture"u8.ToArray()));

        static byte[] Webp(string kind, byte[] chunk) =>
            [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray(), .. System.Text.Encoding.ASCII.GetBytes(kind), 10, 0, 0, 0, .. chunk];
    }

    /// <summary>A photo past the pixel cap stored sideways (orientation 6): decoded straight to the smaller size, upright, as a 4096 px JPEG.</summary>
    [WindowsFact]
    public async Task APhotoPastThePixelCap_GoesAsAnUprightJpeg_Of4096()
    {
        if (await AttachPageAsync() is not { } page)
        {
            return;
        }

        await page.EvalAsync(Pick((ImageFixtures.SidewaysJpeg(8000, 6000, 6), "big.jpg", "image/jpeg")));
        await page.WaitAsync("chips.length === 1 && chips[0].id !== null", "the shrunk photo landed", seconds: 30);
        var frame = ImageFileInfo.Load(_backend.Bodies.Single()).Frames[0];
        Assert.Equal((3072, 4096), (frame.Width, frame.Height));
    }

    /// <summary>A HEIC the browser decodes (stubbed: Edge has no HEIC decoder) goes as a JPEG; over the cap after its shrink, refused with no request.</summary>
    [WindowsFact]
    public async Task AHeicTheBrowserDecodes_GoesAsAJpeg_AndOverTheCap_IsRefusedOnThePage()
    {
        const string Decodes = """
            (() => {
              const real = window.createImageBitmap;
              window.createImageBitmap = (src, opts) => {
                if (src instanceof Blob && src.type === 'image/heic') {
                  const c = document.createElement('canvas');
                  c.width = 64; c.height = 48;
                  const g = c.getContext('2d'); g.fillStyle = 'red'; g.fillRect(0, 0, 64, 48);
                  return real(c);
                }
                return real(src, opts);
              };
            })();
            """;
        if (await AttachPageAsync(init: Decodes) is not { } page)
        {
            return;
        }

        await page.EvalAsync(Pick(([0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 1, 2, 3, 4], "a.heic", "image/heic")));
        await page.WaitAsync("chips.length === 1 && chips[0].id !== null", "the JPEG landed");
        Assert.True(_backend.Bodies.Single().AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]));

        await page.EvalAsync("attachMaxBytes = 100;" + Pick(([0, 0, 0, 24, .. "ftypheic"u8.ToArray(), 1, 2, 3, 4], "b.heic", "image/heic")));
        await page.WaitAsync(Message + " === " + JsonSerializer.Serialize(WebUiText.AttachTooBig), "refused on the page");
        Assert.Single(_backend.Bodies);
    }

    [WindowsFact]
    public async Task TheComposeRow_WithItsClipAndFourChips_FitsAPhone_AndAChipWearsTheThemesLine()
    {
        _backend.Css = StyledSheet;
        WebChip[] four = [new("c1", "pdf", "3 KB"), new("c2", "picture", "2.1 MB"), new("c3", "picture", "12.5 MB"), new("c4", "pdf", "340 KB")];
        if (await AttachPageAsync(chips: four, width: 360) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('#draft-chips .draft-chip').length === 4", "four chips");
        await page.WaitAsync("document.documentElement.scrollWidth <= window.innerWidth", "no sideways scroll at 360 px");
        Assert.Equal("rgb(13, 14, 15)", await ComputedAsync(page, "#draft-chips .draft-chip", "borderTopColor"));
        Assert.Equal("true", await page.TextAsync("String(document.getElementById('attach').classList.contains('hidden'))"));   // four held: no 📎
        Assert.Equal("image/jpeg,image/png,image/webp,image/gif,application/pdf", await page.TextAsync("document.getElementById('attach-input').accept"));
    }
}
