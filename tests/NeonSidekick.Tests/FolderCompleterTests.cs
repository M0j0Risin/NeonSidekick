using NeonSidekick.Files;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary><c>/cwd</c>'s list of any drive's folders (2026-10-05, the user's ask), over a temp folder on the local disk.</summary>
public sealed class FolderCompleterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public FolderCompleterTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Repo"));
        Directory.CreateDirectory(Path.Combine(_dir, "reports"));
        Directory.CreateDirectory(Path.Combine(_dir, "Temp", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var hidden = Directory.CreateDirectory(Path.Combine(_dir, "Secret"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(_dir, "readme.txt"), "");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void AFolder_ListsItsSubfolders_OneLevel_ByName_AFileAndTheHiddenLeftOut()
    {
        string folder = _dir + @"\";
        var found = FolderCompleter.Complete(folder);
        Assert.Equal(FileOutcome.Ok, found.Outcome);
        Assert.Equal([folder + @"Repo\", folder + @"reports\", folder + @"Temp\"], found.Paths);   // no deep\, no readme.txt
        Assert.False(found.Truncated);
        // The folder picker's Show hidden: the dot-folder and the hidden one too.
        Assert.Equal([folder + @".git\", folder + @"Repo\", folder + @"reports\", folder + @"Secret\", folder + @"Temp\"], FolderCompleter.Complete(folder, FileBrowserVisibility.ShowHidden).Paths);
    }

    [Fact]
    public void APrefix_Narrows_IgnoringCase_AndTheSeparatorTypedIsKept()
    {
        string folder = _dir + @"\";
        Assert.Equal([folder + @"Repo\", folder + @"reports\"], FolderCompleter.Complete(folder + "RE").Paths);
        Assert.Equal([folder + @"Temp\deep\"], FolderCompleter.Complete(folder + @"Temp\").Paths);
        string slashed = _dir.Replace('\\', '/') + "/";
        Assert.Equal([slashed + "Temp/"], FolderCompleter.Complete(slashed + "te").Paths);
    }

    [Fact]
    public void Nothing_ForAMissingFolder_AFile_ARelativePath_OrAShare()
    {
        Assert.Empty(FolderCompleter.Complete(_dir + @"\nowhere\").Paths);
        Assert.Empty(FolderCompleter.Complete(_dir + @"\readme.txt\").Paths);
        Assert.Empty(FolderCompleter.Complete(_dir + @"\zzz").Paths);
        Assert.Empty(FolderCompleter.Complete(@"docs\").Paths);
        Assert.Empty(FolderCompleter.Complete(@"\\server\share\").Paths);
        Assert.Empty(FolderCompleter.Complete("").Paths);
    }

    [Fact]
    public void ABareDrive_OffersItsRoot()
    {
        string drive = Path.GetPathRoot(_dir)![..2];   // C:
        Assert.Equal([drive + @"\"], FolderCompleter.Complete(drive).Paths);
        Assert.True(FolderCompleter.IsDrivePath("d:") && FolderCompleter.IsDrivePath(@"D:\x") && FolderCompleter.IsDrivePath("D:/"));
        Assert.False(FolderCompleter.IsDrivePath("D") || FolderCompleter.IsDrivePath("Dx") || FolderCompleter.IsDrivePath("D:x") || FolderCompleter.IsDrivePath("~") || FolderCompleter.IsDrivePath(@"\\s\x"));
    }

    [Fact]
    public void TheCap_CutsTheList_AndSaysSo()
    {
        string many = Path.Combine(_dir, "many");
        for (int i = 0; i < WorkingDirectory.MaxMentionMatches + 5; i++)
        {
            Directory.CreateDirectory(Path.Combine(many, "f" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var found = FolderCompleter.Complete(many + @"\");
        Assert.Equal(WorkingDirectory.MaxMentionMatches, found.Paths.Count);
        Assert.True(found.Truncated);
        Assert.Equal(many + @"\f000\", found.Paths[0]);
    }

    /// <summary>A link's target that could stall a keystroke's read (2026-10-06, the code review's catch): a share in any spelling.</summary>
    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\folder", true)]
    [InlineData(@"//server/share", true)]
    [InlineData(@"\\?\UNC\server\share", true)]
    [InlineData(@"\??\UNC\server\share", true)]
    [InlineData(@"UNC\server\share", true)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"\??\C:\Windows", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    public void IsSlowTarget_IsAShare(string target, bool slow)
    {
        Assert.Equal(slow, FolderCompleter.IsSlowTarget(target));
    }

    /// <summary>
    /// A symbolic link under a local folder that points at a share lists nothing, and nothing past it is read. Making a symbolic
    /// link needs Developer Mode or an elevated process; without either the test has nothing to prove and passes.
    /// </summary>
    [Fact]
    public void ALinkToAShare_ListsNothing()
    {
        string link = Path.Combine(_dir, "share-link");
        try
        {
            Directory.CreateSymbolicLink(link, @"\\neonsidekick-no-such-host.invalid\share");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Empty(FolderCompleter.Complete(link + @"\").Paths);
        Assert.Empty(FolderCompleter.Complete(link + @"\sub\").Paths);
        Assert.Contains(_dir + @"\share-link\", FolderCompleter.Complete(_dir + @"\sh").Paths);   // the link itself still lists in its folder
    }
}
