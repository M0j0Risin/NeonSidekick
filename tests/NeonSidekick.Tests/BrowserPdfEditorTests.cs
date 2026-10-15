using System.Text;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// Editing a PDF in the headless browser (2026-10-14, the PDF plan's phase 4b): pdf-lib embedded, the expressions the page runs and the
/// reading of their answers, and on the machine's own browser (skipped where none is installed, as <see cref="BrowserPdfReaderTests"/>'
/// are) every edit made for real and read back with pdf.js: a merge, a cut, a delete, a turn, a form filled and flattened, a locked
/// input, and an XFA-only form refused.
/// </summary>
public sealed class BrowserPdfEditorTests
{
    [Fact]
    public void TheLibrary_IsEmbedded_AsPublished()
    {
        string? library = BrowserPdfEditor.LibraryText();
        Assert.NotNull(library);
        Assert.StartsWith("!function(e,t){", library, StringComparison.Ordinal);
        Assert.Contains("PDFLib", library, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoad_EndsOnAnObject_SoTheEvaluateAnswers()
    {
        string load = BrowserPdfEditor.LoadExpression("var PDFLib = {};");
        Assert.StartsWith("var PDFLib = {};\n;({ v: ", load, StringComparison.Ordinal);
        using var ok = JsonDocument.Parse("{\"v\":\"ok\"}");
        using var missing = JsonDocument.Parse("{\"v\":\"missing\"}");
        Assert.True(BrowserPdfEditor.Loaded(ok.RootElement));
        Assert.False(BrowserPdfEditor.Loaded(missing.RootElement));
    }

    [Fact]
    public void TheRun_UsesZeroBasedIndices_AndTurnsWithinACircle()
    {
        Assert.Contains("copyPages(docs[0], [1,2,4])", BrowserPdfEditor.RunExpression(PdfEditOp.Extract, [2, 3, 5]), StringComparison.Ordinal);
        Assert.Contains("[0,2].sort((a, b) => b - a)", BrowserPdfEditor.RunExpression(PdfEditOp.Delete, [1, 3]), StringComparison.Ordinal);
        Assert.Contains("+ 270) % 360 + 360) % 360", BrowserPdfEditor.RunExpression(PdfEditOp.Rotate, [1], angle: 270), StringComparison.Ordinal);
        Assert.Contains("for (const d of docs)", BrowserPdfEditor.RunExpression(PdfEditOp.Merge, []), StringComparison.Ordinal);
        Assert.Contains("form.flatten();", BrowserPdfEditor.RunExpression(PdfEditOp.FillForm, [], fields: [], flatten: true), StringComparison.Ordinal);
        Assert.DoesNotContain("form.flatten();", BrowserPdfEditor.RunExpression(PdfEditOp.FillForm, [], fields: [], flatten: false), StringComparison.Ordinal);
    }

    /// <summary>The values go into the page as a literal: a quote, a closing script tag or a line break in one can't break out of it.</summary>
    [Fact]
    public void TheFields_AreAnEscapedLiteral()
    {
        using var doc = JsonDocument.Parse("{\"a\":\"it's </script>\\n\\\"x\\\"\",\"b\":true,\"c\":[\"red\",\"blue\"]}");
        var fields = doc.RootElement.EnumerateObject().Select(p => new PdfFieldValue(p.Name, p.Value.Clone())).ToList();

        string json = BrowserPdfEditor.FieldsJson(fields);

        Assert.DoesNotContain("'", json, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        using var back = JsonDocument.Parse(json);
        Assert.Equal("it's </script>\n\"x\"", back.RootElement.GetProperty("a").GetString());
        Assert.True(back.RootElement.GetProperty("b").GetBoolean());
        Assert.Equal(2, back.RootElement.GetProperty("c").GetArrayLength());
    }

    [Fact]
    public void TheRunsAnswers_SayWhatWasMade_OrWhyNot()
    {
        Assert.Equal((4, 1234, null), BrowserPdfEditor.ReadRun(Json("{\"n\":4,\"s\":1234}")));
        Assert.True(BrowserPdfEditor.ReadRun(Json("{\"xfa\":true}")).Refusal!.Xfa);
        Assert.True(BrowserPdfEditor.ReadRun(Json("{\"nofields\":true}")).Refusal!.NoFields);
        var unknown = BrowserPdfEditor.ReadRun(Json("{\"unknown\":[\"nmae\"],\"names\":[\"name\",\"agree\"]}")).Refusal!;
        Assert.Equal(["nmae"], unknown.UnknownFields);
        Assert.Equal(["name", "agree"], unknown.FieldNames);
        var bad = BrowserPdfEditor.ReadRun(Json("{\"bad\":[{\"n\":\"color\",\"o\":[\"red\",\"blue\"]},{\"n\":\"go\",\"e\":\"none\"},{\"n\":\"name\",\"e\":\"WinAnsi cannot encode\"}]}")).Refusal!;
        Assert.Equal(["color", "go", "name"], bad.BadFields.Select(f => f.Name));
        Assert.Equal(["red", "blue"], bad.BadFields[0].Options);
        Assert.True(bad.BadFields[1].TakesNone);
        Assert.Equal("WinAnsi cannot encode", bad.BadFields[2].Error);
    }

    [Fact]
    public void AChunk_IsItsBase64()
    {
        Assert.Equal(Encoding.ASCII.GetBytes("%PDF"), BrowserPdfEditor.ReadChunk(Json("{\"c\":\"JVBERg==\"}")));
        Assert.Null(BrowserPdfEditor.ReadChunk(Json("{\"c\":\"not base64!\"}")));
        Assert.Null(BrowserPdfEditor.ReadChunk(Json("{\"x\":1}")));
        Assert.Contains("subarray(8388608, 9388608)", BrowserPdfEditor.ChunkExpression(8_388_608, 1_000_000), StringComparison.Ordinal);
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── On the machine's own browser ──────────────────────────────────────────

    private static HeadlessBrowser LiveBrowser(out string? executable)
    {
        var browser = new HeadlessBrowser(Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pdfedit-" + Guid.NewGuid().ToString("N")));
        executable = browser.Locate("");
        return browser;
    }

    private static (IReadOnlyList<int>?, string?) All(int count) => ([.. Enumerable.Range(1, count)], null);

    private static Func<int, (IReadOnlyList<int>?, string?)> Just(params int[] pages) => _ => (pages, null);

    private static async Task<IReadOnlyList<string>> TextsOf(HeadlessBrowser browser, string executable, byte[] pdf)
    {
        var read = await browser.ReadPdfAsync(executable, pdf, new PdfReadOptions(Pictures: false), CancellationToken.None);
        Assert.True(read.Ok, read.Detail);
        return [.. read.Pages.Select(p => p.Text)];
    }

    [WindowsFact]
    public async Task TheMachinesBrowser_Merges_Extracts_Deletes_AndTurns()
    {
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;   // no browser installed: nothing to prove here
        }

        byte[] abc = BrowserPdfReaderTests.MiniPdf("one", "two", "three");
        byte[] de = BrowserPdfReaderTests.MiniPdf("four", "five");

        var merged = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Merge, [abc, de], All), CancellationToken.None);
        Assert.True(merged.Ok, merged.Detail);
        Assert.Equal(5, merged.PageCount);
        Assert.Equal(["one", "two", "three", "four", "five"], await TextsOf(browser, executable, merged.Output!));

        var extracted = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Extract, [abc], Just(3, 1)), CancellationToken.None);
        Assert.True(extracted.Ok, extracted.Detail);
        Assert.Equal(["three", "one"], await TextsOf(browser, executable, extracted.Output!));   // in the order given (a page range gives them sorted)

        var deleted = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Delete, [abc], Just(2)), CancellationToken.None);
        Assert.True(deleted.Ok, deleted.Detail);
        Assert.Equal(["one", "three"], await TextsOf(browser, executable, deleted.Output!));

        var turned = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Rotate, [abc], Just(1), Angle: 90), CancellationToken.None);
        Assert.True(turned.Ok, turned.Detail);
        var drawn = await browser.ReadPdfAsync(executable, turned.Output!, new PdfReadOptions(MaxSide: 400, Pages: [1, 2]), CancellationToken.None);
        Assert.True(drawn.Pages[0].Width > drawn.Pages[0].Height, "page 1 is turned to landscape");
        Assert.True(drawn.Pages[1].Width < drawn.Pages[1].Height, "page 2 stays portrait");

        var refused = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Delete, [abc], _ => (null, "Error: not all of them")), CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("Error: not all of them", refused.Refused);
    }

    [WindowsFact]
    public async Task TheMachinesBrowser_FillsAForm_AndFlattensIt()
    {
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;   // no browser installed: nothing to prove here
        }

        using var values = JsonDocument.Parse("{\"name\":\"Ada Lovelace\",\"agree\":true,\"color\":\"green\"}");
        var fields = values.RootElement.EnumerateObject().Select(p => new PdfFieldValue(p.Name, p.Value.Clone())).ToList();

        var filled = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.FillForm, [TestPdfs.Form()], _ => ([], null), Fields: fields, Flatten: true), CancellationToken.None);

        Assert.True(filled.Ok, filled.Detail);
        string text = (await TextsOf(browser, executable, filled.Output!))[0];
        Assert.Contains("Ada Lovelace", text, StringComparison.Ordinal);   // flattened: the value is the page's own text now
        Assert.Contains("green", text, StringComparison.Ordinal);

        using var wrong = JsonDocument.Parse("{\"nmae\":\"x\"}");
        var unknown = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.FillForm, [TestPdfs.Form()], _ => ([], null), Fields: [new PdfFieldValue("nmae", wrong.RootElement.GetProperty("nmae").Clone())]), CancellationToken.None);
        Assert.False(unknown.Ok);
        Assert.Equal(["nmae"], unknown.UnknownFields);
        Assert.Equal(["name", "agree", "color"], unknown.FieldNames);

        using var badChoice = JsonDocument.Parse("\"purple\"");
        var bad = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.FillForm, [TestPdfs.Form()], _ => ([], null), Fields: [new PdfFieldValue("color", badChoice.RootElement.Clone())]), CancellationToken.None);
        Assert.False(bad.Ok);
        var refusal = Assert.Single(bad.BadFields);
        Assert.Equal("color", refusal.Name);
        Assert.Equal(["red", "green", "blue"], refusal.Options);

        var xfa = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.FillForm, [TestPdfs.XfaOnly()], _ => ([], null), Fields: fields), CancellationToken.None);
        Assert.True(xfa.Xfa, xfa.Detail);
    }

    [WindowsFact]
    public async Task TheMachinesBrowser_RefusesALockedInput_AndNotAPdf()
    {
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;   // no browser installed: nothing to prove here
        }

        var locked = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Merge, [BrowserPdfReaderTests.MiniPdf("one"), TestPdfs.Locked()], All), CancellationToken.None);
        Assert.False(locked.Ok);
        Assert.Equal(1, locked.LockedInput);

        var notPdf = await browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Rotate, [Encoding.ASCII.GetBytes("hello")], All, Angle: 90), CancellationToken.None);
        Assert.False(notPdf.Ok);
        Assert.StartsWith("not a PDF pdf-lib can open: ", notPdf.Detail, StringComparison.Ordinal);
    }
}
