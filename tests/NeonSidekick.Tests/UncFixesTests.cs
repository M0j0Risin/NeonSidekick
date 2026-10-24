using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// The UNC tools after the 2026-10-22 fixes (the fix plan's Phase 6), over local folders standing in for shares: a share's root named as
/// its answers are built, never inside a file's own text; a name given twice (ignoring case) loads neither; the catalog parsed once while
/// its file stands; unc_fetch waited out and cancellable between files, its copy taken back; a share change that failed partway logged.
/// </summary>
public sealed class UncFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _sandboxRoot;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { UncTools = true, FileTools = true, UncWrites = true };
    private readonly WorkingDirectory _sandbox;
    private readonly UncCatalog _catalog;
    private readonly IReadOnlyList<AIFunction> _tools;

    public UncFixesTests()
    {
        _data = Path.Combine(_dir, "data");
        _sandboxRoot = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_data);
        _catalog = new UncCatalog([new UncNamedShare("data", new UncShareConfig { Path = _data, Access = "readwrite" }, "test")], []);
        _sandbox = new WorkingDirectory(() => _sandboxRoot, _time);
        _tools = ChatScreen.UncTools(new UncAccess(() => _catalog, _time), _sandbox, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string relative, string text)
    {
        string full = Path.Combine(_data, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
        return full;
    }

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        ToolAnswers.Text(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))));

    [Fact]
    public async Task AFilesOwnText_SayingTheWorkingDirectory_IsNeverRewritten()
    {
        Put("readme.md", "Copy it into the working directory first.\n");
        string share = UncText.ShareName(_catalog.Shares[0]);

        string read = await Invoke<UncReadTool>(("share", "data"), ("path", "readme.md"));
        Assert.Contains("Copy it into the working directory first.", read, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy it into " + share, read, StringComparison.Ordinal);

        string hits = await Invoke<UncSearchTool>(("share", "data"), ("text", "working directory"));
        Assert.Contains("readme.md:1: Copy it into the working directory first.", hits, StringComparison.Ordinal);
        Assert.Contains("under " + share, hits, StringComparison.Ordinal);   // the root itself is still named for the share

        // A patch copied from the read lands (it missed while the read showed the share's name in the text).
        Assert.StartsWith("Edited", await Invoke<UncPatchTool>(("share", "data"), ("path", "readme.md"), ("old_text", "into the working directory"), ("new_text", "into the sandbox")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Copy it into the sandbox first.\n", File.ReadAllText(Path.Combine(_data, "readme.md")));

        // Outside the scope the root is the working directory again.
        Assert.Equal(FileText.DefaultRootName, FileText.RootName);
        using (FileText.NamingRoot("share 'x'"))
        {
            Assert.Equal("Error: 'a' is outside share 'x'; every path must stay inside it", FileText.OutsideRoot("a"));
        }

        Assert.Equal("Error: 'a' is outside the working directory; every path must stay inside it", FileText.OutsideRoot("a"));
    }

    [Fact]
    public void AShareNamedTwice_IgnoringCase_LoadsNeither_AndIsAProblem()
    {
        string path = Path.Combine(_dir, "unc.json");
        File.WriteAllText(path, """
            {
              // two spellings of one name
              "shares": {
                "eng": { "path": "\\\\fs01\\eng" },
                "Eng": { "path": "\\\\fs01\\other" },
                "fin": { "path": "\\\\fs02\\fin" }
              }
            }
            """);
        var catalog = UncConfigFile.Load(path);
        Assert.Equal(["fin"], catalog.Shares.Select(s => s.Name));
        var problem = Assert.Single(catalog.Problems);
        Assert.Equal(UncText.NamedTwice, problem.Reason);

        // An exact repeat is the same (the reader kept the last; the password splice wrote the first).
        File.WriteAllText(path, """{ "shares": { "eng": { "path": "\\\\a\\b" }, "eng": { "path": "\\\\c\\d" } } }""");
        Assert.Empty(UncConfigFile.Load(path).Shares);
        var repeated = ConnectionsFileEdit.RepeatedNames(Encoding.UTF8.GetBytes("""{ "shares": { "eng": 1, " ENG ": 2, "x": 3 } }"""), "shares");
        Assert.Single(repeated);
        Assert.True(repeated.Contains("eng"));
    }

    [Fact]
    public void TheCatalog_IsParsedOnce_WhileItsFileStands_AndAgainOnceItChanges()
    {
        string path = Path.Combine(_dir, "unc2.json");
        File.WriteAllText(path, """{ "shares": { "eng": { "path": "\\\\fs01\\eng" } } }""");
        var first = UncConfigFile.Load(path);
        Assert.Same(first, UncConfigFile.Load(path));

        File.WriteAllText(path, """{ "shares": { "fin": { "path": "\\\\fs01\\fin" } } }""");
        Assert.Equal(["fin"], UncConfigFile.Load(path).Shares.Select(s => s.Name));
        File.Delete(path);
        Assert.Empty(UncConfigFile.Load(path).Shares);
    }

    [Fact]
    public async Task AFetch_IsWaitedOut_TakenBackOnCancel_AndLogged()
    {
        for (int i = 0; i < 20; i++)
        {
            Put(Path.Combine("pack", $"f{i:D2}.txt"), "x");   // Path.Combine: on a Mac a "\" spelling made 20 root files named pack\f…
        }

        // Cancelled before the first file: nothing of the folder is left in the working directory.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var shareFiles = new UncAccess(() => _catalog, _time).Files(_catalog.Shares[0]);
        Assert.ThrowsAny<OperationCanceledException>(() => WorkingDirectory.CopyBetween(shareFiles, "pack", _sandbox, "pack", overwrite: false, cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, "pack")) && Directory.GetFiles(Path.Combine(_sandboxRoot, "pack")).Length > 0);

        var audit = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == UncConfigFile.Category && e.Level == DiagnosticLevel.Info) audit.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.StartsWith("fetched pack", await Invoke<UncFetchTool>(("share", "data"), ("path", "pack")), StringComparison.Ordinal);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(20, Directory.GetFiles(Path.Combine(_sandboxRoot, "pack")).Length);
        Assert.Contains(audit, line => line.EndsWith("fetched pack to pack", StringComparison.Ordinal));
    }

    [Fact]
    public void APartwayChange_IsLoggedAsOne()
    {
        string line = UncText.AuditLogLine("data", "me", "deleted", "big", null, partial: true);
        Assert.Equal("data (me): deleted big" + UncText.PartialAuditNote, line);
        Assert.Equal("data (me): moved a to b", UncText.AuditLogLine("data", "me", "moved", "a", "b", partial: false));
        UncChange plain = ("done", true);
        Assert.Equal(new UncChange("done", true, Partial: false), plain);
    }
}
