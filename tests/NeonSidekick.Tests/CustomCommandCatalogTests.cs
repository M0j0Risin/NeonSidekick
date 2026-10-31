using System.Text;
using NeonSidekick.CustomCommands;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The two <c>commands</c> folders read (2026-10-30, the custom commands plan): the profile's over the global's, a built-in over both, bad
/// names, links, deep folders and unreadable files as Skipped rows, dot-entries passed over in silence, and a scan that never throws.
/// </summary>
public sealed class CustomCommandCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "neon-cmds-" + Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly string _global;

    public CustomCommandCatalogTests()
    {
        _profile = Path.Combine(_dir, "profile", "commands");
        _global = Path.Combine(_dir, "home", "commands");
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_global);
    }

    public void Dispose() => Junction.DeleteTree(_dir);

    private CustomCommandCatalog Catalog(string? profile = null, string? global = null) =>
        new(() => new CustomCommandRoots(profile ?? _profile, global ?? _global));

    private static string Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void TheProfilesFile_WinsOverTheGlobalOne()
    {
        Write(_profile, "review.md", "Profile review.");
        Write(_global, "review.md", "Global review.");
        Write(_global, "triage.md", "Triage.");

        var scan = Catalog().Scan();

        Assert.Equal(["review", "triage"], scan.Commands.Select(c => c.Name));
        Assert.Equal(CustomCommandScope.Profile, scan.Find("/review")!.Scope);
        Assert.Equal("Profile review.", scan.Find("REVIEW")!.Body);   // any case, with or without the slash
        var shadow = Assert.Single(scan.Shadowed);
        Assert.Equal(CustomCommandScope.Global, shadow.Command.Scope);
        Assert.Equal(CustomCommandScope.Profile, shadow.Winner!.Scope);
        Assert.Null(shadow.BuiltIn);
        Assert.True(scan.Knows("/triage"));
        Assert.False(scan.Knows("triage"));   // a word without its slash is no command line
    }

    [Fact]
    public void ABuiltInsName_IsShadowed_AndNamedOnceARun()
    {
        string clear = Write(_profile, "clear.md", "Clear it.");
        Write(_global, "help.md", "Help me.");
        var catalog = Catalog();

        var scan = catalog.Scan();

        Assert.Empty(scan.Commands);
        Assert.Equal(["/clear", "/help"], scan.Shadowed.Select(s => s.BuiltIn).Order(StringComparer.Ordinal));
        Assert.Equal(2, catalog.TakeNewlyShadowedByBuiltIn().Count);
        Assert.Empty(catalog.TakeNewlyShadowedByBuiltIn());

        // A changed file is named again.
        File.WriteAllText(clear, "Clear it, changed.");
        File.SetLastWriteTimeUtc(clear, DateTime.UtcNow.AddMinutes(5));
        catalog.Scan();
        Assert.Equal("/clear", Assert.Single(catalog.TakeNewlyShadowedByBuiltIn()).BuiltIn);
    }

    [Fact]
    public void ABadStem_IsSkipped_WithTheNameItCouldBe()
    {
        Write(_profile, "my review.md", "x");
        Write(_profile, "con.md", "x");

        var scan = Catalog().Scan();

        Assert.Empty(scan.Commands);
        Assert.Contains(scan.Problems, p => p.FilePath.EndsWith("my review.md", StringComparison.Ordinal) && p.Problem.EndsWith("rename it my-review.md", StringComparison.Ordinal));
        Assert.Contains(scan.Problems, p => p.FilePath.EndsWith("con.md", StringComparison.Ordinal) && p.Problem.Contains("device name", StringComparison.Ordinal));
        Assert.False(CustomCommandName.IsValid(CustomCommandName.Normalize("con")));   // the pane's slots refuse it too
    }

    [Fact]
    public void Names_AreLowercased_AndTheExtensionMatchedIgnoringCase()
    {
        Write(_profile, "Review.MD", "Upper.");
        Write(_profile, "notes.txt", "never read");

        var scan = Catalog().Scan();

        Assert.Equal("review", Assert.Single(scan.Commands).Name);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void Subfolders_NameCommandsWithColons_UpToFourDeep()
    {
        Write(_profile, "frontend/component.md", "Make a component.");
        Write(_profile, "a/b/c/d/deep.md", "Four folders.");
        Write(_profile, "a/b/c/d/e/deeper.md", "Five folders.");

        var scan = Catalog().Scan();

        Assert.Equal(["a:b:c:d:deep", "frontend:component"], scan.Commands.Select(c => c.Name));
        Assert.Equal(CustomCommandText.TooDeepProblem, Assert.Single(scan.Problems).Problem);
        Assert.Equal(Path.Combine("frontend", "component.md"), CustomCommandName.RelativePath("frontend:component"));
    }

    [Fact]
    public void DotFilesAndFolders_ArePassedOverInSilence()
    {
        Write(_profile, ".git/HEAD.md", "x");
        Write(_profile, "._review.md", "AppleDouble");
        Write(_profile, "review.md", "Real.");

        var scan = Catalog().Scan();

        Assert.Equal("review", Assert.Single(scan.Commands).Name);
        Assert.Empty(scan.Problems);
    }

    [Fact]
    public void MissingFolders_AreNone()
    {
        var scan = Catalog(Path.Combine(_dir, "nowhere"), Path.Combine(_dir, "nowhere-else")).Scan();
        Assert.Empty(scan.Commands);
        Assert.Empty(scan.Problems);
        Assert.Empty(scan.Shadowed);
    }

    [Fact]
    public void AFileOverTheCap_IsSkipped()
    {
        Write(_profile, "huge.md", new string('x', CustomCommandFile.MaxBytes + 1));
        Assert.Equal(CustomCommandText.TooLargeProblem, Assert.Single(Catalog().Scan().Problems).Problem);
    }

    [Fact]
    public void AnEditedFile_IsReadAgain()
    {
        string path = Write(_profile, "review.md", "---\ndescription: first\n---\nx");
        var catalog = Catalog();
        Assert.Equal("first", catalog.Scan().Commands[0].Description);

        File.WriteAllText(path, "---\ndescription: second take\n---\nx");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal("second take", catalog.Scan().Commands[0].Description);
    }

    [Fact]
    public void AUtf16File_IsDecoded()
    {
        File.WriteAllText(Path.Combine(_profile, "wide.md"), "---\ndescription: wide\n---\nUTF-16 body.", Encoding.Unicode);
        var command = Assert.Single(Catalog().Scan().Commands);
        Assert.Equal("wide", command.Description);
        Assert.Equal("UTF-16 body.", command.Body);
    }

    [Fact]
    public void TheSkippedKinds_AndTheWarnings()
    {
        Write(_profile, "hidden.md", "---\nuser-invocable: false\n---\nNever typed.");
        Write(_profile, "empty.md", "---\ndescription: nothing\n---\n\n");
        Write(_profile, "pr.md", "Diff: !`gh pr diff`");
        Write(_profile, "fresh.md", CustomCommandText.Template("fresh"));

        var scan = Catalog().Scan();

        Assert.Contains(scan.Problems, p => p.Name == "hidden" && p.Problem == CustomCommandText.NotUserInvocableProblem);
        Assert.Contains(scan.Problems, p => p.Name == "empty" && p.Problem == CustomCommandText.EmptyBodyProblem);
        var pr = scan.Find("pr")!;
        Assert.True(pr.HasShellLines);
        Assert.Equal(CustomCommandText.ShellLinesWarning, pr.Warning);
        Assert.Equal(CustomCommandText.StillTemplateWarning, scan.Find("fresh")!.Warning);
    }

    [Fact]
    public void ABrokenProfileFile_LetsTheGlobalOneRun_AndItsRowSaysSo()
    {
        Write(_profile, "review.md", "---\ndescription: broken\n---\n");
        Write(_global, "review.md", "Global review.");

        var scan = Catalog().Scan();

        Assert.Equal(CustomCommandScope.Global, scan.Find("review")!.Scope);
        var problem = Assert.Single(scan.Problems);
        Assert.Equal(CustomCommandText.EmptyBodyProblem + CustomCommandText.GlobalRunsInsteadNote("/review"), problem.Problem);
        Assert.Empty(scan.Shadowed);
    }

    [Fact]
    public void AFileHeldOpen_IsSkippedWithTheError_AndLoadedOnceReleased()
    {
        string path = Write(_profile, "held.md", "Held.");
        var catalog = Catalog();
        if (OperatingSystem.IsWindows())
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var held = catalog.Scan();
                Assert.Empty(held.Commands);
                Assert.Single(held.Problems);
            }
        }

        Assert.Equal("held", Assert.Single(catalog.Scan().Commands).Name);   // the failure was never cached
    }

    [Fact]
    public void CaseDuplicates_AreTheSecondOfEachName()
    {
        Assert.Equal(["review.md"], CustomCommandName.CaseDuplicates(["Review.md", "review.md", "triage.md"]));
        Assert.Empty(CustomCommandName.CaseDuplicates(["a/review.md", "review.md"]));
    }

    [Fact]
    public void CaseDuplicateFiles_OnACaseSensitiveFolder_SkipTheSecond()
    {
        Write(_profile, "Review.md", "One.");
        if (File.Exists(Path.Combine(_profile, "review.md")))
        {
            return;   // this folder ignores case (Windows, a default Mac disk): there is no second file to make
        }

        Write(_profile, "review.md", "Two.");
        var scan = Catalog().Scan();
        Assert.Single(scan.Commands);
        Assert.Contains(scan.Problems, p => p.Problem == CustomCommandText.CaseDuplicateProblem("/review"));
    }

    [Fact]
    public void ALinkedSubfolder_IsSkipped_AndALinkedRootRead()
    {
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Write(elsewhere, "linked.md", "Linked.");
        string sub = Path.Combine(_profile, "shared");
        string root = Path.Combine(_dir, "linked-root");
        MakeLink(sub, elsewhere);
        MakeLink(root, elsewhere);

        var scan = Catalog().Scan();
        Assert.Empty(scan.Commands);
        Assert.Equal(CustomCommandText.LinkProblem, Assert.Single(scan.Problems).Problem);

        var linked = Catalog(profile: root).Scan();
        Assert.Equal("linked", Assert.Single(linked.Commands).Name);
    }

    [Fact]
    public void TwoRootsLinkedToOneFolder_AreReadOnce_AsTheProfiles()
    {
        string shared = Path.Combine(_dir, "shared-commands");
        Write(shared, "review.md", "Shared.");
        string profile = Path.Combine(_dir, "p-link");
        string global = Path.Combine(_dir, "g-link");
        MakeLink(profile, shared);
        MakeLink(global, shared);

        var scan = Catalog(profile, global).Scan();

        Assert.Equal(CustomCommandScope.Profile, Assert.Single(scan.Commands).Scope);
        Assert.Empty(scan.Shadowed);
        Assert.Equal(CustomCommandText.SameFolderNote, Assert.Single(scan.Problems).Problem);
        Assert.True(new CustomCommandRoots(profile, global).Same);
    }

    [Fact]
    public void Recent_ReusesAScanForASecond()
    {
        var time = new ManualTimeProvider();
        var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(_profile, _global), time);
        Assert.Empty(catalog.Recent().Commands);

        Write(_profile, "late.md", "Late.");
        Assert.Empty(catalog.Recent().Commands);
        time.Advance(CustomCommandCatalog.ReuseWindow);
        Assert.Equal("late", Assert.Single(catalog.Recent().Commands).Name);
    }

    [Fact]
    public void Knows_ScansAgainOnAMiss_InsideTheReuseWindow()
    {
        var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(_profile, _global), new ManualTimeProvider());
        Assert.False(catalog.Knows("/late"));

        Write(_profile, "late.md", "Late.");   // a /commands save just now
        Assert.True(catalog.Knows("/late"));
        Assert.False(catalog.Knows("late"));   // a word without its slash is no command
    }

    [Fact]
    public void CopyableFiles_AreTheWalksFiles_LinksApart()
    {
        Write(_profile, "review.md", "x");
        Write(_profile, "sub/one.md", "x");
        Write(_profile, ".git/x.md", "x");
        Write(_profile, "bad name.md", "x");   // a Skipped file travels as it is
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        MakeLink(Path.Combine(_profile, "linked"), elsewhere);

        var (files, links) = CustomCommandCatalog.CopyableFiles(_profile);

        Assert.Equal(["bad name.md", "review.md", "sub/one.md"], files.Select(f => f.Relative).Order(StringComparer.Ordinal));
        Assert.Equal(["linked"], links);
    }

    // A folder link: a junction on Windows (no privilege needed), a symbolic link elsewhere.
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
