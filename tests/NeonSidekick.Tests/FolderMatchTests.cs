using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary>
/// The folder compare (2026-11-22, the review's #78): a folder must be fully qualified in this system's style. A folder written on the other
/// system, or a relative one, is never made full against this one: it normalizes to null and matches nothing, never a folder of this machine.
/// </summary>
public sealed class FolderMatchTests
{
    [WindowsFact]
    public void OnWindows_AMacFolder_AndARelativeOne_AreRefused()
    {
        Assert.Null(FolderMatch.Normal("/Users/x/Repo"));
        Assert.Null(FolderMatch.Normal("Repo"));
        Assert.Null(FolderMatch.Normal(@"\Repo"));   // rooted, but no drive
        Assert.False(FolderMatch.Same("/Users/x/Repo", @"C:\Users\x\Repo"));
        Assert.Equal(@"C:\Repo", FolderMatch.Normal(@"C:\Repo\"));
        Assert.Equal(@"\\server\share\Repo", FolderMatch.Normal(@"\\server\share\Repo"));
        Assert.True(FolderMatch.Same(@"c:\repo", @"C:\Repo\"));
    }

    [UnixFact]
    public void OnUnix_AWindowsFolder_AndARelativeOne_AreRefused()
    {
        Assert.Null(FolderMatch.Normal(@"C:\Users\x\Repo"));
        Assert.Null(FolderMatch.Normal("Repo"));
        Assert.False(FolderMatch.Same(@"C:\Users\x\Repo", "/Users/x/Repo"));
        Assert.Equal("/Users/x/Repo", FolderMatch.Normal("/Users/x/Repo/"));
    }

    [Fact]
    public void ABlankFolder_IsNull_AndNeverMatches()
    {
        Assert.Null(FolderMatch.Normal(null));
        Assert.Null(FolderMatch.Normal("  "));
        Assert.False(FolderMatch.Same(null, null));
    }
}
