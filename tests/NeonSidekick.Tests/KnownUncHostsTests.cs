using NeonSidekick.Files;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// 2026-10-22, review K7 (the fix plan's D13): a pasted UNC path is probed only on a configured share's server or a mapped drive's host,
/// so pasting a stranger's <c>\\host\s\cat.png</c> never opens SMB (and NTLM) to it; it stays text with a note.
/// </summary>
public sealed class KnownUncHostsTests : IDisposable
{
    public void Dispose() => KnownUncHosts.Configure(null);

    [Theory]
    [InlineData(@"\\fs01\eng\a.png", "fs01")]
    [InlineData("//fs01/eng/a.png", "fs01")]
    [InlineData(@"\\?\UNC\fs01\eng\a.png", "fs01")]
    [InlineData(@"\\dav.example@SSL\DavWWWRoot\a.png", "dav.example")]
    [InlineData(@"\\fs01", "fs01")]
    public void HostOf_ReadsAUncHost(string path, string host) => Assert.Equal(host, KnownUncHosts.HostOf(path));

    [Theory]
    [InlineData(@"C:\pics\a.png")]
    [InlineData(@"\\?\C:\pics\a.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("a.png")]
    [InlineData(@"\\")]
    public void HostOf_IsNullForAnyOtherPath(string path) => Assert.Null(KnownUncHosts.HostOf(path));

    [WindowsFact]
    public void APastedPath_OnAnUnknownHost_StaysText()
    {
        KnownUncHosts.Configure(() => [@"\\fs01\eng"]);
        Assert.True(KnownUncHosts.IsUnknown(@"\\attacker.example\s\cat.png"));
        Assert.False(KnownUncHosts.IsUnknown(@"\\FS01\eng\cat.png"));
        Assert.False(KnownUncHosts.IsUnknown(@"C:\cat.png"));

        Assert.False(ImageFile.TryPastedPaths(@"\\attacker.example\s\cat.png", out _));
        Assert.Equal("attacker.example", ImageFile.UnknownUncHost(@"see ""\\attacker.example\s\cat.png"""));
        Assert.Null(ImageFile.UnknownUncHost(@"\\fs01\eng\cat.png"));
        Assert.Null(ImageFile.UnknownUncHost(@"\\attacker.example\s\notes.txt"));   // not a picture: never probed anyway
    }

    [WindowsFact]
    public void Unset_KnowsNoConfiguredHost()
    {
        KnownUncHosts.Configure(null);
        Assert.True(KnownUncHosts.IsUnknown(@"\\nobody-configured.invalid\s\cat.png"));
    }
}
