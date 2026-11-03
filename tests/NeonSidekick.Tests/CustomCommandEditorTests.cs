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
        Assert.Equal("Costs $5, every $ARGUMENTS word.\n---\nPR #42: done", CustomCommandTemplate.Expand(command.Body, "", command.Arguments));
    }

    /// <summary>
    /// A write that fails after the file was made leaves no file and says it failed (2026-11-02, review P13-4: a cut command stayed, read as
    /// "exists", and every retry said so); a retry then saves.
    /// </summary>
    [Fact]
    public void ASaveWhoseWriteFails_LeavesNoFile_AndIsAFailure_NotExists()
    {
        var open = CustomCommandEditor.OpenNew;
        try
        {
            CustomCommandEditor.OpenNew = path => new FailingStream(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));
            var failed = CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "cut", "some text");
            Assert.Equal(CustomCommandEditOutcome.Failed, failed.Outcome);
            Assert.False(File.Exists(Path.Combine(_roots.Profile, "cut.md")));
        }
        finally
        {
            CustomCommandEditor.OpenNew = open;
        }

        Assert.Equal(CustomCommandEditOutcome.Saved, CustomCommandEditor.Save(_roots, CustomCommandScope.Profile, "cut", "some text").Outcome);
    }

    /// <summary>A stream whose writes fail as a full disk's do, its file left made.</summary>
    private sealed class FailingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("There is not enough space on the disk.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
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

        Assert.Equal(CustomCommandEditOutcome.Copied, CustomCommandEditor.CopyInto(_roots.Profile, source, target, "sub/x.md", overwrite: false).Outcome);
        File.WriteAllText(Path.Combine(target, "sub", "x.md"), "Theirs.");
        Assert.Equal(CustomCommandEditOutcome.Exists, CustomCommandEditor.CopyInto(_roots.Profile, source, target, "sub/x.md", overwrite: false).Outcome);
        Assert.Equal("Theirs.", File.ReadAllText(Path.Combine(target, "sub", "x.md")));
        Assert.Equal(CustomCommandEditOutcome.Copied, CustomCommandEditor.CopyInto(_roots.Profile, source, target, "sub/x.md", overwrite: true).Outcome);
        Assert.Equal("Mine.", File.ReadAllText(Path.Combine(target, "sub", "x.md")));
    }

    /// <summary>A file there in another case is the one command (2026-10-31, review P5-5, P7-1): found segment by segment, as it is on disk.</summary>
    [Fact]
    public void Existing_FindsAPath_InAnyCase()
    {
        string file = Path.Combine(_roots.Profile, "Front", "Card.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(CustomCommandEditor.Existing(_roots.Profile, "front/card.md")!));
        Assert.True(CustomCommandEditor.Taken(_roots.Profile, "front:card"));
        Assert.Null(CustomCommandEditor.Existing(_roots.Profile, "front/other.md"));
        Assert.Null(CustomCommandEditor.Existing(_roots.Profile, "card.md/front.md"));
    }

    /// <summary>The copy replaces the one there in its own case, and never takes a source that is no plain file under its root (review P6-8).</summary>
    [Fact]
    public void CopyInto_Overwrite_ReplacesTheOneThere_InItsCase_AndRefusesASourceOutsideItsRoot()
    {
        string source = Path.Combine(_roots.Profile, "x.md");
        File.WriteAllText(source, "Mine.");
        string target = Path.Combine(_dir, "other", "commands");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "X.md"), "Theirs.");

        var result = CustomCommandEditor.CopyInto(_roots.Profile, source, target, "x.md", overwrite: true);

        Assert.Equal(CustomCommandEditOutcome.Copied, result.Outcome);
        Assert.Equal("Mine.", File.ReadAllText(Directory.GetFiles(target).Single()));
        Assert.Equal(CustomCommandEditOutcome.Missing, CustomCommandEditor.CopyInto(_roots.Global, source, target, "x.md", overwrite: true).Outcome);
    }

    /// <summary>
    /// Two folders that are one are a SharedFolder (2026-10-31, review P7-2), an info notice, from all three acts; a rename to the same name
    /// stays NothingToChange.
    /// </summary>
    [Fact]
    public void AMoveOrCopy_IntoTheSameFolder_IsSharedFolder()
    {
        string file = Path.Combine(_roots.Profile, "x.md");
        File.WriteAllText(file, "x");
        var same = new CustomCommandRoots(_roots.Profile, _roots.Profile);

        Assert.Equal(CustomCommandEditOutcome.SharedFolder, CustomCommandEditor.Move(same, CustomCommandScope.Profile, file, CustomCommandScope.Global).Outcome);
        Assert.Equal(CustomCommandEditOutcome.SharedFolder, CustomCommandEditor.MoveToProfile(_roots, CustomCommandScope.Profile, file, _roots.Profile).Outcome);
        Assert.Equal(CustomCommandEditOutcome.SharedFolder, CustomCommandEditor.CopyToProfile(_roots, CustomCommandScope.Profile, file, _roots.Profile).Outcome);
        Assert.Equal(CustomCommandEditOutcome.NothingToChange, CustomCommandEditor.Rename(_roots, CustomCommandScope.Profile, file, "x").Outcome);
        Assert.Equal(CustomCommandEditOutcome.NothingToChange, CustomCommandEditor.Move(_roots, CustomCommandScope.Profile, file, CustomCommandScope.Profile).Outcome);
    }

    /// <summary>A file that is itself a link is named as one (2026-10-31, review P4-3), not by its folder.</summary>
    [Fact]
    public void DeletingALinkFile_IsLinked_TheFileNamed()
    {
        string real = Path.Combine(_dir, "real.md");
        File.WriteAllText(real, "x");
        string link = Path.Combine(_roots.Profile, "x.md");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // no symlink right on this machine
        }

        var result = CustomCommandEditor.Delete(_roots, CustomCommandScope.Profile, link, null);

        Assert.Equal(CustomCommandEditOutcome.Linked, result.Outcome);
        Assert.True(result.LinkIsFile);
        Assert.Contains("is a link", NeonSidekick.App.CustomCommandsMenu.Refusal(result), StringComparison.Ordinal);
        Assert.True(File.Exists(real));
    }

    /// <summary>A description cut never splits an emoji (review P3-5), and one that needs quotes reads back as written (review P4-9).</summary>
    [Theory]
    [InlineData("Use when: the user asks \"why\" and \\ more")]
    [InlineData("- a list line")]
    public void SavedFile_TheDescription_ReadsBack(string line)
    {
        string content = CustomCommandEditor.SavedFile(line + "\nmore", out _);
        var text = CustomCommandFile.Read(content);

        Assert.Equal(line, text.Description);
    }

    [Fact]
    public void SavedFile_ACutDescription_KeepsAWholeEmoji()
    {
        string line = new string('a', CustomCommandEditor.SavedDescriptionLength - 1) + "😀 tail";
        var text = CustomCommandFile.Read(CustomCommandEditor.SavedFile(line, out _));

        Assert.DoesNotContain(System.Text.Rune.ReplacementChar, text.Description!.EnumerateRunes());
        Assert.EndsWith("…", text.Description, StringComparison.Ordinal);
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
