using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Pdf;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// Editing PDFs (2026-10-14, the PDF plan's phase 4b): what each edit needs and makes (<see cref="PdfEditPlan"/>), the door over the fake
/// browser (<see cref="PdfEditing"/>: the refusals before any work, the names, the numbering, to and overwrite, every way the edit can say
/// no), the tool's arguments, <c>/pdf</c>'s edit grammar and its completion, and <c>read_pdf</c>'s fields. The real browser's edits are
/// <see cref="BrowserPdfEditorTests"/>'.
/// </summary>
public sealed class PdfEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pdfedit-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly WorkingDirectory _files;
    private readonly PdfEditing _editing;

    public PdfEditTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, TimeProvider.System);
        _editing = new PdfEditing(_browser, _files, () => _browser.Executable);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Pdf(string relative, string text = "%PDF-1.7 a pdf")
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (name, value) in pairs)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── The plan ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheActions_AndAngles_ReadAsTheyAreWritten()
    {
        Assert.True(PdfEditPlan.TryAction("Fill-Form", out var fill));
        Assert.Equal(PdfEditOp.FillForm, fill);
        Assert.True(PdfEditPlan.TryAction("rotate", out var rotate));
        Assert.Equal(PdfEditOp.Rotate, rotate);
        Assert.False(PdfEditPlan.TryAction("split", out _));
        Assert.Equal(270, PdfEditPlan.AngleOf(-90));
        Assert.Equal(90, PdfEditPlan.AngleOf(450));
        Assert.Null(PdfEditPlan.AngleOf(45));
        Assert.Null(PdfEditPlan.AngleOf(0));
    }

    [Fact]
    public void Check_RefusesWhatAnActionLacksOrDoesNotTake()
    {
        Assert.Equal(PdfText.MergeNeedsTwo, PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Merge, ["a.pdf"])));
        Assert.Equal(PdfText.MergeTooMany(20), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Merge, [.. Enumerable.Range(1, 21).Select(i => i + ".pdf")])));
        Assert.Equal(PdfText.OnePdf("extract"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Extract, ["a.pdf", "b.pdf"], "1")));
        Assert.Equal(PdfText.PagesNeeded("delete"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Delete, ["a.pdf"])));
        Assert.Equal(PdfText.NotFor("pages", "merge"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Merge, ["a.pdf", "b.pdf"], "1")));
        Assert.Equal(PdfText.BadPages("2-1"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Extract, ["a.pdf"], "2-1")));
        Assert.Equal(PdfText.BadAngle(45), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Rotate, ["a.pdf"], Angle: 45)));
        Assert.Equal(PdfText.BadAngle(0), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Rotate, ["a.pdf"])));
        Assert.Equal(PdfText.NotFor("angle", "extract"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Extract, ["a.pdf"], "1", Angle: 90)));
        Assert.Equal(PdfText.FieldsNeeded, PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.FillForm, ["a.pdf"])));
        Assert.Equal(PdfText.NotFor("flatten", "rotate"), PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Rotate, ["a.pdf"], Angle: 90, Flatten: true)));
        Assert.Null(PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.Rotate, ["a.pdf"], Angle: -90)));
        Assert.Null(PdfEditPlan.Check(new PdfEditRequest(PdfEditOp.FillForm, ["a.pdf"], Fields: [new PdfFieldValue("name", Json("\"Ada\""))])));
    }

    [Fact]
    public void PagesFor_ClosesTheRange_AndNeverDeletesEveryPage()
    {
        Assert.Equal([2, 3], PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Extract, ["a.pdf"], "2-"), "a.pdf", 3).Pages);
        Assert.Equal([1, 2, 3], PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Rotate, ["a.pdf"], Angle: 90), "a.pdf", 3).Pages);
        Assert.Empty(PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Merge, ["a.pdf", "b.pdf"]), "a.pdf", 3).Pages!);
        Assert.Equal(PdfText.PastTheEnd("a.pdf", 3, "7-9"), PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Extract, ["a.pdf"], "7-9"), "a.pdf", 3).Error);
        Assert.Equal(PdfText.CannotDeleteAll("a.pdf", 3), PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Delete, ["a.pdf"], "1-"), "a.pdf", 3).Error);
        Assert.Equal([1, 2], PdfEditPlan.PagesFor(new PdfEditRequest(PdfEditOp.Delete, ["a.pdf"], "1-2"), "a.pdf", 3).Pages);
    }

    [Fact]
    public void TheNewFile_IsNamedForWhatWasDone()
    {
        Assert.Equal("-p2-5", PdfEditPlan.Suffix(PdfEditOp.Extract, [2, 3, 4, 5]));
        Assert.Equal("-p1_3_7-9", PdfEditPlan.Suffix(PdfEditOp.Extract, [1, 3, 7, 8, 9]));
        Assert.Equal("-pages", PdfEditPlan.Suffix(PdfEditOp.Extract, [.. Enumerable.Range(1, 40).Where(n => n % 2 == 0)]));
        Assert.Equal("-merged", PdfEditPlan.Suffix(PdfEditOp.Merge, []));
        Assert.Equal("-edited", PdfEditPlan.Suffix(PdfEditOp.Delete, [2]));
        Assert.Equal("-rotated", PdfEditPlan.Suffix(PdfEditOp.Rotate, [1]));
        Assert.Equal("-filled", PdfEditPlan.Suffix(PdfEditOp.FillForm, []));
        Assert.Equal("docs/report-p2-5.pdf", PdfPlan.OutputFor(null, "docs/report.pdf", null, null, DateTimeOffset.UnixEpoch, _ => false, "-p2-5"));
    }

    // ── The door ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExtract_WritesANewPdfBesideIt_NamedForThePages_ThenNumbers()
    {
        Pdf("docs/report.pdf");
        _browser.EditPageCount = 8;

        string first = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Extract, ["docs/report.pdf"], "2-5"), CancellationToken.None);
        string second = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Extract, ["docs/report.pdf"], "2-5"), CancellationToken.None);

        Assert.StartsWith("Wrote " + Path.Combine("docs", "report-p2-5.pdf") + " (2 pages, ", first, StringComparison.Ordinal);   // the OS's separator, as every file result
        Assert.EndsWith("): pages 2–5 of " + Path.Combine("docs", "report.pdf"), first, StringComparison.Ordinal);
        Assert.StartsWith("Wrote " + Path.Combine("docs", "report-p2-5-2.pdf") + " ", second, StringComparison.Ordinal);   // never over the first
        Assert.Equal(_browser.EditBytes, File.ReadAllBytes(Path.Combine(_root, "docs", "report-p2-5.pdf")));
        Assert.Equal("%PDF-1.7 a pdf", File.ReadAllText(Path.Combine(_root, "docs", "report.pdf")));   // the original untouched
        var job = _browser.EditJobs[0];
        Assert.Equal(PdfEditOp.Extract, job.Op);
        Assert.Equal("%PDF-1.7 a pdf", Encoding.ASCII.GetString(Assert.Single(job.Inputs)));
    }

    [Fact]
    public async Task To_AndOverwrite_ReplaceOnlyWhenAsked_TheOriginalIncluded()
    {
        Pdf("report.pdf");
        Pdf("out.pdf", "%PDF-1.7 old");

        string refused = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90, To: "out.pdf"), CancellationToken.None);
        Assert.Equal(FileText.Error(FileOutcome.Exists, "out.pdf", "write"), refused);
        Assert.Empty(_browser.EditJobs);   // refused before the browser ran

        string replaced = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90, To: "report", Overwrite: true), CancellationToken.None);
        Assert.StartsWith("Replaced report.pdf (2 pages, ", replaced, StringComparison.Ordinal);
        Assert.EndsWith("): report.pdf with pages 1–3 turned 90°", replaced, StringComparison.Ordinal);
        Assert.Equal(_browser.EditBytes, File.ReadAllBytes(Path.Combine(_root, "report.pdf")));

        Directory.CreateDirectory(Path.Combine(_root, "out"));
        string intoFolder = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 180, To: "out"), CancellationToken.None);
        Assert.StartsWith("Wrote " + Path.Combine("out", "report-rotated.pdf") + " ", intoFolder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overwrite_WithNoTo_StillNumbers_NeverOverAFileOfThatName()
    {
        Pdf("contract.pdf");
        Pdf("contract-rotated.pdf", "%PDF-1.7 the user's own");

        string result = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["contract.pdf"], Angle: 90, Overwrite: true), CancellationToken.None);

        Assert.StartsWith("Wrote contract-rotated-2.pdf ", result, StringComparison.Ordinal);
        Assert.Equal("%PDF-1.7 the user's own", File.ReadAllText(Path.Combine(_root, "contract-rotated.pdf")));
    }

    [Fact]
    public async Task ATo_EndingInASlash_IsAFolder_EvenOneNotMadeYet_AndNumbers()
    {
        Pdf("report.pdf");

        string first = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90, To: "out/"), CancellationToken.None);
        string second = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90, To: "out/"), CancellationToken.None);

        Assert.StartsWith("Wrote " + Path.Combine("out", "report-rotated.pdf") + " ", first, StringComparison.Ordinal);
        Assert.StartsWith("Wrote " + Path.Combine("out", "report-rotated-2.pdf") + " ", second, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "out", ".pdf")));
    }

    [Fact]
    public async Task AMerge_TakesThePdfsInOrder()
    {
        Pdf("a.pdf", "%PDF-1.7 a");
        Pdf("b c.pdf", "%PDF-1.7 b");

        string result = await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Merge, ["a.pdf", "b c.pdf"]), CancellationToken.None);

        Assert.StartsWith("Wrote a-merged.pdf ", result, StringComparison.Ordinal);
        Assert.EndsWith("): a.pdf and b c.pdf merged", result, StringComparison.Ordinal);
        Assert.Equal(["%PDF-1.7 a", "%PDF-1.7 b"], _browser.EditJobs[0].Inputs.Select(Encoding.ASCII.GetString));
    }

    [Fact]
    public async Task TheRefusals_BeforeAnyWork()
    {
        Pdf("report.pdf");
        File.WriteAllText(Path.Combine(_root, "notes.pdf"), "just text");

        Assert.Equal(PdfText.NotAPdf("notes.pdf"), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["notes.pdf"], Angle: 90), CancellationToken.None));
        Assert.Equal(FileText.Error(FileOutcome.Missing, "gone.pdf", "read"), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["gone.pdf"], Angle: 90), CancellationToken.None));
        Assert.Equal(FileText.Error(FileOutcome.OutsideRoot, "../out.pdf", "write"), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90, To: "../out.pdf"), CancellationToken.None));
        Assert.Equal(PdfText.PagesNeeded("extract"), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Extract, ["report.pdf"]), CancellationToken.None));
        Assert.Empty(_browser.EditJobs);

        _browser.Executable = null;
        Assert.Equal(PdfText.NoBrowserToEdit, await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Rotate, ["report.pdf"], Angle: 90), CancellationToken.None));
    }

    [Fact]
    public async Task EveryNo_FromTheEdit_IsASentence_AndNothingIsWritten()
    {
        Pdf("form.pdf");
        Pdf("other.pdf");
        var fill = new PdfEditRequest(PdfEditOp.FillForm, ["form.pdf"], Fields: [new PdfFieldValue("nmae", Json("\"Ada\""))]);

        _browser.EditPageCount = 2;
        Assert.Equal(PdfText.CannotDeleteAll("form.pdf", 2), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Delete, ["form.pdf"], "1-2"), CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, HeadlessBrowserLocked, null, 0) { LockedInput = 1 };
        Assert.Equal(PdfText.LockedToEdit("other.pdf"), await _editing.EditAsync(new PdfEditRequest(PdfEditOp.Merge, ["form.pdf", "other.pdf"]), CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, "an XFA form", null, 0) { Xfa = true };
        Assert.Equal(PdfText.XfaForm("form.pdf"), await _editing.EditAsync(fill, CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, "no form fields", null, 0) { NoFields = true };
        Assert.Equal("Error: 'form.pdf' has no form fields to fill", await _editing.EditAsync(fill, CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, "unknown", null, 0) { UnknownFields = ["nmae"], FieldNames = ["name", "agree"] };
        Assert.Equal("Error: 'form.pdf' has no field 'nmae'; its fields are 'name', 'agree'", await _editing.EditAsync(fill, CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, "bad", null, 0) { BadFields = [new PdfFieldRefusal("color", "", ["red", "blue"]), new PdfFieldRefusal("go", PdfFieldRefusal.NoValue, []), new PdfFieldRefusal("name", "WinAnsi cannot encode \"日\"", [])] };
        Assert.Equal("Error: nothing was written; 'form.pdf' would not take 'color', which takes one of 'red', 'blue'; 'go', which takes no value (a button or a signature); 'name' (WinAnsi cannot encode \"日\")", await _editing.EditAsync(fill, CancellationToken.None));

        _browser.EditOutcome = new PdfEdit(false, "stopped after 2 min", null, 0);
        Assert.Equal("Error: msedge could not edit the PDF: stopped after 2 min", await _editing.EditAsync(fill, CancellationToken.None));

        Assert.Equal(["form.pdf", "other.pdf"], Directory.GetFiles(_root).Select(Path.GetFileName).Order());
    }

    private const string HeadlessBrowserLocked = HeadlessBrowser.PdfLockedDetail;

    // ── The tool ──────────────────────────────────────────────────────────────

    private PdfEditTool Tool() => new(_files, _editing);

    [Fact]
    public async Task TheTool_ReadsItsArguments_AndPassesThemOn()
    {
        Pdf("form.pdf");
        Pdf("a.pdf");
        Pdf("b.pdf");

        string filled = (string)(await Tool().InvokeAsync(Args(("action", "fill_form"), ("path", "form.pdf"), ("fields", Json("{\"name\":\"Ada\",\"agree\":true,\"colors\":[\"red\",\"blue\"]}")), ("flatten", true))))!;
        Assert.EndsWith("): form.pdf with 3 fields filled in, flattened", filled, StringComparison.Ordinal);
        var job = _browser.EditJobs[^1];
        Assert.Equal(["name", "agree", "colors"], job.Fields!.Select(f => f.Name));
        Assert.True(job.Flatten);

        string merged = (string)(await Tool().InvokeAsync(Args(("action", "merge"), ("paths", Json("[\"a.pdf\",\"b.pdf\"]")))))!;
        Assert.StartsWith("Wrote a-merged.pdf ", merged, StringComparison.Ordinal);

        string turned = (string)(await Tool().InvokeAsync(Args(("action", "rotate"), ("path", "a.pdf"), ("angle", -90))))!;
        Assert.EndsWith("turned 270°", turned, StringComparison.Ordinal);
        Assert.Equal(270, _browser.EditJobs[^1].Angle);

        Assert.Equal(FileText.BadChoice("action", "split", "merge, extract, delete, rotate, fill_form"), await Tool().InvokeAsync(Args(("action", "split"), ("path", "a.pdf"))));
        Assert.Equal(PdfText.BadAngleText("sideways"), await Tool().InvokeAsync(Args(("action", "rotate"), ("path", "a.pdf"), ("angle", "sideways"))));
        Assert.Equal(PdfText.BadFieldValue("name"), await Tool().InvokeAsync(Args(("action", "fill_form"), ("path", "form.pdf"), ("fields", Json("{\"name\":{\"first\":\"Ada\"}}")))));
        Assert.Equal(PdfText.BadFields("[1,2]"), await Tool().InvokeAsync(Args(("action", "fill_form"), ("path", "form.pdf"), ("fields", Json("[1,2]")))));
    }

    [Fact]
    public void TheTool_IsAQuietFileTool_NotForPlanMode_InTheWorkingDirectory()
    {
        Assert.Contains(PdfEditTool.ToolName, PlanTools.Mutating);
        Assert.DoesNotContain(PdfEditTool.ToolName, PlanTools.ReadOnly);
        Assert.Contains(PdfEditTool.ToolName, ChatScreen.QuietTools);
        Assert.Contains("working directory", Tool().Description, StringComparison.Ordinal);
        Assert.Equal(PdfEditTool.ToolName, FileToolNames.All[^1]);
    }

    // ── /pdf ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseEdit_ReadsTheVerbs_UnlessAFileHasTheName()
    {
        var (merge, _) = PdfCommand.ParseEdit("merge a.pdf \"b c.pdf\" to=all.pdf overwrite", _ => false);
        Assert.Equal(PdfEditOp.Merge, merge!.Op);
        Assert.Equal(["a.pdf", "b c.pdf"], merge.Paths);
        Assert.Equal("all.pdf", merge.To);
        Assert.True(merge.Overwrite);

        var (listed, _) = PdfCommand.ParseEdit("merge paths=a.pdf,b.pdf", _ => false);
        Assert.Equal(["a.pdf", "b.pdf"], listed!.Paths);

        var (extract, _) = PdfCommand.ParseEdit("extract my report.pdf pages=2-5", _ => false);
        Assert.Equal(["my report.pdf"], extract!.Paths);   // one path, spaces and all, as /pdf's own target is
        Assert.Equal("2-5", extract.Pages);

        var (rotate, _) = PdfCommand.ParseEdit("rotate a.pdf angle=-90", _ => false);
        Assert.Equal(-90, rotate!.Angle);

        Assert.Equal((null, null), PdfCommand.ParseEdit("merge", name => name == "merge"));   // a file called merge: /pdf makes a PDF of it
        Assert.Equal((null, null), PdfCommand.ParseEdit("notes.md to=x.pdf", _ => false));
        Assert.Equal(PdfText.BadAngleText("ninety"), PdfCommand.ParseEdit("rotate a.pdf angle=ninety", _ => false).Error);
        Assert.Equal(PdfText.NotAnEditOption("paper=a4", "extract"), PdfCommand.ParseEdit("extract a.pdf pages=1 paper=a4", _ => false).Error);
        Assert.Equal(PdfText.NotAnEditOption("landscape", "rotate"), PdfCommand.ParseEdit("rotate a.pdf angle=90 landscape", _ => false).Error);
    }

    [Fact]
    public async Task RunAsync_SendsAnEdit_ToTheEditor()
    {
        Pdf("report.pdf");
        var converter = new PdfConverter(_browser, new WebAccess(new HttpClient(new StubHttpMessageHandler()), _browser, TimeProvider.System).Fetcher, new Printing.PrintService(new FakePrintSpooler(), _files, () => new AppSettingsData(), TimeProvider.System), _files, () => new AppSettingsData());

        string result = await PdfCommand.RunAsync(converter, "extract report.pdf pages=1", () => null, CancellationToken.None, _editing);

        Assert.StartsWith("Wrote report-p1.pdf ", result, StringComparison.Ordinal);
        Assert.Empty(_browser.PdfRuns);   // no print
        Assert.Contains("/pdf merge <file> <file>…", PdfText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_OffersTheVerbs_ThenEachOnesOptions()
    {
        Assert.Equal(["merge"], PdfCommand.Complete("me").Select(c => c.Text));
        Assert.Contains("reply", PdfCommand.Complete("").Select(c => c.Text));
        Assert.Equal(["rotate a.pdf pages=", "rotate a.pdf angle=90", "rotate a.pdf angle=180", "rotate a.pdf angle=270", "rotate a.pdf to=", "rotate a.pdf overwrite"], PdfCommand.Complete("rotate a.pdf ").Select(c => c.Text));
        Assert.Equal(["merge a.pdf b.pdf to=", "merge a.pdf b.pdf overwrite"], PdfCommand.Complete("merge a.pdf b.pdf ").Select(c => c.Text));
        Assert.DoesNotContain("extract a.pdf pages=", PdfCommand.Complete("extract a.pdf pages=1 ").Select(c => c.Text));
    }

    [Fact]
    public void TheArgumentList_OffersPdfs_ForEachWordOfAMerge()
    {
        var sources = new ChatScreen.ArgumentSources(() => [], "default", [], _ => [], _ => new MentionResult(FileOutcome.Ok, [], false), _ => new MentionResult(FileOutcome.Ok, [], false))
        {
            AnyFiles = prefix => new MentionResult(FileOutcome.Ok, new[] { "docs/", "a.pdf", "b.pdf", "notes.md" }.Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList(), false),
        };

        Assert.Equal(["merge docs/", "merge a.pdf", "merge b.pdf"], ChatScreen.ArgumentPaths("/pdf", "merge ", sources)!.Paths);
        Assert.Equal(["merge a.pdf b.pdf"], ChatScreen.ArgumentPaths("/pdf", "merge a.pdf b", sources)!.Paths);   // the next PDF, the first kept
        Assert.Equal(["extract a.pdf"], ChatScreen.ArgumentPaths("/pdf", "extract a", sources)!.Paths);
        Assert.Null(ChatScreen.ArgumentPaths("/pdf", "extract a.pdf ", sources));   // the options' word list now
        Assert.Null(ChatScreen.ArgumentPaths("/pdf", "merge a.pdf to=", sources));
        Assert.Contains("notes.md", ChatScreen.ArgumentPaths("/pdf", "", sources)!.Paths);   // /pdf's own target: any file
    }

    // ── read_pdf's fields ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReadPdf_ListsTheFormsFields_FromTheCache_AndRefusesThemWithPages()
    {
        Pdf("form.pdf");
        _browser.PdfReadCount = 1;
        _browser.PdfFields.Add(new PdfFormField("name", "text", "", [], 1, false));
        _browser.PdfFields.Add(new PdfFormField("agree", "checkbox", "Off", ["Yes"], 1, false));
        _browser.PdfFields.Add(new PdfFormField("color", "combobox", "red", ["red", "green", "blue"], 1, true));
        var tool = new ReadPdfTool(_files, new PdfReading(_browser, () => _browser.Executable), () => new AppSettingsData());

        string first = (string)(await tool.InvokeAsync(Args(("path", "form.pdf"), ("fields", true))))!;
        string again = (string)(await tool.InvokeAsync(Args(("path", "form.pdf"), ("fields", true))))!;

        Assert.Equal(
            "form.pdf: 3 form fields (pdf_edit's fill_form sets them by name)\n" +
            "- name: text, empty (page 1)\n" +
            "- agree: checkbox, \"Off\"; on value \"Yes\" (page 1)\n" +
            "- color: combobox, \"red\"; one of \"red\", \"green\", \"blue\"; read-only (page 1)",
            first);
        Assert.Equal(first, again);
        Assert.Single(_browser.PdfReadOptionsSeen);   // the second from the cache
        Assert.True(_browser.PdfReadOptionsSeen[0].Fields);
        Assert.Equal([], _browser.PdfReadOptionsSeen[0].Pages!);   // no page's text read for it
        Assert.Equal(PdfText.FieldsAlone, await tool.InvokeAsync(Args(("path", "form.pdf"), ("fields", true), ("pages", "1"))));
        Assert.Equal("form.pdf has no form fields", PdfText.FieldList("form.pdf", [], false));
        Assert.Equal("form.pdf: an XFA form; pdf_edit cannot fill it (it fills AcroForm fields)", PdfText.FieldList("form.pdf", [], true));
    }
}
