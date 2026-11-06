using System.Text;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public class UpdateManifestTests
{
    private static readonly string Hash = new('a', 64);

    private static byte[] Json(string files, int format = 1, string version = "0.5.7", string exe = "NeonSidekick.exe") =>
        Encoding.UTF8.GetBytes($$"""{"format":{{format}},"version":"{{version}}","rid":"win-x64","exe":"{{exe}}","files":[{{files}}]}""");

    private static string Entry(string path) => $$"""{"path":"{{path}}","sha256":"{{Hash}}"}""";

    [Fact]
    public void TheFormat_IsPinned()
    {
        // A lasting contract: an old exe reads a new release's manifest. This text is what build.ps1 writes.
        const string text = """
            {
              "format": 1,
              "version": "0.5.7",
              "rid": "win-x64",
              "exe": "NeonSidekick.exe",
              "files": [
                { "path": "NeonSidekick.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                { "path": "docs/README.md", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
              ]
            }
            """;
        var manifest = UpdateManifest.Read(Encoding.UTF8.GetBytes(text), out string? problem);
        Assert.Null(problem);
        Assert.Equal(new UpdateVersion(0, 5, 7), manifest!.ParsedVersion);
        Assert.Equal("win-x64", manifest.Rid);
        Assert.Equal("NeonSidekick.exe", manifest.Exe);
        Assert.Equal(["NeonSidekick.exe", "docs/README.md"], manifest.Files.Select(f => f.Path));
    }

    [Theory]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","exe":"a.exe","files":null}""")]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","exe":"a.exe","files":[null]}""")]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","files":[]}""")]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","exe":null,"files":[]}""")]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","exe":"a.exe","files":[{"path":"a.exe"}]}""")]
    [InlineData("""{"format":1,"version":"0.5.7","rid":"win-x64","exe":"a.exe","files":[{"path":"a.exe","sha256":null}]}""")]
    public void NullsInAManifest_AreRefused_NotThrown(string json)
    {
        // A null past the deserializer reached the crash catch at a start (2026-11-04, review P1-1).
        Assert.Null(UpdateManifest.Read(Encoding.UTF8.GetBytes(json), out string? problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a/COM1.dll")]
    [InlineData("LPT9")]
    [InlineData("aux.tar.gz")]
    public void ADeviceName_IsRefusedOnWindows(string path)
    {
        // CreateFile opens the device, not a file (2026-11-04, review P5-16). None is in today's package, and Unix has no such names.
        Assert.Equal(!OperatingSystem.IsWindows(), UpdateManifest.IsRelativeInside(path));
    }

    [Theory]
    [InlineData("console.dll")]
    [InlineData("com10.dll")]
    [InlineData("docs/auxiliary.md")]
    [InlineData("lpt.dll")]
    public void ANameThatOnlyStartsLikeADevice_IsFine(string path) => Assert.True(UpdateManifest.IsRelativeInside(path));

    [Fact]
    public void Versions_CompareByPart()
    {
        Assert.True(new UpdateVersion(0, 5, 10) > new UpdateVersion(0, 5, 9));
        Assert.True(new UpdateVersion(0, 6, 0) > new UpdateVersion(0, 5, 99));
        Assert.Equal(0, new UpdateVersion(0, 5, 7).CompareTo(new UpdateVersion(0, 5, 7)));
    }

    [Fact]
    public void AFormatItDoesNotKnow_IsRefused()
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("NeonSidekick.exe"), format: 2), out string? problem));
        Assert.Equal(UpdateText.ManifestUnknownFormat(2), problem);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Damaged_IsRefused(string text)
    {
        Assert.Null(UpdateManifest.Read(Encoding.UTF8.GetBytes(text), out string? problem));
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("0.5.7.0")]
    [InlineData("v0.5.7")]
    public void ABadVersion_IsRefused(string version)
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("NeonSidekick.exe"), version: version), out _));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("a//b.dll")]
    [InlineData("./a.dll")]
    [InlineData("a.dll.")]
    [InlineData("a /b.dll")]
    [InlineData(".update/swap.json")]
    [InlineData(".UPDATE/swap.json")]
    [InlineData("UPDATE~1/swap.json")]
    [InlineData("update~12/a.dll")]
    [InlineData("update-manifest.json")]
    [InlineData("release/x.dll")]
    public void APathOutsideThePackagesOwn_IsRefused(string path)   // UPDATE~n is .update's own short name (review P13-10)
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("NeonSidekick.exe") + "," + Entry(path)), out string? problem));
        Assert.Equal(UpdateText.ManifestBadEntry(path), problem);
    }

    [Fact]
    public void ABackslashPath_IsRefused()
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("NeonSidekick.exe") + "," + Entry(@"a\\b.dll")), out _));
    }

    [Fact]
    public void TheSameFileTwice_IsRefused_InAnyCase()
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("NeonSidekick.exe") + "," + Entry("a.dll") + "," + Entry("A.DLL")), out _));
    }

    [Fact]
    public void AnExeItDoesNotList_OrInAFolder_IsRefused()
    {
        Assert.Null(UpdateManifest.Read(Json(Entry("a.dll")), out _));
        Assert.Null(UpdateManifest.Read(Json(Entry("bin/NeonSidekick.exe"), exe: "bin/NeonSidekick.exe"), out _));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ABadHash_IsRefused(string hash)
    {
        string files = $$"""{"path":"NeonSidekick.exe","sha256":"{{hash}}"}""";
        Assert.Null(UpdateManifest.Read(Json(files), out _));
    }

    [Theory]
    [InlineData("0.5.10", "0.5.9", 1)]
    [InlineData("0.6.0", "0.5.99", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.5.7", "0.5.7", 0)]
    [InlineData("0.5.7", "0.5.8", -1)]
    public void Versions_CompareByNumber(string left, string right, int sign)
    {
        Assert.True(UpdateVersion.TryParse(left, out var a));
        Assert.True(UpdateVersion.TryParse(right, out var b));
        Assert.Equal(sign, Math.Sign(a.CompareTo(b)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("-1.2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3-beta")]
    [InlineData(" 1.2.3")]
    [InlineData("99999999999.0.0")]
    public void NotAVersion(string text) => Assert.False(UpdateVersion.TryParse(text, out _));

    [Fact]
    public void TheShaFile_IsPinned()
    {
        // A lasting contract, and what the signature covers: this exact text.
        string text = UpdateShaFile.Text(Hash, "NeonSidekick-v0.5.7-win-x64.zip", new string('b', 64));
        Assert.Equal(Hash + " *NeonSidekick-v0.5.7-win-x64.zip\n# " + new string('b', 64) + " *update-manifest.json\n", text);
        var read = UpdateShaFile.TryParse(text);
        Assert.Equal(new UpdateShaFile(Hash, "NeonSidekick-v0.5.7-win-x64.zip", new string('b', 64)), read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc *a.zip\n")]
    public void AShaFileWithoutTheManifestLine_CannotBeInstalled(string text) => Assert.Null(UpdateShaFile.TryParse(text));

    [Fact]
    public void AReleaseFromBeforeTheUpdater_CannotBeInstalled()
    {
        Assert.Null(UpdateShaFile.TryParse(Hash + " *NeonSidekick-v0.5.6-win-x64.zip\n"));
    }

    [Fact]
    public void AShaFileNamingAnotherManifest_CannotBeInstalled()
    {
        Assert.Null(UpdateShaFile.TryParse(Hash + " *a.zip\n# " + Hash + " *other.json\n"));
    }
}
