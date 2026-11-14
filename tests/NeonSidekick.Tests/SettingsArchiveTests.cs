using System.IO.Compression;
using System.Text.Json.Nodes;
using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/settings export</c> and <c>/settings import</c> (2026-11-13, plan parsed-yawning-papert): the allowlist, the zip, the vetting and the
/// import's never-overwrite rules, on temp homes. Portable: entry names use <c>/</c>, and the import's view of the computer is a fake.
/// </summary>
public sealed class SettingsArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    private string From => Path.Combine(_dir, "from");

    private string To => Path.Combine(_dir, "to");

    private string Downloads => Path.Combine(_dir, "downloads");

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    public SettingsArchiveTests()
    {
        Directory.CreateDirectory(From);
        Directory.CreateDirectory(To);
        Directory.CreateDirectory(Downloads);
    }

    public void Dispose()
    {
        try
        {
            Junction.DeleteTree(_dir);   // a junction a test made is taken away, never what it leads to
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The import's view of a computer: this OS, files and folders as the disk says, and every encrypted secret unreadable.</summary>
    internal static ImportEnvironment Fake(string? os = null) =>
        new(os ?? SettingsArchive.ThisOs, File.Exists, Directory.Exists, _ => null, os == SettingsArchive.MacOs ? "zsh" : Shell.ShellKinds.PlatformDefault, "1.0.0");

    private static void Put(string root, string relative, string text = "x")
    {
        string path = Path.Combine(root, Path.Combine(relative.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, Path.Combine(relative.Split('/'))));

    private static bool Has(string root, string relative) => File.Exists(Path.Combine(root, Path.Combine(relative.Split('/'))));

    private SettingsTransferOutcome Export(string? typed = null) =>
        SettingsArchive.ExportToFile(From, typed, Downloads, "1.0.0", Now, CancellationToken.None);

    private string ExportedZip()
    {
        var outcome = Export();
        Assert.Null(outcome.Error);
        return Directory.EnumerateFiles(Downloads, "*.zip").Single();
    }

    private static List<string> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return [.. archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal)];
    }

    private SettingsTransferOutcome Import(string zip, ImportEnvironment? environment = null) =>
        SettingsArchive.ImportFromFile(To, zip, environment ?? Fake(), CancellationToken.None);

    private static string Text(SettingsTransferOutcome outcome) => string.Join("\n", outcome.Lines.Select(l => l.Text));

    /// <summary>A zip made by hand: each (name, text), with a manifest unless <paramref name="manifest"/> is null.</summary>
    private string Zip(IEnumerable<(string Name, string Text)> entries, string? manifest = """{ "format": 1, "app": "1.0.0", "os": "windows", "created": "2026-11-13T00:00:00Z", "profiles": [] }""", Action<ZipArchive>? more = null)
    {
        string path = Path.Combine(Downloads, "hand-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            if (manifest is not null)
            {
                using var writer = new StreamWriter(archive.CreateEntry(SettingsArchive.ManifestName).Open());
                writer.Write(manifest);
            }

            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }

            more?.Invoke(archive);
        }

        return path;
    }

    // ─── the allowlist ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("configs/sql.json", true)]
    [InlineData("configs/SQL.JSON", true)]
    [InlineData("configs/sub/sql.json", false)]
    [InlineData("configs/notes.txt", false)]
    [InlineData("themes/a.json", true)]
    [InlineData("themes/cat/b.json", true)]
    [InlineData("themes/.hidden/b.json", false)]
    [InlineData("themes/cat/deeper/b.json", false)]
    [InlineData("voices/v.json", true)]
    [InlineData("voices/cat/w.json", true)]
    [InlineData("skills/s/SKILL.md", true)]
    [InlineData("skills/s/scripts/run.sh", true)]
    [InlineData("skills/s/.neon-lock", true)]
    [InlineData("skills/s/.git/HEAD", false)]
    [InlineData("skills/s/node_modules/x.js", false)]
    [InlineData("skills/.import-1/s/SKILL.md", false)]
    [InlineData("commands/review.md", true)]
    [InlineData("commands/Deploy.MD", true)]
    [InlineData("commands/a/b/c/d/x.md", true)]
    [InlineData("commands/a/b/c/d/e/x.md", false)]
    [InlineData("commands/.hidden/x.md", false)]
    [InlineData("commands/x.txt", false)]
    [InlineData("comfy/wf.json", true)]
    [InlineData("comfy/wf.md", true)]
    [InlineData("comfy/sub/wf.json", false)]
    [InlineData("machine-id", false)]
    [InlineData("crash.log", false)]
    [InlineData("models/x.bin", false)]
    [InlineData("db/skills/skills.db", false)]
    [InlineData("persona_x.md", false)]
    [InlineData("profiles/neon/profile.json", true)]
    [InlineData("profiles/neon/persona.md", true)]
    [InlineData("profiles/neon/Operata.md", true)]
    [InlineData("profiles/neon/memory.json", false)]
    [InlineData("profiles/neon/configs/memory.json", true)]
    [InlineData("profiles/neon/configs/browser-history.json", false)]
    [InlineData("profiles/neon/db/sessions/sessions.db", false)]
    [InlineData("profiles/neon/images/x.png", false)]
    [InlineData("profiles/neon/files/a.txt", false)]
    [InlineData("profiles/neon/skills/s/SKILL.md", true)]
    [InlineData("profiles/neon/commands/x.md", true)]
    [InlineData("profiles/neon/comfy/wf.json", true)]
    [InlineData("profiles/_temp/profile.json", false)]
    [InlineData("profiles/bad name/profile.json", false)]
    [InlineData("profiles/neon/configs/Thumbs.db", false)]
    [InlineData("themes/a.tmp", false)]
    [InlineData("themes/.DS_Store", false)]
    public void IsExported_IsTheAllowlist(string relative, bool expected) => Assert.Equal(expected, SettingsArchive.IsExported(relative));

    [Theory]
    [InlineData("skills/s/SKILL.md", true)]
    [InlineData("skills/s/a?.md", false)]
    [InlineData("skills/s/a:b.md", false)]
    [InlineData("skills/s/a|b.md", false)]
    [InlineData("../x", false)]
    [InlineData("/etc/x", false)]
    [InlineData("C:/x", false)]
    [InlineData("skills/CON/x.md", false)]
    [InlineData("skills/s/x.md.", false)]
    public void IsPortablePath_RefusesWhatEitherSystemCannotHold(string relative, bool expected) => Assert.Equal(expected, SettingsArchive.IsPortablePath(relative));

    // ─── export ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_CarriesExactlyTheAllowlist_AndNamesTheLooseFiles()
    {
        foreach (string file in new[]
        {
            "configs/sql.json", "configs/app.db", "themes/a.json", "themes/cat/b.json", "themes/.hidden/c.json", "voices/v.json", "voices/cat/w.json",
            "skills/s1/SKILL.md", "skills/s1/scripts/run.sh", "skills/s1/.git/HEAD", "skills/s1/.neon-lock", "skills/noskill/readme.md",
            "commands/review.md", "commands/Deploy.MD", "commands/a/b/c/d/e/deep.md", "comfy/wf.json", "comfy/wf.md", "comfy/lone.md",
            "machine-id", "crash.log", "models/x.bin", "db/skills/skills.db", "persona_x.md", "claude-cli/state.json",
            "profiles/neon/profile.json", "profiles/neon/persona.md", "profiles/neon/configs/memory.json", "profiles/neon/configs/browser-history.json",
            "profiles/neon/db/sessions/sessions.db", "profiles/neon/images/x.png", "profiles/neon/files/a.txt", "profiles/neon/files - Copy/x.txt",
            "profiles/neon/memory.json", "profiles/_temp/profile.json", "profiles/larry/persona.md",
        })
        {
            Put(From, file);
        }

        var outcome = Export();

        Assert.Null(outcome.Error);
        string zip = Directory.EnumerateFiles(Downloads, "*.zip").Single();
        Assert.Equal(SettingsTransferText.DefaultFileName(Now.DateTime), Path.GetFileName(zip));   // the clock's own wall time (the review's #30)
        Assert.Equal(
            new[]
            {
                "comfy/wf.json", "comfy/wf.md", "commands/Deploy.MD", "commands/review.md", "configs/sql.json", SettingsArchive.ManifestName,
                "profiles/larry/persona.md", "profiles/neon/configs/memory.json", "profiles/neon/persona.md", "profiles/neon/profile.json",
                "skills/s1/.neon-lock", "skills/s1/SKILL.md", "skills/s1/scripts/run.sh", "themes/a.json", "themes/cat/b.json", "voices/cat/w.json", "voices/v.json",
            }.Order(StringComparer.Ordinal),
            Entries(zip));

        string text = Text(outcome);
        Assert.Contains("persona_x.md", text);
        Assert.Contains("configs/app.db", text);
        Assert.Contains("profiles/neon/memory.json", text);
        Assert.Contains("profiles/neon/files - Copy/", text);
        Assert.DoesNotContain("profiles/neon/files/", text);
        Assert.DoesNotContain("profiles/neon/db/", text);
        Assert.DoesNotContain("claude-cli", text);
        Assert.DoesNotContain("machine-id", text);
        Assert.Equal(SettingsTransferText.KeepPrivate, outcome.Lines[^1].Text);

        using var archive = ZipFile.OpenRead(zip);
        var manifest = JsonNode.Parse(new StreamReader(archive.GetEntry(SettingsArchive.ManifestName)!.Open()).ReadToEnd())!;
        Assert.Equal(1, (int)manifest["format"]!);
        Assert.Equal("1.0.0", (string?)manifest["app"]);
        Assert.Equal(SettingsArchive.ThisOs, (string?)manifest["os"]);
        Assert.Equal("2026-11-13T09:30:00Z", (string?)manifest["created"]);
        Assert.Equal(["larry", "neon"], manifest["profiles"]!.AsArray().Select(n => (string)n!).ToArray());
    }

    [Fact]
    public void Export_OfAFreshHome_WritesNothing()
    {
        var outcome = Export();

        Assert.Equal(SettingsTransferText.NothingToExport, outcome.Error);
        Assert.Empty(Directory.EnumerateFiles(Downloads));
    }

    [Fact]
    public void Export_DatesA1970FileAs1980()
    {
        Put(From, "profiles/neon/persona.md");
        File.SetLastWriteTime(Path.Combine(From, "profiles", "neon", "persona.md"), new DateTime(1970, 1, 2, 0, 0, 0, DateTimeKind.Local));

        string zip = ExportedZip();

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(1980, archive.GetEntry("profiles/neon/persona.md")!.LastWriteTime.Year);
    }

    [Fact]
    public void Export_ReadsAFileHeldOpenForWriting()
    {
        Put(From, "profiles/neon/profile.json", "{}");
        using (new FileStream(Path.Combine(From, "profiles", "neon", "profile.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Null(Export().Error);
        }
    }

    /// <summary>A file whose read fails partway (another app's lock on its bytes) is named as unreadable, not a write failure (2026-11-14, the review's #62).</summary>
    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Export_AFileLockedPartway_IsNamedAsUnreadable()
    {
        Put(From, "profiles/neon/profile.json", "{}");
        Put(From, "profiles/neon/persona.md", "be kind");
        using var holder = new FileStream(Path.Combine(From, "profiles", "neon", "persona.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        holder.Lock(0, holder.Length);

        var outcome = Export();

        Assert.NotNull(outcome.Error);
        Assert.StartsWith(SettingsTransferText.Unreadable("profiles/neon/persona.md", "")[..^2], outcome.Error, StringComparison.Ordinal);   // the reason is the OS's
        Assert.Empty(Directory.EnumerateFiles(Downloads));
    }

    [Fact]
    public void Export_PastTheEntryCap_RefusesAndNamesTheBiggest()
    {
        Put(From, "skills/big/SKILL.md");
        for (int i = 0; i < SettingsArchive.MaxEntries; i++)
        {
            Put(From, $"skills/big/f{i}.md");
        }

        var outcome = Export();

        Assert.NotNull(outcome.Error);
        Assert.Contains("skills/big/", outcome.Error);
        Assert.Empty(Directory.EnumerateFiles(Downloads));
    }

    [Fact]
    public void Export_SkipsALinkedSkillFolder_AndNamesIt()
    {
        Put(Path.Combine(_dir, "elsewhere"), "SKILL.md");
        Put(From, "profiles/neon/persona.md");
        Put(From, "skills/real/SKILL.md");
        if (OperatingSystem.IsWindows())
        {
            Junction.Make(Path.Combine(From, "skills", "linked"), Path.Combine(_dir, "elsewhere"));   // the review's #98: no right needed, so it always runs
        }
        else
        {
            Directory.CreateSymbolicLink(Path.Combine(From, "skills", "linked"), Path.Combine(_dir, "elsewhere"));
        }

        var outcome = Export();

        Assert.Contains("skills/linked/ (a link)", Text(outcome));
        Assert.DoesNotContain(Entries(Directory.EnumerateFiles(Downloads, "*.zip").Single()), e => e.Contains("linked", StringComparison.Ordinal));
    }

    // ─── the destination ─────────────────────────────────────────────────────────

    [Fact]
    public void Destination_FollowsTheTypedPath()
    {
        string name = SettingsTransferText.DefaultFileName(Now.LocalDateTime);
        Assert.Equal(Path.Combine(Downloads, name), SettingsArchive.Destination(From, null, Downloads, Now.LocalDateTime, out _));
        Assert.Equal(Path.Combine(Downloads, "mine.zip"), SettingsArchive.Destination(From, "mine", Downloads, Now.LocalDateTime, out _));
        Assert.Equal(Path.Combine(Downloads, "My Stuff", "x.zip"), SettingsArchive.Destination(From, "\"My Stuff" + Path.DirectorySeparatorChar + "x.zip\"", Downloads, Now.LocalDateTime, out _));
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.Equal(Path.Combine(elsewhere, name), SettingsArchive.Destination(From, elsewhere, Downloads, Now.LocalDateTime, out _));
        Assert.Equal(Path.Combine(elsewhere, "a.ZIP"), SettingsArchive.Destination(From, Path.Combine(elsewhere, "a.ZIP"), Downloads, Now.LocalDateTime, out _));
    }

    [Fact]
    public void Destination_RefusesAnExistingFile_AndOneInsideTheHome()
    {
        File.WriteAllText(Path.Combine(Downloads, "taken.zip"), "");
        Assert.Null(SettingsArchive.Destination(From, "taken.zip", Downloads, Now.LocalDateTime, out string? error));
        Assert.Equal(SettingsTransferText.AlreadyThere(Path.Combine(Downloads, "taken.zip")), error);

        Assert.Null(SettingsArchive.Destination(From, Path.Combine(From, "x.zip"), Downloads, Now.LocalDateTime, out error));
        Assert.StartsWith("The zip can't go inside the app's home", error, StringComparison.Ordinal);
        Assert.Equal("", File.ReadAllText(Path.Combine(Downloads, "taken.zip")));
    }

    [Fact]
    public void ListZips_NamesTheNewestFive_OrSaysThereAreNone()
    {
        Assert.Equal([SettingsTransferText.NoZips(Downloads)], SettingsArchive.ListZips(Downloads).Select(l => l.Text));

        for (int i = 0; i < 7; i++)
        {
            string path = Path.Combine(Downloads, $"{SettingsTransferText.FilePrefix}2026111{i}-000000.zip");
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }

        var lines = SettingsArchive.ListZips(Downloads);
        Assert.Equal(6, lines.Count);
        Assert.Equal(SettingsTransferText.ZipLine($"{SettingsTransferText.FilePrefix}20261116-000000.zip"), lines[1].Text);
        Assert.Equal(SettingsTransferText.ZipLine($"{SettingsTransferText.FilePrefix}20261112-000000.zip"), lines[^1].Text);
    }

    // ─── import ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_IntoAnEmptyHome_KeepsNamesAndBytes()
    {
        Put(From, "profiles/neon/profile.json", """{ "TtsSpeed": 1.2 }""");
        Put(From, "profiles/neon/persona.md", "be kind");
        Put(From, "profiles/larry/configs/memory.json", "[]");
        Put(From, "configs/sql.json", "{ \"connections\": [] }");
        Put(From, "skills/s1/SKILL.md", "---\nname: s1\n---");
        Put(From, "skills/s1/scripts/run.sh", "echo hi");

        var outcome = Import(ExportedZip());

        Assert.Null(outcome.Error);
        Assert.Equal("be kind", Read(To, "profiles/neon/persona.md"));
        Assert.Equal("[]", Read(To, "profiles/larry/configs/memory.json"));
        Assert.Equal("{ \"connections\": [] }", Read(To, "configs/sql.json"));
        Assert.Equal("echo hi", Read(To, "skills/s1/scripts/run.sh"));
        Assert.Contains("1.2", Read(To, "profiles/neon/profile.json"));
        Assert.Contains(SettingsTransferText.Added(["larry", "neon"]), Text(outcome));
        Assert.Equal(SettingsTransferText.DeleteZip(Directory.EnumerateFiles(Downloads, "*.zip").Single()), outcome.Lines[^1].Text);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(To, "profiles"), ".import-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(To, "skills"), ".import-*"));
    }

    [Fact]
    public void Import_NeverOverwrites_AProfileTakenGetsASuffix()
    {
        Put(From, "profiles/neon/persona.md", "theirs");
        Put(From, "configs/sql.json", "theirs");
        Put(To, "profiles/NEON/persona.md", "mine");
        Put(To, "configs/sql.json", "mine");

        var outcome = Import(ExportedZip());

        Assert.Equal("mine", Read(To, "profiles/NEON/persona.md"));
        Assert.Equal("theirs", Read(To, "profiles/neon-imported/persona.md"));
        Assert.Equal("mine", Read(To, "configs/sql.json"));
        string text = Text(outcome);
        Assert.Contains(SettingsTransferText.Renamed("neon", "neon-imported"), text);
        Assert.Contains(SettingsTransferText.Skipped(["configs/sql.json"]), text);
    }

    [Fact]
    public void Import_IntoAHomeWithoutANeonFolder_KeepsNeon()
    {
        Put(From, "profiles/neon/persona.md");

        Import(ExportedZip());

        Assert.True(Has(To, "profiles/neon/persona.md"));
    }

    [Fact]
    public void FreeName_ChainsSuffixes_WithinTheNameLimit()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "neon", "neon-imported" };
        Assert.Equal("larry", SettingsArchive.FreeName("larry", taken));
        Assert.Equal("Neon-imported-2", SettingsArchive.FreeName("Neon", taken));   // the zip's spelling kept

        string longName = new('a', Profiles.MaxNameLength);
        taken.Add(longName);
        string free = SettingsArchive.FreeName(longName, taken);
        Assert.Equal(Profiles.MaxNameLength, free.Length);
        Assert.EndsWith("-imported", free, StringComparison.Ordinal);
        Assert.True(Profiles.IsValidName(free));
    }

    [Fact]
    public void Import_SkipsBySkillFolder_ComfyPair_ThemeName_AndVoiceStem()
    {
        Put(From, "skills/s1/SKILL.md", "theirs");
        Put(From, "skills/s1/extra.md", "theirs");
        Put(From, "comfy/wf.json", "theirs");
        Put(From, "comfy/wf.md", "theirs");
        Put(From, "themes/x.json", "{ }");
        Put(From, "themes/y.json", """{ "name": "Mine" }""");
        Put(From, "themes/z.json", "{ }");
        Put(From, "voices/v.json", "{ }");
        Put(From, "profiles/neon/persona.md");
        Put(To, "skills/S1/SKILL.md", "mine");
        Put(To, "comfy/wf.json", "mine");
        Put(To, "themes/cat/x.json", "{ }");
        Put(To, "themes/other.json", """{ "name": "mine" }""");
        Put(To, "voices/cat/v.json", "{ }");

        var outcome = Import(ExportedZip());

        Assert.False(Has(To, "skills/S1/extra.md"));
        Assert.False(Has(To, "comfy/wf.md"));
        Assert.False(Has(To, "themes/x.json"));
        Assert.False(Has(To, "themes/y.json"));
        Assert.True(Has(To, "themes/z.json"));
        Assert.False(Has(To, "voices/v.json"));
        string text = Text(outcome);
        foreach (string skipped in new[] { "skills/s1/", "comfy/wf.json", "comfy/wf.md", "themes/x.json", "themes/y.json", "voices/v.json" })
        {
            Assert.Contains(skipped, text);
        }
    }

    [Fact]
    public void Import_AProfileWithoutProfileJson_LandsAsTheDefaults()
    {
        Put(From, "profiles/larry/persona.md", "arr");

        var outcome = Import(ExportedZip());

        Assert.Equal("arr", Read(To, "profiles/larry/persona.md"));
        Assert.False(Has(To, "profiles/larry/profile.json"));
        Assert.Contains(SettingsTransferText.Added(["larry"]), Text(outcome));
    }

    [Fact]
    public void Import_FixesUpProfileJson_KeepingUnknownKeys()
    {
        Put(From, "profiles/neon/profile.json", """{ "LlmApiKey": "dpapi:AAAA", "WebUiToken": "t", "FutureSetting": 7, "WorkingDirectory": "" }""");

        var outcome = Import(ExportedZip());

        var json = JsonNode.Parse(Read(To, "profiles/neon/profile.json"))!;
        Assert.Equal("empty", (string?)json["LlmApiKey"]);
        Assert.Equal("", (string?)json["WebUiToken"]);
        Assert.Equal(7, (int)json["FutureSetting"]!);
        var reset = outcome.Lines.Single(l => l.Warn);
        Assert.Contains("LLM API key", reset.Text);
        Assert.Contains("Web UI token", reset.Text);
        Assert.DoesNotContain("dpapi", reset.Text);
    }

    [Fact]
    public void Import_AnUnparsableProfileJson_LandsAsItIs_AndSaysSo()
    {
        string zip = Zip([("profiles/neon/profile.json", "{ not json")]);

        var outcome = Import(zip);

        Assert.Equal("{ not json", Read(To, "profiles/neon/profile.json"));
        Assert.Contains(SettingsTransferText.Unparsable(["neon"]), Text(outcome));
    }

    [Fact]
    public void Import_SkipsUnknownEntries_AndLinks()
    {
        string zip = Zip([("profiles/neon/persona.md", "p"), ("notes/readme.txt", "x")], more: archive =>
        {
            var link = archive.CreateEntry("skills/s/SKILL.md");
            link.ExternalAttributes = unchecked((int)(0xA1FF_0000u));
            using var writer = new StreamWriter(link.Open());
            writer.Write("/etc/passwd");
        });

        var outcome = Import(zip);

        string text = Text(outcome);
        Assert.Contains("notes/readme.txt", text);
        Assert.Contains("skills/s/SKILL.md (a link)", text);
        Assert.False(Directory.Exists(Path.Combine(To, "skills", "s")));
        Assert.False(Has(To, "notes/readme.txt"));
    }

    [Fact]
    public void Import_FromANewerApp_SaysSo_AndAnUnreadableVersionDoesNot()
    {
        string newer = Zip([("profiles/neon/persona.md", "p")], """{ "format": 1, "app": "99.0.0", "os": "windows" }""");
        Assert.Contains(SettingsTransferText.Newer("99.0.0"), Text(Import(newer)));

        Directory.Delete(Path.Combine(To, "profiles"), recursive: true);
        string odd = Zip([("profiles/neon/persona.md", "p")], """{ "format": 1, "app": "nightly", "os": "windows", "profiles": ["ghost"] }""");
        var outcome = Import(odd);
        Assert.DoesNotContain("Made by version", Text(outcome));
        Assert.False(Directory.Exists(Path.Combine(To, "profiles", "ghost")));
    }

    [Fact]
    public void Import_SaysTheTrustLine_WhenTheZipHoldsAnMcpFile()
    {
        string zip = Zip([("configs/mcp.json", "{}")]);

        var outcome = Import(zip);

        string text = Text(outcome);
        Assert.Contains(SettingsTransferText.McpLater, text);
        Assert.Contains(SettingsTransferText.Trust, text);
    }

    [Theory]
    [InlineData("../x.md")]
    [InlineData("/abs.md")]
    [InlineData("C:/x.md")]
    [InlineData("skills/s/a?.md")]
    public void Import_RefusesAnUnsafePath_WritingNothing(string name)
    {
        string zip = Zip([("profiles/neon/persona.md", "p"), (name, "x")]);

        var outcome = Import(zip);

        Assert.NotNull(outcome.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(To));
    }

    [Fact]
    public void Import_RefusesWhatIsNotASettingsZip()
    {
        string notZip = Path.Combine(Downloads, "x.zip");
        File.WriteAllText(notZip, "hello");
        Assert.Equal(SettingsTransferText.NotZip, Import(notZip).Error);

        Assert.Equal(SettingsTransferText.NoManifest, Import(Zip([("profiles/neon/persona.md", "p")], manifest: null)).Error);
        Assert.Equal(SettingsTransferText.NewerFormat(2), Import(Zip([("profiles/neon/persona.md", "p")], """{ "format": 2 }""")).Error);
        Assert.Equal(SettingsTransferText.CaseClash("profiles/neon/Persona.md"), Import(Zip([("profiles/neon/persona.md", "p"), ("profiles/neon/Persona.md", "q")])).Error);
        Assert.Equal(SettingsTransferText.Missing(Path.Combine(Downloads, "nope.zip")), Import(Path.Combine(Downloads, "nope.zip")).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(To));
    }

    [Fact]
    public void Import_RefusesATruncatedZip()
    {
        Put(From, "profiles/neon/persona.md", new string('p', 5000));
        string zip = ExportedZip();
        byte[] bytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(zip, bytes[..(bytes.Length - 30)]);

        var outcome = Import(zip);

        Assert.StartsWith("The zip is damaged", outcome.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(To));
    }

    [Fact]
    public void Import_RefusesTooManyEntries()
    {
        string zip = Zip(Enumerable.Range(0, SettingsArchive.MaxEntries + 1).Select(i => ($"commands/c{i}.md", "")));

        Assert.Equal(SettingsTransferText.TooManyEntries(SettingsArchive.MaxEntries + 2), Import(zip).Error);
    }

    [Fact]
    public void Import_Cancelled_WritesNothing_AndALeftoverStageIsClearedNextTime()
    {
        Put(From, "profiles/neon/persona.md");
        string zip = ExportedZip();
        Directory.CreateDirectory(Path.Combine(To, "profiles", ".import-crashed", "neon"));
        Directory.SetLastWriteTimeUtc(Path.Combine(To, "profiles", ".import-crashed"), DateTime.UtcNow.AddHours(-2));   // a crash's, not a running import's
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => SettingsArchive.ImportFromFile(To, zip, Fake(), cancelled.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(To, "profiles")));
        Assert.Null(Import(zip).Error);
        Assert.Equal(["neon"], Directory.EnumerateDirectories(Path.Combine(To, "profiles")).Select(Path.GetFileName));
    }

    [Fact]
    public void RunAtStart_ReportsOrRefuses()
    {
        Put(From, "profiles/neon/persona.md");
        string zip = ExportedZip();

        Assert.Null(SettingsArchive.RunAtStart(To, "\"" + zip + "\"", Fake()).Error);
        Assert.Equal(SettingsTransferText.StartRefused(SettingsTransferText.BadPath("a\0b")), SettingsArchive.RunAtStart(To, "a\0b", Fake()).Error);
        Assert.Equal(SettingsTransferText.StartRefused(SettingsTransferText.Missing(Path.Combine(Downloads, "nope.zip"))),
            SettingsArchive.RunAtStart(To, Path.Combine(Downloads, "nope.zip"), Fake()).Error);
    }

    /// <summary>A Mac export records a script's mode and a Mac import gives it back, so a skill's script stays executable.</summary>
    [UnixFact]
    public void ModeBits_RoundTrip_OnAMac()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // [UnixFact] skips it there; this guard is for the analyzer
        }

        Put(From, "skills/s/SKILL.md");
        Put(From, "skills/s/run.sh", "echo");
        File.SetUnixFileMode(Path.Combine(From, "skills", "s", "run.sh"), (UnixFileMode)0b111_101_101);

        Import(ExportedZip());

        Assert.True(File.GetUnixFileMode(Path.Combine(To, "skills", "s", "run.sh")).HasFlag(UnixFileMode.UserExecute));
    }

    // ─── the review's fixes (2026-11-13) ─────────────────────────────────────────

    [Theory]
    [InlineData(".settings-imported", false)]
    [InlineData("skills/s/.venv/lib.py", false)]
    [InlineData("skills/s/venv/lib.py", false)]
    [InlineData("skills/s/__pycache__/x.pyc", false)]
    [InlineData("commands/__pycache__/x.md", true)]
    [InlineData("themes/._a.json", false)]
    public void IsExported_LeavesOutTheMarker_SkillBuildFolders_AndAppleDouble(string relative, bool expected) =>
        Assert.Equal(expected, SettingsArchive.IsExported(relative));

    /// <summary>A skill's packages are named once each, a skill without SKILL.md is named, the start marker never travels (#45, #89, #3).</summary>
    [Fact]
    public void Export_NamesSkillBuildFolders_AndSkillsWithoutASkillFile_AndNeverTheMarker()
    {
        Put(From, "skills/s1/SKILL.md");
        Put(From, "skills/s1/.venv/lib/a.py");
        Put(From, "skills/s1/.venv/lib/b.py");
        Put(From, "skills/s1/.git/HEAD");
        Put(From, "skills/loose/readme.md");
        Put(From, SettingsArchive.StartMarkerName, "ABC");

        var outcome = Export();

        string text = Text(outcome);
        Assert.Contains("skills/s1/.venv/" + SettingsTransferText.SkillBuildNote, text);
        Assert.Contains("skills/loose/" + SettingsTransferText.NoSkillFileNote, text);
        Assert.DoesNotContain(".git", text);
        Assert.DoesNotContain(SettingsArchive.StartMarkerName, text);
        Assert.Equal(1, text.Split(".venv").Length - 1);
        Assert.Equal([SettingsArchive.ManifestName, "skills/s1/SKILL.md"], Entries(Directory.EnumerateFiles(Downloads, "*.zip").Single()));
    }

    /// <summary>
    /// A home of every folder and file the app itself makes exports with nothing named as left out (the review's #50 and #80: the Mac's
    /// shell records were named in every export). A folder the app starts making goes in this list, and fails it without its /help files row.
    /// </summary>
    [Fact]
    public void Export_OfTheAppsOwnFolders_LeavesNothingOutLoudly()
    {
        Put(From, "profiles/neon/persona.md");
        foreach (string folder in new[]
        {
            "models", "llama", Skills.SkillRecordStore.FolderName, Viewer.VideoPage.FolderName, Viewer.BrowserPage.FolderName, "claude-cli",
            Unc.MacMountRules.FolderName, Shell.ShellRecords.RelativeFolder,
        })
        {
            Put(From, folder + "/x.bin");
        }

        Put(From, Diagnostics.CrashReport.FileName);
        Put(From, Files.OwnerLock.MachineIdFileName);
        foreach (string folder in new[]
        {
            Sessions.SessionStore.FolderName, Files.CheckpointStore.FolderName, Shopping.ShoppingStore.FolderName, Files.WorkingDirectory.DefaultFolderName,
            AppSettings.ImagesFolderName, Viewer.BrowserPage.FolderName,
        })
        {
            Put(From, "profiles/neon/" + folder + "/x.bin");
        }

        var outcome = Export();

        Assert.Null(outcome.Error);
        Assert.DoesNotContain(outcome.Lines, l => l.Text.StartsWith("Left out", StringComparison.Ordinal));
    }

    /// <summary>A linked top folder is left out and named, as a nested link is (the review's #20).</summary>
    [WindowsFact]
    public void Export_SkipsALinkedTopFolder_AndNamesIt()
    {
        Put(Path.Combine(_dir, "elsewhere"), "a.json", "{}");
        Put(From, "profiles/neon/persona.md");
        Junction.Make(Path.Combine(From, "themes"), Path.Combine(_dir, "elsewhere"));

        var outcome = Export();

        Assert.Contains("themes/" + SettingsTransferText.LinkNote, Text(outcome));
        Assert.DoesNotContain(Entries(Directory.EnumerateFiles(Downloads, "*.zip").Single()), e => e.StartsWith("themes/", StringComparison.Ordinal));
    }

    /// <summary>A file gone between the walk and the write is named, not the whole export's failure (the review's #85).</summary>
    [Fact]
    public void Write_AFileGoneSinceTheWalk_IsLeftOut()
    {
        Put(From, "profiles/neon/persona.md");
        Put(From, "profiles/neon/operata.md");
        var c = SettingsArchive.Collect(From);
        File.Delete(Path.Combine(From, "profiles", "neon", "operata.md"));

        using var output = new MemoryStream();
        Assert.Null(SettingsArchive.Write(c, output, "1.0.0", Now, CancellationToken.None));

        Assert.Contains("profiles/neon/operata.md" + SettingsTransferText.GoneNote, c.LeftOut);
    }

    /// <summary>A failed write into the zip reaches the caller (the review's #76: it was reported as a source file that wouldn't read).</summary>
    [Fact]
    public void Write_AZipThatCannotGrow_Throws()
    {
        Put(From, "profiles/neon/persona.md", new string('p', 10_000));
        var c = SettingsArchive.Collect(From);

        using var output = new MemoryStream(new byte[200], writable: true);
        Assert.ThrowsAny<NotSupportedException>(() => SettingsArchive.Write(c, output, "1.0.0", Now, CancellationToken.None));
    }

    /// <summary>The export's byte cap leaves the manifest room (the review's #28): files just under the limit are refused.</summary>
    [Fact]
    public void Export_PastTheByteCap_LessTheManifest_IsRefused()
    {
        Put(From, "profiles/neon/persona.md");
        using (var big = new FileStream(Path.Combine(From, "profiles", "neon", "persona.md"), FileMode.Open))
        {
            big.SetLength(SettingsArchive.MaxBytes - 1000);
        }

        var outcome = Export();

        Assert.StartsWith(SettingsTransferText.TooBig(2, SettingsArchive.MaxBytes - 1000, ["profiles/neon/persona.md"])[..20], outcome.Error, StringComparison.Ordinal);
        Assert.Contains("profiles/neon/persona.md", outcome.Error);
        Assert.Empty(Directory.EnumerateFiles(Downloads));
    }

    /// <summary>A path ending in a separator is a folder (#9), a taken default name tries -2 (#75), <c>~</c> is the user's home (#22).</summary>
    [Fact]
    public void Destination_ReadsFolders_FreesTheDefaultName_AndExpandsTheHome()
    {
        string name = SettingsTransferText.DefaultFileName(Now.LocalDateTime);
        string folder = Path.Combine(_dir, "new-folder");
        Assert.Equal(Path.Combine(folder, name), SettingsArchive.Destination(From, folder + Path.DirectorySeparatorChar, Downloads, Now.LocalDateTime, out _));

        File.WriteAllText(Path.Combine(Downloads, name), "");
        string stem = Path.GetFileNameWithoutExtension(name);
        Assert.Equal(Path.Combine(Downloads, stem + "-2.zip"), SettingsArchive.Destination(From, null, Downloads, Now.LocalDateTime, out _));

        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(user, "x.zip"), SettingsArchive.Destination(From, "~/x.zip", Downloads, Now.LocalDateTime, out _));

        Assert.Null(SettingsArchive.Destination(From, "a\0b", Downloads, Now.LocalDateTime, out string? error));
        Assert.Equal(SettingsTransferText.BadPath("a\0b"), error);
    }

    /// <summary>A name typed without <c>.zip</c> finds the zip (the review's #90); a path no file can have is null.</summary>
    [Fact]
    public void ImportPath_AddsZip_WhenThatFileIsThere()
    {
        File.WriteAllText(Path.Combine(Downloads, "mine.zip"), "");

        Assert.Equal(Path.Combine(Downloads, "mine.zip"), SettingsArchive.ImportPath("mine", Downloads));
        Assert.Equal(Path.Combine(Downloads, "other"), SettingsArchive.ImportPath("\"other\"", Downloads));
        Assert.Null(SettingsArchive.ImportPath("a\0b", Downloads));
    }

    [Fact]
    public void Import_RefusesADuplicateEntry_ApartFromACaseClash()
    {
        string zip = Zip([("profiles/neon/persona.md", "p"), ("profiles/neon/persona.md", "q")]);

        Assert.Equal(SettingsTransferText.DuplicateEntry("profiles/neon/persona.md"), Import(zip).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(To));
    }

    [Fact]
    public void Import_AnOversizedManifest_IsDamaged()
    {
        string manifest = "{ \"format\": 1, \"app\": \"1.0.0\", \"os\": \"windows\", \"pad\": \"" + new string('x', 70_000) + "\" }";

        Assert.Equal(SettingsTransferText.Damaged(SettingsTransferText.ManifestTooBig), Import(Zip([("profiles/neon/persona.md", "p")], manifest)).Error);
    }

    /// <summary>Entries declaring more than the byte cap are refused before anything is written.</summary>
    [Fact]
    public void Import_PastTheDeclaredByteCap_IsTooLarge()
    {
        string zip = Zip([("profiles/neon/persona.md", "p")], more: archive =>
        {
            using var body = archive.CreateEntry("commands/big.md", CompressionLevel.Fastest).Open();
            var zeros = new byte[1 << 20];
            for (long written = 0; written <= SettingsArchive.MaxBytes; written += zeros.Length)
            {
                body.Write(zeros);
            }
        });

        Assert.Equal(SettingsTransferText.TooLarge, Import(zip).Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(To));
    }

    /// <summary>A file longer than any settings zip can be is refused before its directory is parsed (the review's #69).</summary>
    [Fact]
    public void Read_AnOverlongStream_IsRefusedBeforeOpening()
    {
        string path = Path.Combine(Downloads, "long.zip");
        using (var file = new FileStream(path, FileMode.CreateNew))
        {
            file.Write("PK\u0003\u0004"u8);
            file.SetLength(SettingsArchive.MaxBytes + (long)SettingsArchive.MaxEntries * 1024 + 1);
        }

        using var stream = File.OpenRead(path);
        Assert.Null(SettingsArchive.Read(stream, out string? error));
        Assert.Equal(SettingsTransferText.TooLarge, error);
    }

    /// <summary>Names free here are reserved before any suffix is chosen (the review's #73): the zip's own "work-imported" keeps its name.</summary>
    [Fact]
    public void Import_ReservesFreeNames_BeforeSuffixing()
    {
        Put(To, "profiles/work/persona.md", "mine");
        string zip = Zip([("profiles/work/persona.md", "a"), ("profiles/work-imported/persona.md", "b")]);

        var outcome = Import(zip);

        Assert.Equal("b", Read(To, "profiles/work-imported/persona.md"));
        Assert.Equal("a", Read(To, "profiles/work-imported-2/persona.md"));
        Assert.Contains(SettingsTransferText.Renamed("work", "work-imported-2"), Text(outcome));
    }

    /// <summary>An unparsable profile.json is reported under the name it landed as (the review's #7).</summary>
    [Fact]
    public void Import_AnUnparsableProfile_IsNamedAsItLanded()
    {
        Put(To, "profiles/neon/persona.md");
        string zip = Zip([("profiles/neon/profile.json", "{ not json")]);

        Assert.Contains(SettingsTransferText.Unparsable(["neon-imported"]), Text(Import(zip)));
    }

    /// <summary>A duplicate key is the profile's problem, never the zip's (the review's #25): it lands as it came.</summary>
    [Fact]
    public void Import_AProfileWithADuplicateKey_LandsAsItIs()
    {
        string zip = Zip([("profiles/neon/profile.json", """{ "WebUiToken": "a", "WebUiToken": "b" }""")]);

        var outcome = Import(zip);

        Assert.Null(outcome.Error);
        Assert.Equal("""{ "WebUiToken": "a", "WebUiToken": "b" }""", Read(To, "profiles/neon/profile.json"));
        Assert.Contains(SettingsTransferText.Unparsable(["neon"]), Text(outcome));
    }

    /// <summary>A profile.json with nothing to reset is left byte for byte, comments and all (the review's #93).</summary>
    [Fact]
    public void Import_AProfileWithNothingToReset_IsLeftAsItCame()
    {
        const string json = "{\n  // mine\n  \"TtsSpeed\": 1.2,\n}";
        string zip = Zip([("profiles/neon/profile.json", json)]);

        Import(zip);

        Assert.Equal(json, Read(To, "profiles/neon/profile.json"));
    }

    /// <summary>A home file that can't be written is named with its reason, a warning, and not "nothing imported" (the review's #8).</summary>
    [Fact]
    public void Import_AHomeFileThatCannotBeWritten_IsNamedAsFailed()
    {
        Put(To, "commands", "a file where the folder goes");
        string zip = Zip([("commands/x.md", "do it")]);

        var outcome = Import(zip);

        var failed = outcome.Lines.Single(l => l.Text.StartsWith("Couldn't be added: commands/x.md (", StringComparison.Ordinal));
        Assert.True(failed.Warn);
        Assert.DoesNotContain(SettingsTransferText.NothingAdded, Text(outcome));
    }

    /// <summary>A start import where every item failed is not remembered, so the next start tries it again (2026-11-14, the review's #22).</summary>
    [Fact]
    public void RunAtStart_WithNothingLanded_TriesTheZipAgainNextStart()
    {
        Put(To, "commands", "a file where the folder goes");
        string zip = Zip([("commands/x.md", "do it")]);

        var first = SettingsArchive.RunAtStart(To, zip, Fake());
        Assert.Null(first.Error);
        Assert.True(first.NothingLanded);
        Assert.False(File.Exists(Path.Combine(To, SettingsArchive.StartMarkerName)));

        File.Delete(Path.Combine(To, "commands"));
        var second = SettingsArchive.RunAtStart(To, zip, Fake());

        Assert.False(second.NothingLanded);
        Assert.DoesNotContain(SettingsTransferText.AlreadyImported(zip), Text(second));
        Assert.True(Has(To, "commands/x.md"));
        Assert.True(File.Exists(Path.Combine(To, SettingsArchive.StartMarkerName)));
    }

    /// <summary>A theme saved with a byte-order mark is still known by its name (the review's #24).</summary>
    [Fact]
    public void Import_AThemeWithAByteOrderMark_IsMatchedByName()
    {
        Put(To, "themes/other.json", """{ "name": "mine" }""");
        string zip = Zip([("themes/y.json", "﻿{ \"name\": \"Mine\" }")]);

        Assert.Contains(SettingsTransferText.Skipped(["themes/y.json"]), Text(Import(zip)));
        Assert.False(Has(To, "themes/y.json"));
    }

    /// <summary>A zip skill named as a local one by its SKILL.md is skipped, whatever its folder (#87); two of one name, the first wins.</summary>
    [Fact]
    public void Import_ASkillOfALocalSkillsName_IsSkipped()
    {
        Put(To, "skills/local/SKILL.md", "---\nname: helper\ndescription: d\n---\nbody");
        string zip = Zip([
            ("skills/other/SKILL.md", "---\nname: helper\ndescription: d\n---\nbody"),
            ("skills/twin-a/SKILL.md", "---\nname: twin\ndescription: d\n---\nbody"),
            ("skills/twin-b/SKILL.md", "---\nname: twin\ndescription: d\n---\nbody"),
        ]);

        var outcome = Import(zip);

        Assert.False(Directory.Exists(Path.Combine(To, "skills", "other")));
        Assert.True(Has(To, "skills/twin-a/SKILL.md"));
        Assert.False(Directory.Exists(Path.Combine(To, "skills", "twin-b")));
        Assert.Contains(SettingsTransferText.Skipped(["skills/other/", "skills/twin-b/"]), Text(outcome));
    }

    /// <summary>One skill's entries land in one folder, under the first spelling (the review's #67).</summary>
    [Fact]
    public void Import_ASkillSpelledTwoWays_LandsInOneFolder()
    {
        string zip = Zip([("skills/Tool/SKILL.md", "---\nname: tool\ndescription: d\n---\nbody"), ("skills/tool/run.sh", "echo")]);

        Import(zip);

        Assert.Equal(["Tool"], Directory.EnumerateDirectories(Path.Combine(To, "skills")).Select(Path.GetFileName));
        Assert.True(Has(To, "skills/Tool/run.sh"));
    }

    /// <summary>A connection file holding a password this computer's store may not have is named, as a warning (the review's #34).</summary>
    [Fact]
    public void Import_NamesConnectionFilesWithStoredPasswords()
    {
        string zip = Zip([
            ("configs/sql.json", """{ "connections": [ { "name": "a", "password": "dpapi:AAAA" } ] }"""),
            ("configs/mysql.json", """{ "connections": [ { "name": "b", "password": "plain" } ] }"""),
            ("profiles/neon/configs/postgres.json", """{ "connections": [ { "name": "c", "passwordStore": "credman" } ] }"""),
        ]);

        var outcome = Import(zip);

        var line = outcome.Lines.Single(l => l.Text == SettingsTransferText.ConnectionPasswords(["profiles/neon/configs/postgres.json", "configs/sql.json"]));
        Assert.True(line.Warn);
    }

    /// <summary>A connection file with a duplicate key, the home's or a profile's, still lands with no error (2026-11-14, the review's #21).</summary>
    [Fact]
    public void Import_AConnectionFileWithADuplicateKey_StillLands()
    {
        const string home = """{ "connections": [], "connections": [] }""";
        const string mine = """{ "x": 1, "x": 2 }""";
        string zip = Zip([("configs/sql.json", home), ("profiles/neon/configs/x.json", mine)]);

        var outcome = Import(zip);

        Assert.Null(outcome.Error);
        Assert.Equal(home, Read(To, "configs/sql.json"));
        Assert.Equal(mine, Read(To, "profiles/neon/configs/x.json"));
    }

    [Theory]
    [InlineData("""{ "password": "dpapi:AAAA" }""", true)]
    [InlineData("""{ "a": [ { "b": "keychain:AAAA" } ] }""", true)]
    [InlineData("""{ "PasswordStore": "credman" }""", true)]
    [InlineData("""{ "passwordStore": "file", "password": "plain" }""", false)]
    [InlineData("""{ "passwordStore": "" }""", false)]
    [InlineData("""{ not json""", false)]
    [InlineData("""{ "password": "dpapi:A", "password": "x" }""", false)]   // a duplicate key throws on the walk, never out (the review's #21)
    public void HoldsStoredPassword_FindsAnEncryptedValueOrAStore(string json, bool expected) =>
        Assert.Equal(expected, SettingsArchive.HoldsStoredPassword(System.Text.Encoding.UTF8.GetBytes(json)));

    /// <summary>The trust line follows any addition, not only an MCP file (the review's #35).</summary>
    [Fact]
    public void Import_SaysTheTrustLine_ForAnyAddition()
    {
        var outcome = Import(Zip([("commands/x.md", "do it")]));

        Assert.Contains(SettingsTransferText.Trust, Text(outcome));
        Assert.DoesNotContain(SettingsTransferText.McpLater, Text(outcome));
    }

    /// <summary>The retype advice comes only with a key a person types (the review's #38): the Web UI token alone doesn't bring it.</summary>
    [Fact]
    public void Import_TheResetLine_AdvisesRetypingOnlyForKeys()
    {
        string token = Text(Import(Zip([("profiles/neon/profile.json", """{ "WebUiToken": "t" }""")])));
        Assert.Contains(SettingsTransferText.Reset([SettingsTransferText.ResetOne("neon", [SettingsTransferText.SettingLabel("WebUiToken")])], keys: false), token);

        string key = Text(Import(Zip([("profiles/larry/profile.json", """{ "AnthropicApiKey": "keychain:AAAA" }""")])));
        Assert.Contains("Type the keys again", key);
    }

    [Fact]
    public void SettingLabel_IsTheRowsName()
    {
        Assert.Equal(SettingsMenu.FieldName(SettingsField.LlmApiKey), SettingsTransferText.SettingLabel(nameof(AppSettingsData.LlmApiKey)));
        Assert.Equal("Web UI token", SettingsTransferText.SettingLabel(nameof(AppSettingsData.WebUiToken)));
        Assert.Equal("NoSuchKey", SettingsTransferText.SettingLabel("NoSuchKey"));
        Assert.Equal("3", SettingsTransferText.SettingLabel("3"));
    }

    /// <summary>A stage an hour old is a crash's and goes; a younger one may be another copy's import and stays (the review's #5).</summary>
    [Fact]
    public void Import_ClearsOnlyOldStages()
    {
        string old = Path.Combine(To, "profiles", SettingsArchive.StagePrefix + "old");
        string fresh = Path.Combine(To, "profiles", SettingsArchive.StagePrefix + "fresh");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        Import(Zip([("profiles/neon/persona.md", "p")]));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    /// <summary>A start import remembers the zip and skips it next time (#3, the user's pick); <c>""</c> is the missing value (#27); MCP's later line is left out (#107).</summary>
    [Fact]
    public void RunAtStart_RemembersTheZip_AndSkipsARepeat()
    {
        Put(From, "profiles/neon/persona.md");
        Put(From, "configs/mcp.json", "{}");
        string zip = ExportedZip();

        var first = SettingsArchive.RunAtStart(To, zip, Fake());
        Assert.Null(first.Error);
        Assert.DoesNotContain(SettingsTransferText.McpLater, Text(first));
        Assert.True(File.Exists(Path.Combine(To, SettingsArchive.StartMarkerName)));

        var second = SettingsArchive.RunAtStart(To, zip, Fake());
        Assert.Equal([SettingsTransferText.AlreadyImported(zip)], second.Lines.Select(l => l.Text));
        Assert.Equal(["neon"], Directory.EnumerateDirectories(Path.Combine(To, "profiles")).Select(Path.GetFileName));

        Assert.Contains(SettingsTransferText.Renamed("neon", "neon-imported"), Text(Import(zip)));   // /settings import takes it again
        Assert.Equal(SettingsTransferText.MissingFlagValue, SettingsArchive.RunAtStart(To, "\"\"", Fake()).Error);
    }

    /// <summary>An imported file always has its owner's read and write (the review's #88), and a fixed-up profile.json keeps its mode (#97).</summary>
    [UnixFact]
    public void ModeBits_OwnerReadWrite_AndAFixedFileKeepsItsMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // [UnixFact] skips it there; this guard is for the analyzer
        }

        Put(From, "profiles/neon/profile.json", """{ "WebUiToken": "t" }""");
        Put(From, "profiles/neon/persona.md");
        File.SetUnixFileMode(Path.Combine(From, "profiles", "neon", "profile.json"), UnixFileMode.UserRead | UnixFileMode.GroupRead);
        File.SetUnixFileMode(Path.Combine(From, "profiles", "neon", "persona.md"), UnixFileMode.UserRead);

        Import(ExportedZip());

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(Path.Combine(To, "profiles", "neon", "profile.json")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(To, "profiles", "neon", "persona.md")));
        Assert.Equal("", (string?)JsonNode.Parse(Read(To, "profiles/neon/profile.json"))!["WebUiToken"]);
    }

    [Fact]
    public void Lines_ListTenThenMore()
    {
        var names = Enumerable.Range(1, 13).Select(i => "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.EndsWith("f10 +3 more", SettingsTransferText.Listed(names), StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_ReadsAsTheSourceGeneratorWrites()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new SettingsArchiveManifest { Format = 1, App = "1.2.3", Os = "macos", Profiles = ["a"] }, SettingsJsonContext.Default.SettingsArchiveManifest);
        Assert.Contains("\"format\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"os\": \"macos\"", json, StringComparison.Ordinal);
        var back = System.Text.Json.JsonSerializer.Deserialize("""{ "format": 1, "app": "x", "unknown": true }""", SettingsJsonContext.Default.SettingsArchiveManifest)!;
        Assert.Equal("x", back.App);
    }
}
