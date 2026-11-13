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

    /// <summary>A file's restricting keys warn on its card and over each run (2026-11-02, review P12-6); a file without them doesn't.</summary>
    [Fact]
    public void ACommandsAllowedTools_Warns_OnTheCardAndOverEachRun()
    {
        Write(_profile, "review.md", "---\nallowed-tools: Read, Grep\n---\nReview it.");
        Write(_profile, "plain.md", "Plain.");
        Write(_profile, "args.md", "---\narguments: [file-name, style]\n---\nStyle $style.");

        var scan = Catalog().Scan();
        var review = scan.Find("review")!;
        string warning = CustomCommandText.IgnoredKeysWarning(["allowed-tools"]);
        Assert.Equal(warning, review.Warning);
        Assert.Contains(warning, CustomCommandText.SentNotice(review), StringComparison.Ordinal);
        Assert.Null(scan.Find("plain")!.Warning);
        Assert.DoesNotContain(warning, CustomCommandText.SentNotice(scan.Find("plain")!), StringComparison.Ordinal);
        Assert.Equal(CustomCommandText.ArgumentDroppedWarning, scan.Find("args")!.Warning);
    }

    /// <summary>The scan's reuse window is on the monotonic clock (2026-11-02, review P4-6): the wall clock stepped back keeps no stale scan fresh.</summary>
    [Fact]
    public void Recent_AcrossABackwardClockStep_ScansAgain()
    {
        var time = new ManualTimeProvider();
        var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(_profile, _global), time);
        Assert.Null(catalog.Recent().Find("sum"));

        Write(_profile, "sum.md", "Sum it up.");
        time.UtcNow = time.UtcNow.AddHours(-1);
        time.Advance(CustomCommandCatalog.ReuseWindow);
        Assert.NotNull(catalog.Recent().Find("sum"));
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

    /// <summary>The newest built-in (2026-11-12, the video-files plan): a <c>video.md</c> written before <c>/video</c> existed is shadowed now.</summary>
    [Fact]
    public void ANewBuiltIn_ShadowsAnOlderCustomCommand_OfItsName()
    {
        Write(_profile, "video.md", "Play my video.");

        var scan = Catalog().Scan();

        Assert.Empty(scan.Commands);
        Assert.Equal("/video", Assert.Single(scan.Shadowed).BuiltIn);
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

    /// <summary>
    /// A file name is split by the system's separators alone (2026-10-31, review P1-4): a <c>:</c> in a stem, or a <c>\</c> off Windows, is no
    /// command name, so a name always maps back to its one file.
    /// </summary>
    [Fact]
    public void AColonOrAForeignBackslash_InAStem_IsNoName()
    {
        char[] windows = ['\\', '/'];
        char[] unix = ['/'];
        Assert.Null(CustomCommandName.FromRelativePath("a:b.md", windows));
        Assert.Equal("tools:review", CustomCommandName.FromRelativePath("tools\\review.md", windows));
        Assert.Null(CustomCommandName.FromRelativePath("tools\\review.md", unix));
        Assert.Equal("tools:review", CustomCommandName.FromRelativePath("tools/review.md", unix));
        Assert.Contains(":", CustomCommandText.BadNameProblem("a:b.md"), StringComparison.Ordinal);
    }

    /// <summary>Each bad name says its one reason (2026-10-31, review P7-3, P2-14, P6-3).</summary>
    [Theory]
    [InlineData("con", "device name")]
    [InlineData("a:b:c:d:e:f", "parts")]
    [InlineData("x", null)]
    [InlineData("My Review", "lowercase letters")]
    public void NameProblem_SaysTheOneReason(string name, string? said)
    {
        string? problem = CustomCommandText.NameProblem(name);
        if (said is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(said, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NameProblem_TooLong_PartOrWhole()
    {
        Assert.Contains("a part is over", CustomCommandText.NameProblem(new string('a', 65)), StringComparison.Ordinal);
        Assert.Contains("over 128", CustomCommandText.NameProblem(string.Join(':', Enumerable.Repeat(new string('a', 60), 3))), StringComparison.Ordinal);
        Assert.Contains("device name", CustomCommandText.BadNameError("con", ""), StringComparison.Ordinal);
    }

    /// <summary>A bad folder's name suggests renaming the folder, not the file (2026-10-31, review P6-3).</summary>
    [Fact]
    public void ABadFolder_SuggestsRenamingTheFolder()
    {
        Assert.Contains("My Folder", CustomCommandText.BadNameProblem(Path.Combine("My Folder", "review.md")), StringComparison.Ordinal);
        Assert.Contains("my-folder", CustomCommandText.BadNameProblem(Path.Combine("My Folder", "review.md")), StringComparison.Ordinal);
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
        // The ordinal tie-break (2026-10-31, review P2-12): Review.md sorts first and runs, whatever order the listing gave.
        Assert.Equal("Review.md", Path.GetFileName(Assert.Single(scan.Commands).FilePath));
        Assert.Contains(scan.Problems, p => p.Problem == CustomCommandText.CaseDuplicateProblem("/review") && Path.GetFileName(p.FilePath) == "review.md");
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
        // A note, not a Skipped row (2026-10-31, review P5-6).
        Assert.Empty(scan.Problems);
        Assert.Equal(CustomCommandText.SameFolderNote, scan.Note);
        Assert.True(scan.SameFolder);
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

        Write(_profile, "late.md", "Late.");   // a file dropped in just now
        Assert.False(catalog.Knows("/other"));   // a word not missed yet scans again, and that scan sees late.md
        Assert.True(catalog.Knows("/late"));
        Assert.False(catalog.Knows("late"));   // a word without its slash is no command
    }

    /// <summary>
    /// A repeated miss scans once per published scan (2026-10-31, review P1-6, P3-8: a pasted path scanned on every key), and an edit the
    /// app makes (<see cref="CustomCommandCatalog.Invalidate"/>) is read at once.
    /// </summary>
    [Fact]
    public void ARepeatedMiss_ScansOnce_AndInvalidate_ReadsAfresh()
    {
        var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(_profile, _global), new ManualTimeProvider());
        Assert.False(catalog.Knows("/sum"));
        Assert.False(catalog.Knows("/sum"));   // scanned again: missed

        Write(_profile, "sum.md", "Sum.");
        Assert.False(catalog.Knows("/sum"));   // missed already since this scan: no scan
        Assert.Empty(catalog.Recent().Commands);

        catalog.Invalidate();   // what /commands save does
        Assert.Equal("sum", Assert.Single(catalog.Recent().Commands).Name);
        Assert.True(catalog.Knows("/sum"));
    }

    /// <summary>Another profile's folders inside the reuse window are read, never the last profile's scan (2026-10-31, review P5-3).</summary>
    [Fact]
    public void Recent_ReadsTheNewFolders_AfterAProfileSwitch_InsideTheWindow()
    {
        string other = Path.Combine(_dir, "other-profile", "commands");
        Write(other, "theirs.md", "Theirs.");
        Write(_profile, "mine.md", "Mine.");
        string current = _profile;
        var catalog = new CustomCommandCatalog(() => new CustomCommandRoots(current, _global), new ManualTimeProvider());
        Assert.Equal("mine", Assert.Single(catalog.Recent().Commands).Name);

        current = other;

        Assert.Equal("theirs", Assert.Single(catalog.Recent().Commands).Name);
        Assert.Equal(other, catalog.Recent().Roots.Profile);
    }

    /// <summary>A description, hint or warning with a control character reaches no surface as a code (2026-10-31, review P2-1, P3-4).</summary>
    [Fact]
    public void ADescriptionsControlCharacters_AreCleaned_AtTheSource()
    {
        Write(_profile, "x.md", "---\ndescription: \"red \\e[31m here\"\nargument-hint: \"<a\\tb>\"\n---\nBody.");

        var command = Assert.Single(Catalog(_profile, _global).Scan().Commands);

        Assert.DoesNotContain(command.Description!, char.IsControl);
        Assert.DoesNotContain(command.ArgumentHint!, char.IsControl);
    }

    /// <summary>The template's description or hint left as it was warns too (2026-10-31, review P6-5), not only its body.</summary>
    [Fact]
    public void TheTemplatesDescription_LeftAsItWas_Warns()
    {
        Write(_profile, "x.md", "---\ndescription: " + CustomCommandText.TemplateDescription("old-name") + "\n---\nA real prompt.");
        Write(_profile, "y.md", "---\nargument-hint: " + CustomCommandText.TemplateHint + "\n---\nA real prompt.");

        var commands = Catalog(_profile, _global).Scan().Commands;

        Assert.All(commands, c => Assert.Contains(CustomCommandText.StillTemplateWarning, c.Warning, StringComparison.Ordinal));
        Assert.Equal(2, commands.Count);
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

        var (files, links, _) = CustomCommandCatalog.CopyableFiles(_profile);

        Assert.Equal(["bad name.md", "review.md", "sub/one.md"], files.Select(f => f.Relative).Order(StringComparer.Ordinal));
        Assert.Equal(["linked"], links);
    }

    /// <summary>
    /// A FIFO named *.md (2026-10-31, review P5-2) is a Skipped row, never opened, so the scan never blocks on it. A Mac's walk asks; the
    /// scan runs under a deadline so a hang fails instead of stalling the suite.
    /// </summary>
    [MacFact]
    public async Task AFifo_IsASkippedRow_AndTheScanNeverBlocks()
    {
        Write(_profile, "review.md", "Review.");
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/mkfifo", Path.Combine(_profile, "x.md")) { UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var scan = await Task.Run(() => Catalog().Scan()).WaitAsync(TimeSpan.FromSeconds(20));
        var (files, _, _) = await Task.Run(() => CustomCommandCatalog.CopyableFiles(_profile)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("review", Assert.Single(scan.Commands).Name);
        Assert.Contains(scan.Problems, p => Path.GetFileName(p.FilePath) == "x.md" && p.Problem == CustomCommandText.NotAFileProblem);
        Assert.DoesNotContain(files, f => f.Relative == "x.md");
    }

    /// <summary>A subfolder a copy can't list is named among its unreadable folders (2026-10-31, review P5-4), not "nothing to copy".</summary>
    [UnixFact]
    public void CopyableFiles_NameAnUnreadableFolder()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // [UnixFact] skips it; the guard is for the platform analyzer (File.SetUnixFileMode)
        }

        string locked = Path.Combine(_profile, "locked");
        Write(_profile, "locked/x.md", "x");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            if (Directory.EnumerateFileSystemEntries(locked).Any())
            {
                return;   // run as root: nothing is unreadable
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            var (files, _, unreadable) = CustomCommandCatalog.CopyableFiles(_profile);

            Assert.Empty(files);
            Assert.Equal(locked, Assert.Single(unreadable).FilePath);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
