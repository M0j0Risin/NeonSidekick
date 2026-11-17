using NeonSidekick.Files;
using NeonSidekick.Render;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// The real browser's draws (2026-11-16, plan sharded-hugging-puppy #9, <see cref="HeadlessBrowser.RenderPictureAsync"/>): a Mermaid flowchart,
/// Mermaid's complaint about bad text, the model's SVG, a page whose script never runs, and a web picture never fetched. Each returns early
/// on a machine with no browser, as <see cref="BrowserPdfReaderTests"/>' live ones do.
/// </summary>
public sealed class PictureRenderTests : IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly string _profile = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "render-" + Guid.NewGuid().ToString("N"));
    private readonly HeadlessBrowser _browser;

    public PictureRenderTests()
    {
        _browser = new HeadlessBrowser(_profile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_profile, recursive: true); } catch { /* best effort */ }
    }

    private async Task<PictureRender?> DrawAsync(RenderJob job)
    {
        if (_browser.Locate("") is not { } executable)
        {
            return null;   // no browser installed: nothing to prove here
        }

        return await _browser.RenderPictureAsync(executable, job, CancellationToken.None);
    }

    private static void IsAPicture(PictureRender drawn, int minWidth)
    {
        Assert.True(drawn.Ok, drawn.Detail);
        Assert.Equal(PngSignature, drawn.Bytes![..8]);
        Assert.True(drawn.Width >= minWidth, $"{drawn.Width}×{drawn.Height}");
        Assert.True(ImageFile.TryLoad(drawn.Bytes, "drawn.png", out var image, out _) && image is not null);
        Assert.True(drawn.Bytes.Length > 1000, "a blank picture: " + drawn.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AMermaidFlowchart_IsDrawn()
    {
        if (await DrawAsync(new RenderJob("graph TD\n  A[Start] --> B{Ready?}\n  B -->|yes| C[Go]\n  B -->|no| D[Wait]", null, PictureFormat.Png, 1200, 2, "#FFFFFF")) is not { } drawn)
        {
            return;
        }

        IsAPicture(drawn, 100);
        Assert.False(drawn.Cut);
    }

    [Fact]
    public async Task BadMermaid_ComesBackAsItsComplaint()
    {
        if (await DrawAsync(new RenderJob("graph TD\n  A --> \n  ((((", null, PictureFormat.Png, 1200, 2, "#FFFFFF")) is not { } drawn)
        {
            return;
        }

        Assert.False(drawn.Ok);
        Assert.False(string.IsNullOrWhiteSpace(drawn.MermaidError), drawn.Detail);
    }

    [Fact]
    public async Task TheModelsSvg_IsDrawn_OnATransparentGround()
    {
        string page = PicturePage.ForModelSvg("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"300\" height=\"200\"><rect width=\"300\" height=\"200\" fill=\"#3366CC\"/><text x=\"20\" y=\"100\" font-size=\"40\" fill=\"#fff\">Hello</text></svg>", _ => null);
        if (await DrawAsync(new RenderJob(null, page, PictureFormat.Png, 1200, 1, null)) is not { } drawn)
        {
            return;
        }

        IsAPicture(drawn, 300);
        Assert.Equal(300 + (2 * PicturePage.Pad), drawn.Width);
        Assert.Equal(200 + (2 * PicturePage.Pad), drawn.Height);
    }

    [Fact]
    public async Task APagesScript_NeverRuns_AndAWebPicture_IsNeverFetched()
    {
        // Were the script to run (past the rebuild and the policy) the page would grow to 3000 pixels tall.
        string page = PicturePage.ForModelHtml(
            "<div style=\"height:120px;background:#C33\">Card</div><img src=\"https://example.com/missing.png\" width=\"50\" height=\"50\">" +
            "<script>document.body.innerHTML = '<div style=\"height:3000px\"></div>';</script>", _ => null);
        if (await DrawAsync(new RenderJob(null, page, PictureFormat.Png, 600, 1, "#FFFFFF")) is not { } drawn)
        {
            return;
        }

        Assert.True(drawn.Ok, drawn.Detail);
        Assert.True(drawn.Height < 400, drawn.Height.ToString(System.Globalization.CultureInfo.InvariantCulture));   // the card's own height, not the viewport's
        Assert.Equal(600, drawn.Width);
    }

    [Fact]
    public async Task AChart_IsDrawn_AsAJpegWhenAsked()
    {
        var table = new ChartTable();
        Office.TableSource.TextTable("month,sales\nJan,10\nFeb,14\nMar,9", "data", table, CancellationToken.None);
        string svg = ChartSvg.Svg(new ChartSpec(ChartKind.Bar, Width: 600), table.Data("month", ["sales"], out _)!, out _);
        if (await DrawAsync(new RenderJob(null, PicturePage.ForTrustedSvg(svg), PictureFormat.Jpeg, 632, 1, ChartSvg.LightGround)) is not { } drawn)
        {
            return;
        }

        Assert.True(drawn.Ok, drawn.Detail);
        Assert.Equal([0xFF, 0xD8], drawn.Bytes![..2]);
        Assert.Equal(632, drawn.Width);
    }
}
