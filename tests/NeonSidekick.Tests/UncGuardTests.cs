using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// The UNC side of the 2026-10-22 path fixes: a whole drive judged by the folded path (review V8), the most specific share deciding a target's
/// access (J13), a network share's relative path in Windows' spelling on a Mac (H17).
/// </summary>
public sealed class UncGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public UncGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [WindowsTheory]
    [InlineData(@"C:\Temp\..")]
    [InlineData(@"C:\.")]
    [InlineData(@"C:\x\..\.")]
    public void AWholeDrive_IsJudgedFolded(string path) =>
        Assert.Equal(UncText.WholeDrive("C:"), new UncShareConfig { Path = path }.Problem);

    /// <summary>A read-only share nested in a read-write one is reached as itself, whichever name the call gave (review J13).</summary>
    [WindowsFact]
    public void TheMostSpecificShare_DecidesTheAccess()
    {
        string parent = Path.Combine(_dir, "data");
        string nested = Path.Combine(parent, "finance");
        Directory.CreateDirectory(nested);
        var catalog = new UncCatalog(
            [
                new UncNamedShare("data", new UncShareConfig { Path = parent, Access = "readwrite" }, "test"),
                new UncNamedShare("finance", new UncShareConfig { Path = nested }, "test"),
            ],
            []);
        var access = new UncAccess(() => catalog, new ManualTimeProvider());

        var found = access.Resolve("data", @"finance\q3.xlsx", out string relative, out string? error);
        Assert.Null(error);
        Assert.Equal("finance", found!.Name);
        Assert.Equal(Path.Combine(nested, "q3.xlsx"), relative);   // a full path under the nested share, which its sandbox takes
        Assert.NotNull(UncAccess.WriteRefusal(found, writesOn: true));

        // A sibling reached by ".." is no nested share: the named share keeps it (and its own sandbox refuses the climb).
        Assert.Equal("data", access.Resolve("data", @"..\other\x.txt", out _, out _)!.Name);
        Assert.Equal("data", access.Resolve("data", "plain.txt", out _, out _)!.Name);
    }

    [UnixFact]
    public void ANetworkSharesBackslashes_AreSeparators_Unix()
    {
        var share = new UncNamedShare("eng", new UncShareConfig { Path = "smb://fs01/eng" }, "test");
        Assert.Equal("reports/q3.md", UncAccess.ForFiles(share, @"reports\q3.md"));
        var local = new UncNamedShare("data", new UncShareConfig { Path = "/Users/me/data" }, "test");
        Assert.Equal(@"a\b.txt", UncAccess.ForFiles(local, @"a\b.txt"));   // a local folder's names may hold one
    }
}
