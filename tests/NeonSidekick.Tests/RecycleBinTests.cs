using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>A real-bin test runs only with <c>NEONSIDEKICK_TEST_RECYCLE=1</c>: it leaves a temp file in the user's Recycle bin.</summary>
public sealed class RecycleBinFactAttribute : FactAttribute
{
    public const string Variable = "NEONSIDEKICK_TEST_RECYCLE";

    public RecycleBinFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Skip = "no Recycle bin on this system";
        }
        else if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = Variable + "=1 runs it (it leaves a temp file in the Recycle bin)";
        }
    }
}

/// <summary>
/// <c>File deletes to Recycle bin</c> (later on 2026-10-15): the checks before Windows' shell (from its documentation), the answers read whole,
/// a Mac's codes, the time cap, the one delete over a fake bin (to the bin, no bin on that drive, a refusal deleting nothing, none),
/// <c>delete</c>'s sentence and description, a share's working directory staying direct, the pictures', and the smoke's probe.
/// </summary>
public sealed class RecycleBinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRecycleBin _bin = new();

    public RecycleBinTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        PictureDeletes.Bin = null;
        PictureDeletes.ToRecycleBin = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void CheckWindows_RefusesARelativePath_ALongOne_AndTheLongPathPrefix_AndAShareHasNoBin()
    {
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNeedsFullPath), RecycleBin.CheckWindows(@"folder\a.png"));
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNeedsFullPath), RecycleBin.CheckWindows(@"C:a.png"));
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleTooLong), RecycleBin.CheckWindows(@"\\?\C:\a.png"));
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleTooLong), RecycleBin.CheckWindows(@"C:\" + new string('a', 260)));
        Assert.Equal(RecycleOutcome.NoBin, RecycleBin.CheckWindows(@"\\server\share\a.png")!.Outcome);
        Assert.Null(RecycleBin.CheckWindows(@"C:\pictures\a.png"));
        Assert.Contains("File deletes to " + FileText.RecycleBinName, FileText.RecycleTooLong, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAnswers_AreReadWhole_AndAMacsCodesMapped()
    {
        Assert.Equal(RecycleOutcome.Recycled, RecycleBin.MapWindows(0, aborted: false).Outcome);
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleCancelled), RecycleBin.MapWindows(0, aborted: true));   // the docs: 0 can come back on a cancel
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleRefused(2)), RecycleBin.MapWindows(2, aborted: false));
        Assert.Equal(RecycleOutcome.NoBin, RecycleBin.MapMac(RecycleBin.MacNoTrashCode, "unsupported").Outcome);
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, "You don't have permission."), RecycleBin.MapMac(513, " You don't have permission. "));
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleRefused(4)), RecycleBin.MapMac(4, null));
    }

    [Fact]
    public void RunCapped_ABinThatNeverAnswers_FailsAfterTheCap_AndAThrowIsAFailure()
    {
        using var never = new ManualResetEventSlim();
        var hung = RecycleBin.RunCapped(() => { never.Wait(); return new RecycleResult(RecycleOutcome.Recycled); }, TimeSpan.FromMilliseconds(100), sta: true);
        never.Set();

        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNoAnswer), hung);
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, "boom"), RecycleBin.RunCapped(() => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5), sta: false));
        Assert.Equal(RecycleOutcome.Recycled, RecycleBin.RunCapped(() => new RecycleResult(RecycleOutcome.Recycled), TimeSpan.FromSeconds(5), sta: true).Outcome);
    }

    [Fact]
    public void Remove_ToTheBin_NoBinDeletesDirectly_ARefusalDeletesNothing_AndNoneIsDirect()
    {
        string recycled = Put("a.png");
        Assert.Equal(DeleteWay.Recycled, FileDeletes.Remove(recycled, false, false, _bin));
        Assert.Equal([recycled], _bin.Moved);

        string noBin = Put("b.png");
        _bin.Answer = new RecycleResult(RecycleOutcome.NoBin);
        Assert.Equal(DeleteWay.DeletedNoBin, FileDeletes.Remove(noBin, false, false, _bin));
        Assert.False(File.Exists(noBin));

        string refused = Put("c.png");
        _bin.Answer = new RecycleResult(RecycleOutcome.Failed, "the bin is full");
        Assert.Equal("the bin is full", Assert.Throws<IOException>(() => FileDeletes.Remove(refused, false, false, _bin)).Message);
        Assert.True(File.Exists(refused));   // never deleted behind a refusal

        string direct = Put("d.png");
        Assert.Equal(DeleteWay.Deleted, FileDeletes.Remove(direct, false, false, null));
        Assert.False(File.Exists(direct));

        string link = Put("e.png");
        int before = _bin.Moved.Count;
        Assert.Equal(DeleteWay.Deleted, FileDeletes.Remove(link, false, isLink: true, _bin));   // a link always goes directly
        Assert.Equal(before, _bin.Moved.Count);
    }

    [Fact]
    public async Task TheDeleteTool_ToTheBin_SaysSo_ItsDescriptionFollows_AndAShareStaysDirect()
    {
        var settings = new AppSettingsData();
        string root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(Path.Combine(root, "pics"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(root, "pics", "b.png"), "x");
        File.WriteAllText(Path.Combine(root, "c.txt"), "x");
        var files = new WorkingDirectory(() => root, TimeProvider.System) { RecycleDeletes = () => settings.FileDeletesToRecycleBin ? _bin : null };
        var delete = ChatScreen.FileTools(files, () => true, _ => { }, () => settings).OfType<DeleteTool>().Single();

        Assert.Equal(DeleteTool.RecycleDescriptionText, delete.Description);
        Assert.Equal("moved a.txt to the " + FileText.RecycleBinName, delete.Describe("a.txt"));
        Assert.Equal("moved the folder pics" + Path.DirectorySeparatorChar + " and everything in it to the " + FileText.RecycleBinName, delete.Describe("pics"));
        Assert.Equal([Path.Combine(root, "a.txt"), Path.Combine(root, "pics")], _bin.Moved);   // full paths

        settings.FileDeletesToRecycleBin = false;
        Assert.Equal(DeleteTool.DescriptionText, delete.Description);
        Assert.Equal("deleted c.txt", delete.Describe("c.txt"));
        Assert.Equal(2, _bin.Moved.Count);

        settings.FileDeletesToRecycleBin = true;
        _bin.Answer = new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNoAnswer);
        File.WriteAllText(Path.Combine(root, "d.txt"), "x");
        Assert.Contains(FileText.RecycleNoAnswer, delete.Describe("d.txt"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "d.txt")));

        // A share's working directory (unc_delete's) is built without a bin: direct, as ever.
        var share = new WorkingDirectory(() => root, TimeProvider.System, WorkingDirectoryOptions.Share);
        Assert.Equal(DeleteWay.Deleted, share.Delete("d.txt").Way);
        Assert.Contains("for good", DeleteTool.DescriptionText, StringComparison.Ordinal);
        Assert.Contains("its description says whether it goes for good", Llm.Assistant.FileRule, StringComparison.Ordinal);   // the rule leaves where to the description
        await Task.CompletedTask;
    }

    [Fact]
    public void FileText_Deleted_NamesWhereItWent()
    {
        Assert.Equal("deleted x.txt (no " + FileText.RecycleBinName + " on that drive)", FileText.Deleted(new DeleteResult(FileOutcome.Ok, "x.txt", false) { Way = DeleteWay.DeletedNoBin }));
        Assert.Equal("deleted x.txt", FileText.Deleted(new DeleteResult(FileOutcome.Ok, "x.txt", false)));
    }

    [Fact]
    public void ThePictures_FollowTheSetting()
    {
        string a = Put("a.png");
        string b = Put("b.png");
        bool on = true;
        PictureDeletes.Bin = _bin;
        PictureDeletes.ToRecycleBin = () => on;

        Assert.True(PictureMenu.Delete(a));
        Assert.Equal([a], _bin.Moved);
        Assert.Equal("(🖼️ moved a.png to the " + FileText.RecycleBinName + ")", PictureMenuText.Deleted("a.png", DeleteWay.Recycled));

        on = false;
        Assert.True(PictureMenu.Delete(b));
        Assert.False(File.Exists(b));
        Assert.Single(_bin.Moved);
        Assert.Equal("(🖼️ deleted b.png)", PictureMenuText.Deleted("b.png"));
    }

    [Fact]
    public void TheSettingsName_IsTheSystemsBin()
    {
        Assert.Equal("File deletes to " + (OperatingSystem.IsMacOS() ? "Trash" : "Recycle bin"), SettingsMenu.FieldName(SettingsField.FileDeletesToRecycleBin));
        Assert.True(new AppSettingsData().FileDeletesToRecycleBin);
        Assert.Equal(SettingsField.FileDeletesToRecycleBin, SettingsMenu.ToolsTabFields[ToolsText.MenuIndex(ToolsText.FileMenuTitle)][1]);   // the File menu's second row
    }

    [WindowsFact]
    public void TheSmokesProbe_CallsTheShell_OnAMissingFile_AndPasses()
    {
        var check = SmokeChecks.ProbeRecycleBin();

        Assert.True(check.Passed, check.Detail);
        Assert.Equal("files:recycle-bin", check.Name);
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void TheShellsBin_AShareAndARelativePath_NeverReachTheShell()
    {
        var bin = new WindowsRecycleBin();

        Assert.Equal(RecycleOutcome.NoBin, bin.Move(@"\\localhost\c$\nothing-here.png").Outcome);
        Assert.Equal(RecycleOutcome.Failed, bin.Move(@"relative.png").Outcome);
    }

    [RecycleBinFact]
    public void TheSystemsBin_TakesATempFile()
    {
        string path = Put("neonsidekick-recycle-test.txt");
        var bin = RecycleBin.Platform()!;

        var result = bin.Move(path);

        Assert.Equal(RecycleOutcome.Recycled, result.Outcome);
        Assert.False(File.Exists(path));
    }
}
