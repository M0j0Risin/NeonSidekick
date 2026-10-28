using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Audits;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Office;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;
using NeonSidekick.Web;
using static NeonSidekick.Tests.Fakes.TestOffice;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>read_office</c> the tool (2026-10-28, the Office plan): every <see cref="OfficeFile"/> extension reaching its reader, the arguments
/// a kind doesn't take refused, the continuation rules, its classing (quiet, read-only in plan mode, kept in an audit), the hints that
/// point to it from <c>read_file</c>, <c>unc_read</c>, <c>web_fetch</c> and <c>download_file</c>, and a read on a share.
/// </summary>
public sealed class ReadOfficeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "readoffice-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new();
    private readonly ReadOfficeTool _tool;

    public ReadOfficeTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, _time);
        _tool = new ReadOfficeTool(_files, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private static AIFunctionArguments Args(params (string Key, object? Value)[] pairs)
    {
        var arguments = new AIFunctionArguments();
        foreach (var (key, value) in pairs)
        {
            arguments[key] = value;
        }

        return arguments;
    }

    private async Task<string> Call(params (string, object?)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    [Theory]
    [InlineData("a.docx")]
    [InlineData("a.docm")]
    [InlineData("a.dotx")]
    [InlineData("a.dotm")]
    public async Task EveryWordExtension_ReachesTheWordReader(string name)
    {
        Put(name, Docx(P("Hello")));
        Assert.Equal($"{name} (Word document, 5 characters)\nHello", await Call(("path", name)));
    }

    [Theory]
    [InlineData("b.xlsx")]
    [InlineData("b.xlsm")]
    [InlineData("b.xltx")]
    [InlineData("b.xltm")]
    public async Task EveryExcelExtension_ReachesTheExcelReader(string name)
    {
        Put(name, Xlsx([new("S", SheetData("<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>", "A1"))]));
        Assert.StartsWith($"{name} (Excel workbook, 1 sheet;", await Call(("path", name)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c.pptx")]
    [InlineData("c.pptm")]
    [InlineData("c.potx")]
    [InlineData("c.potm")]
    [InlineData("c.ppsx")]
    [InlineData("c.ppsm")]
    public async Task EverySlidesExtension_ReachesTheSlidesReader(string name)
    {
        Put(name, Pptx([new(Shape("title", "Hi"))]));
        Assert.Equal($"{name} (slide 1 of 1)\n## Slide 1 — Hi", await Call(("path", name)));
    }

    [Fact]
    public async Task ArgumentsTheKindDoesntTake_AreRefused()
    {
        Put("a.docx", Docx(P("x")));
        Put("b.xlsx", Xlsx([new("S", SheetData("", "A1"))]));
        Put("c.pptx", Pptx([new(Shape("title", "T")), new(Shape("title", "U"))]));
        Assert.Equal(OfficeText.WrongArgument("sheet", OfficeKind.Word), await Call(("path", "a.docx"), ("sheet", "S")));
        Assert.Equal(OfficeText.WrongArgument("notes", OfficeKind.Word), await Call(("path", "a.docx"), ("notes", true)));
        Assert.Equal(OfficeText.WrongArgument("from_char", OfficeKind.Excel), await Call(("path", "b.xlsx"), ("from_char", 10)));
        Assert.Equal(OfficeText.WrongArgument("slides", OfficeKind.Excel), await Call(("path", "b.xlsx"), ("slides", "1")));
        Assert.Equal(OfficeText.WrongArgument("range", OfficeKind.Slides), await Call(("path", "c.pptx"), ("range", "A1")));
        Assert.Equal(OfficeText.FromCharNeedsOneSlide, await Call(("path", "c.pptx"), ("slides", "1-2"), ("from_char", 5)));
        Assert.Equal(OfficeText.FromCharNeedsOneSlide, await Call(("path", "c.pptx"), ("from_char", 5)));
        Assert.StartsWith("c.pptx (slide 2 of 2)", await Call(("path", "c.pptx"), ("slides", "2"), ("from_char", 3)), StringComparison.Ordinal);
        Assert.Equal(OfficeText.BadSlides("x"), await Call(("path", "c.pptx"), ("slides", "x")));
        Assert.Equal(FileText.BadBoolean("notes", "maybe"), await Call(("path", "c.pptx"), ("notes", "maybe")));
        Assert.Equal(FileText.PathRequired("path"), await Call(("sheet", "S")));
        Assert.Equal(OfficeText.Legacy("old.doc"), await Call(("path", "old.doc")));
        Assert.Equal(FileText.Error(FileOutcome.Missing, "gone.docx", "read"), await Call(("path", "gone.docx")));
    }

    [Fact]
    public async Task AWorkbookOpenElsewhere_StillReads()
    {
        Put("open.xlsx", Xlsx([new("S", SheetData("<row r=\"1\"><c r=\"A1\"><v>3</v></c></row>", "A1"))]));
        using var held = new FileStream(Path.Combine(_root, "open.xlsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.StartsWith("open.xlsx (Excel workbook", await Call(("path", "open.xlsx")), StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_IsAReadOnlyQuietFileTool_AfterReadPdf()
    {
        Assert.Contains(ReadOfficeTool.ToolName, PlanTools.ReadOnly);
        Assert.DoesNotContain(ReadOfficeTool.ToolName, PlanTools.Mutating);
        Assert.Contains(ReadOfficeTool.ToolName, ChatScreen.QuietTools);
        Assert.Contains(ReadOfficeTool.ToolName, AuditTools.Always);
        Assert.Equal(ReadOfficeTool.BaseDescription, _tool.Description);
        Assert.EndsWith("read_file cannot read an Office file.", ReadOfficeTool.BaseDescription, StringComparison.Ordinal);
        Assert.True(ReadOfficeTool.BaseDescription.Length <= ReadPdfTool.BaseDescription.Length, "no longer than read_pdf's");
        var tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings);
        Assert.Equal(ReadFileTool.ToolName, tools[tools.ToList().FindIndex(t => t.Name == ReadOfficeTool.ToolName) - 1].Name);   // read_pdf left out without a reader
    }

    [Fact]
    public async Task ReadFile_PointsToReadOffice_AndTheLegacyFormatsToSaveAs()
    {
        Put("r.docx", Docx(P("x")));
        Put("r.doc", [0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0]);
        var read = new ReadFileTool(_files);
        Assert.Equal(FileText.NotText("r.docx"), await read.InvokeAsync(Args(("path", "r.docx"))));
        Assert.EndsWith(FileText.ReadOfficeHint, FileText.NotText("r.docx"), StringComparison.Ordinal);
        Assert.EndsWith(FileText.SaveAsOfficeHint, FileText.NotText("r.doc"), StringComparison.Ordinal);
        Assert.EndsWith(FileText.SaveAsOfficeHint, FileText.NotText("r.odt"), StringComparison.Ordinal);
        Assert.Equal("; use read_office to read it", FileText.ReadOfficeHint);
    }

    [Fact]
    public void TheWebAndShareHints_NameReadOffice()
    {
        Assert.EndsWith(WebText.SaveOfficeNote, WebText.Binary("https://x.test/report.docx", "application/octet-stream", 100), StringComparison.Ordinal);
        Assert.EndsWith(WebText.SaveOfficeNote, WebText.Binary("https://x.test/get?id=3", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 100), StringComparison.Ordinal);
        Assert.DoesNotContain(WebText.SaveOfficeNote, WebText.Binary("https://x.test/a.zip", "application/zip", 100), StringComparison.Ordinal);
        Assert.Equal("; read_office reads it", WebText.ReadOfficeNote);
        Assert.Contains("read_office with share", new UncReadTool(new UncAccess(() => UncCatalog.Empty, TimeProvider.System), () => _settings).Description, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task OnAShare_ReadsWhereItIs()
    {
        string share = Path.Combine(_dir, "share");
        Directory.CreateDirectory(share);
        File.WriteAllBytes(Path.Combine(share, "plan.docx"), Docx(P("On the share")));
        var catalog = new UncCatalog([new UncNamedShare("eng", new UncShareConfig { Path = share }, "test")], []);
        _settings.UncTools = true;
        var tool = new ReadOfficeTool(_files, () => _settings, new UncAccess(() => catalog, _time));
        Assert.Contains("share", tool.JsonSchema.ToString(), StringComparison.Ordinal);
        Assert.Equal("plan.docx (Word document, 12 characters)\nOn the share", await tool.InvokeAsync(Args(("path", "plan.docx"), ("share", "eng"))));
        Assert.Equal("plan.docx (Word document, 12 characters)\nOn the share", await tool.InvokeAsync(Args(("path", Path.Combine(share, "plan.docx")))));
    }
}
