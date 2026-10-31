using System.Text;
using NeonSidekick.CustomCommands;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The command files written, moved and deleted (2026-10-30, the custom commands plan): UTF-8 without a BOM and <c>\n</c> for a new file, a
/// user's bytes kept on a rename or move, refusals for built-ins, taken names, links and missing files, and a save that reads back as sent.
/// </summary>
public sealed class CustomCommandEditorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neon-cmded-" + Guid.NewGuid().ToString("N"));
    private readonly CustomCommandRoots _roots;

    public CustomCommandEditorTests()
    {
        _roots = new CustomCommandRoots(Path.Combine(_dir, "profile", "commands"), Path.Combine(_dir, "home", "commands"));
        Directory.CreateDirectory(_roots.Profile);
        Directory.CreateDirectory(_roots.Global);
    }

    public void Dispose() => Junction.DeleteTree(_dir);

    private CustomCommandScan Scan() => new CustomCommandCatalog(() => _roots).Scan();

    [Fact]
    public void Create_WritesTheTemplate_Utf8WithoutABom_AndLineFeedsOnly()
    {
        var result = CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "triage");

        Assert.Equal(CustomCommandEditOutcome.Created, result.Outcome);
        byte[] bytes = File.ReadAllBytes(result.Path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal(CustomCommandText.Template("triage"), Encoding.UTF8.GetString(bytes));
        Assert.Equal(CustomCommandText.StillTemplateWarning, Scan().Find("triage")!.Warning);
    }

    [Fact]
    public void Create_MakesAGoneRoot_AndASubfolderForAColonName()
    {
        Directory.Delete(_roots.Profile);
        var result = CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "frontend:component");

        Assert.Equal(CustomCommandEditOutcome.Created, result.Outcome);
        Assert.Equal(Path.Combine(_roots.Profile, "frontend", "component.md"), result.Path);
        Assert.Equal("frontend:component", Assert.Single(Scan().Commands).Name);
    }

    [Fact]
    public void Refusals_ABuiltIn_ATakenName_ABadOne()
    {
        Assert.Equal(CustomCommandEditOutcome.BuiltIn, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "clear").Outcome);
        Assert.Equal(CustomCommandEditOutcome.BadName, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "Bad Name").Outcome);
        Assert.Equal(CustomCommandEditOutcome.BadName, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "con").Outcome);
        File.WriteAllText(Path.Combine(_roots.Profile, "Review.md"), "theirs");
        Assert.Equal(CustomCommandEditOutcome.Exists, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "review").Outcome);   // any case
        Assert.Equal("theirs", File.ReadAllText(Path.Combine(_roots.Profile, "Review.md")));   // never overwritten
        // A profile command over a global one of the same name is allowed.
        File.WriteAllText(Path.Combine(_roots.Global, "triage.md"), "global");
        Assert.Equal(CustomCommandEditOutcome.Created, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "triage").Outcome);
    }

    [Fact]
    public void ALinkedSubfolder_IsNeverWrittenThrough()
    {
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        MakeLink(Path.Combine(_roots.Profile, "a"), elsewhere);

        Assert.Equal(CustomCommandEditOutcome.Linked, CustomCommandEditor.Create(_roots, CustomCommandScope.Profile, "a:b").Outcome);
        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public void Save_ReadsBackAsTheTextItWas()
    {
        string text = "Costs $5, every $ARGUMENTS word.\r\n---\r\nPR #42: done";
        var result = CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "again", text);

        Assert.Equal(CustomCommandEditOutcome.Saved, result.Outcome);
        Assert.False(result.MayReadDifferently);
        Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(result.Path));
        var command = Scan().Find("again")!;
        Assert.Equal("Costs $5, every $ARGUMENTS word.", command.Description);
        Assert.Equal("Costs $5, every $ARGUMENTS word.\n---\nPR #42: done", CustomCommandTemplate.Expand(command.Body, "", command.Arguments).Text);
    }

    [Fact]
    public void Save_SaysWhenABackslashedPlaceholderMayReadDifferently()
    {
        Assert.True(CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "odd", "keep \\$1 here").MayReadDifferently);
    }

    [Fact]
    public void Save_RefusesBlankAndOversizeText()
    {
        Assert.Equal(CustomCommandEditOutcome.Blank, CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "x", " \n ").Outcome);
        Assert.Equal(CustomCommandEditOutcome.TooLarge, CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "x", new string('y', CustomCommandFile.MaxBytes)).Outcome);
    }

    [Fact]
    public void RenameAndMove_KeepTheFilesBytes_AndAnEmptiedFolderGoes()
    {
        string folder = Path.Combine(_roots.Profile, "tools");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "wide.md");
        File.WriteAllText(file, "UTF-16 text", Encoding.Unicode);
        byte[] bytes = File.ReadAllBytes(file);

        var renamed = CustomCommandEditor.Rename(_roots, CustomCommandScope.Profile, file, "wide2");
        Assert.Equal(CustomCommandEditOutcome.Renamed, renamed.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(renamed.Path));
        Assert.False(Directory.Exists(folder));   // emptied by the rename: removed

        var moved = CustomCommandEditor.Move(_roots, CustomCommandScope.Profile, renamed.Path, CustomCommandScope.Global);
        Assert.Equal(CustomCommandEditOutcome.Moved, moved.Outcome);
        Assert.Equal(Path.Combine(_roots.Global, "wide2.md"), moved.Path);
        Assert.Equal(bytes, File.ReadAllBytes(moved.Path));
    }

    [Fact]
    public void AFolderHoldingADotFile_IsKeptWhenEmptiedOfCommands()
    {
        string folder = Path.Combine(_roots.Profile, "kept");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ".keep"), "");
        string file = Path.Combine(folder, "one.md");
        File.WriteAllText(file, "One.");

        Assert.Equal(CustomCommandEditOutcome.Renamed, CustomCommandEditor.Rename(_roots, CustomCommandScope.Profile, file, "two").Outcome);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Delete_GoesThroughTheBin()
    {
        string file = Path.Combine(_roots.Profile, "gone.md");
        File.WriteAllText(file, "Gone.");
        var bin = new FakeRecycleBin();

        var result = CustomCommandEditor.Delete(_roots, CustomCommandScope.Profile, file, bin);

        Assert.Equal(CustomCommandEditOutcome.Deleted, result.Outcome);
        Assert.Equal(Path.GetFullPath(file), Assert.Single(bin.Moved));
        Assert.Equal(CustomCommandEditOutcome.Missing, CustomCommandEditor.Delete(_roots, CustomCommandScope.Profile, Path.Combine(_roots.Profile, "never.md"), bin).Outcome);
    }

    [Fact]
    public void CopyInto_SkipsATakenFile_UnlessOverwrite()
    {
        string source = Path.Combine(_roots.Profile, "sub", "x.md");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "Mine.");
        string target = Path.Combine(_dir, "other", "commands");

        Assert.Equal(CustomCommandEditOutcome.Copied, CustomCommandEditor.CopyInto(source, target, "sub/x.md", overwrite: false).Outcome);
        File.WriteAllText(Path.Combine(target, "sub", "x.md"), "Theirs.");
        Assert.Equal(CustomCommandEditOutcome.Exists, CustomCommandEditor.CopyInto(source, target, "sub/x.md", overwrite: false).Outcome);
        Assert.Equal("Theirs.", File.ReadAllText(Path.Combine(target, "sub", "x.md")));
        Assert.Equal(CustomCommandEditOutcome.Copied, CustomCommandEditor.CopyInto(source, target, "sub/x.md", overwrite: true).Outcome);
        Assert.Equal("Mine.", File.ReadAllText(Path.Combine(target, "sub", "x.md")));
    }

    private static void MakeLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Junction.Make(link, target);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}
