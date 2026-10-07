using System.IO.Compression;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.MySql;
using NeonSidekick.Oracle;
using NeonSidekick.Postgres;
using NeonSidekick.Shell;
using NeonSidekick.Skills;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// The tidy-up before the first Mac release (2026-10-06): what a Mac user and the model read where Windows' text named Windows
/// (the connection files, the password stores, <c>open</c>, the skills folder, the help), and the small differences — zsh's
/// <c>type</c>, a skill resource's backslash, the zip's order. The Mac texts are pinned by <see cref="UnixFactAttribute"/>s that
/// return early off macOS (Linux keeps Windows' text); the rest hold on every system. Windows' own sentences stay pinned where
/// they were.
/// </summary>
public sealed class MacTidyUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public MacTidyUpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ── the connection files' templates ──

    public static TheoryData<string, string> Templates() => new()
    {
        { "sql", SqlConfigFile.EmptyText },
        { "oracle", OracleConfigFile.EmptyText },
        { "mysql", MySqlConfigFile.EmptyText },
        { "postgres", PostgresConfigFile.EmptyText },
        { "unc", UncConfigFile.EmptyText },
    };

    [UnixTheory]
    [MemberData(nameof(Templates))]
    public void OnMacOS_ATemplate_NamesTheKeychain_AndNoWindowsStore(string family, string text)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.DoesNotContain("Credential Manager", text);
        Assert.DoesNotContain("cmdkey", text);
        Assert.DoesNotContain("dpapi:", text);
        Assert.Contains("the macOS Keychain as NeonSidekick/" + family + "/<name>", text);
        Assert.Contains("security add-generic-password -s NeonSidekick/" + family + "/", text);
        Assert.Contains("(\"keychain:…\")", text);
    }

    [Fact]
    public void OffMacOS_TheTemplates_AreWindowsOwn()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(SqlConfigFile.WindowsEmptyText, SqlConfigFile.EmptyText);
        Assert.Equal(OracleConfigFile.WindowsEmptyText, OracleConfigFile.EmptyText);
        Assert.Equal(MySqlConfigFile.WindowsEmptyText, MySqlConfigFile.EmptyText);
        Assert.Equal(PostgresConfigFile.WindowsEmptyText, PostgresConfigFile.EmptyText);
        Assert.Equal(UncConfigFile.WindowsEmptyText, UncConfigFile.EmptyText);
        Assert.Contains("cmdkey /generic:NeonSidekick/postgres/billing /user:billing_ro /pass", PostgresConfigFile.EmptyText);
    }

    /// <summary>The Mac's <c>sql.json</c> keeps Windows sign-in — Kerberos works there (the user's proof) — and leaves runas out; each example usable once uncommented.</summary>
    [UnixFact]
    public void OnMacOS_TheSqlTemplate_KeepsWindowsSignIn_DropsRunAs_AndEachExampleLoads()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(profile);
        var example = SqlConfigFile.EmptyText.Split('\n')
            .Where(l => l.StartsWith("  // ", StringComparison.Ordinal))
            .Select(l => l["  // ".Length..])
            .Where(l => l.EndsWith('{') || l.EndsWith(',') || l.EndsWith('}') || l.EndsWith('"') || l.EndsWith("true", StringComparison.Ordinal))
            .Where(l => l.StartsWith('"') || l.StartsWith("  ", StringComparison.Ordinal) || l.StartsWith('}'));
        string path = SqlConfigFile.ProfilePath(profile);
        File.WriteAllText(path, "{ \"connections\": {\n" + string.Join("\n", example) + "\n} }");

        var loaded = SqlConfigFile.Load(path);

        Assert.Empty(loaded.Problems);
        Assert.Equal(["adventureworks", "reports-me", "reports-reader"], loaded.Connections.Select(c => c.Name));
        Assert.Equal(["sql", "windows", "sql"], loaded.Connections.Select(c => c.Config.Auth));
        Assert.True(loaded.Connections[2].Config.InCredentialManager);
        Assert.Contains("Kerberos", SqlConfigFile.EmptyText);
        Assert.DoesNotContain("\"auth\": \"runas\"", SqlConfigFile.EmptyText);
        Assert.Contains("security add-generic-password -s NeonSidekick/sql/reports-reader -a reader -w", SqlConfigFile.EmptyText);
    }

    /// <summary>Windows sign-in is integrated security on every system: Kerberos carries it on a Mac (2026-10-06), so nothing may gate it to Windows.</summary>
    [Fact]
    public void WindowsSignIn_IsIntegratedSecurity_OnEverySystem()
    {
        var builder = new SqlConnectionConfig { Server = "sqlhost01", Auth = SqlConnectionConfig.WindowsAuth }.Builder();

        Assert.True(builder.IntegratedSecurity);
        Assert.Contains(SqlConnectionConfig.WindowsAuth, ToolsMenuWords());
    }

    private static IReadOnlyList<string> ToolsMenuWords() => SettingsMenu.SqlWizardAuthWords;

    // ── the wizards and the password stores ──

    [UnixFact]
    public void OnMacOS_TheWizards_OfferWindowsSignIn_ButNoRunAs_AndNameTheKeychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal([SqlConnectionConfig.SqlAuth, SqlConnectionConfig.WindowsAuth], SettingsMenu.SqlWizardAuthWords);
        Assert.Equal(["sql      a SQL login (user and password)", "windows  Windows sign-in as you through Kerberos (your ticket, no password)"], SettingsMenu.SqlWizardAuthRows);
        Assert.Equal(["file     encrypted (Keychain key) in sql.json", "credman  the macOS Keychain"], SettingsMenu.SqlWizardStoreRows);
        Assert.Equal(["file     encrypted (Keychain key) in postgres.json", "credman  the macOS Keychain"], SettingsMenu.PostgresWizardStoreRows);
        Assert.Equal("Saved the password of 'pg' to the macOS Keychain as NeonSidekick/postgres/pg.", SqlText.PasswordSavedToCredman("pg", "NeonSidekick/postgres/pg"));
        Assert.Equal("the password cannot be decrypted — it was saved by another user or on another Mac (bad tag); set it again on the SQL tab of /tools", SqlText.CannotDecrypt("bad tag"));
        Assert.Equal(@"no password in the macOS Keychain for NeonSidekick/unc/x; set it on the UNC tab of /tools, or: security add-generic-password -s NeonSidekick/unc/x -a <DOMAIN\name> -w", UncText.NoCredential("NeonSidekick/unc/x"));
        Assert.Equal("The database file: a full path (/Users/you/data/app.db), or one relative to sqlite.json's folder.", SettingsMenu.SqliteWizardPathQuestion);
    }

    [WindowsFact]
    public void OnWindows_TheWizardsAndStores_ReadAsBefore()
    {
        Assert.Equal(["sql      a SQL login (user and password)", "windows  Windows sign-in as you (no password)", "runas    Windows sign-in as another account (runas /netonly)"], SettingsMenu.SqlWizardAuthRows);
        Assert.Equal([SqlConnectionConfig.SqlAuth, SqlConnectionConfig.WindowsAuth, SqlConnectionConfig.RunAsAuth], SettingsMenu.SqlWizardAuthWords);
        Assert.Equal(["file     encrypted (DPAPI) in sql.json", "credman  Windows Credential Manager"], SettingsMenu.SqlWizardStoreRows);
        Assert.Equal(["file     encrypted (DPAPI) in unc.json", "credman  Windows Credential Manager"], SettingsMenu.UncWizardStoreRows);
        Assert.Equal("Saved the password of 'pg' to Windows Credential Manager as NeonSidekick/postgres/pg.", SqlText.PasswordSavedToCredman("pg", "NeonSidekick/postgres/pg"));
        Assert.Equal(@"no password in Windows Credential Manager for NeonSidekick/unc/x; set it on the UNC tab of /tools, or: cmdkey /generic:NeonSidekick/unc/x /user:<DOMAIN\name> /pass", UncText.NoCredential("NeonSidekick/unc/x"));
        Assert.Equal(@"The database file: a full path (D:\data\app.db), or one relative to sqlite.json's folder.", SettingsMenu.SqliteWizardPathQuestion);
        Assert.Equal("(Windows default)", SettingsMenu.WindowsDefaultPrinterLabel);
    }

    // ── what the model and the screen read ──

    [UnixFact]
    public void OnMacOS_TheWording_NamesTheMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var files = new WorkingDirectory(() => _dir, new ManualTimeProvider());
        string description = new OpenTool(files, _ => { }).Description;
        Assert.Contains("or a folder in Finder, on their screen", description);
        Assert.DoesNotContain("Explorer", description);
        Assert.Equal(" in Finder", FileText.FolderBrowser);
        Assert.Equal("Use external skills (.agents/skills)", SettingsMenu.ExternalSkillsName);
        Assert.Equal("~/.agents/skills is read too", SettingsMenu.ToggleDescribe(SettingsField.ExternalSkills, true));
        Assert.Equal(Claude.ClaudeText.MacNotFound, Claude.ClaudeText.NotFound);
        Assert.DoesNotContain("USERPROFILE", Claude.ClaudeText.NotFound);
        Assert.Equal("a\\b is not a path every system's folders can hold safely", SkillInstallText.UnsafePathRefusal("a\\b"));
        Assert.Equal("/terminal needs Windows for now; open Terminal or iTerm2 yourself in the working directory.", ChatScreen.MacTerminalUnavailableError);
        Assert.Equal("(none: printing needs Windows)", SettingsMenu.WindowsDefaultPrinterLabel);
        Assert.Equal("GPU load (needs Windows for now)", Perf.PerfText.GpuNote);
        Assert.DoesNotContain(Camera.CameraCommand.Words, w => w.Note.Contains("Windows lists", StringComparison.Ordinal));
        // MagicScaler works on a Mac since 2026-10-07, over ImageIO: its line names that, not "Windows only".
        Assert.Contains(AboutText.Components, c => c.Name == "PhotoSauce.MagicScaler" && c.Role == "image decode, edit and downscale through Apple's ImageIO codecs");
    }

    [UnixFact]
    public void OnMacOS_ThePdfWording_HasNoPrintToPdf()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(ConvertToPdfTool.MacDescriptionText, ConvertToPdfTool.DescriptionHere);
        Assert.DoesNotContain("Microsoft Print to PDF", ConvertToPdfTool.MacDescriptionText);
        Assert.Equal("Error: no Edge, Chrome, Brave or Chromium was found in /Applications, so nothing can make the PDF; set Web browser path", Pdf.PdfText.NoRoute("Microsoft Print to PDF"));
        Assert.Equal("Error: PDF engine is printer, and Microsoft Print to PDF needs Windows; set PDF engine to auto or browser", Pdf.PdfText.NoPrinter("Microsoft Print to PDF"));
        Assert.Equal("the browser when one is found", Pdf.PdfEngine.Describe(Pdf.PdfEngine.Auto));
        Assert.Equal("Microsoft Print to PDF: needs Windows", Pdf.PdfEngine.Describe(Pdf.PdfEngine.Printer));
    }

    [UnixFact]
    public void OnMacOS_ThePreferNativeRule_DoesNotNameType()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.DoesNotContain("type,", Assistant.ShellNativeRule(files: true, git: false, web: false, sql: false));
    }

    // ── the help ──

    [Fact]
    public void TheMacHelpLayer_NamesOnlySettingsAndFormsTheTablesHold()
    {
        foreach (var (field, text) in HelpMac.Settings)
        {
            Assert.NotEqual("", HelpSettings.DescribeAsWritten(field));
            Assert.NotEqual("", text);
        }

        var syntaxes = HelpCommands.AsWritten.SelectMany(c => c.Forms).Select(f => f.Syntax).ToHashSet(StringComparer.Ordinal);
        foreach (string syntax in HelpMac.Forms.Keys)
        {
            Assert.Contains(syntax, syntaxes);
        }
    }

    [Fact]
    public void OffMacOS_TheHelp_IsAsWritten()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Same(HelpCommands.AsWritten, HelpCommands.Commands);
        foreach (var field in HelpMac.Settings.Keys)
        {
            Assert.Equal(HelpSettings.DescribeAsWritten(field), HelpSettings.Describe(field));
        }
    }

    /// <summary>On a Mac no setting's help names a Windows password store or folder, and the Mac layer's forms are the ones <c>/help</c> shows.</summary>
    [UnixFact]
    public void OnMacOS_TheHelp_NamesNoWindowsStoreOrFolder()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (var field in Enum.GetValues<SettingsField>())
        {
            string text = HelpSettings.Describe(field);
            Assert.DoesNotContain("DPAPI", text);
            Assert.DoesNotContain("Credential Manager", text);
            Assert.DoesNotContain("%USERPROFILE%", text);
            Assert.DoesNotContain("cmdkey", text);
        }

        var forms = HelpCommands.Commands.SelectMany(c => c.Forms).ToDictionary(f => f.Syntax, f => f.Meaning, StringComparer.Ordinal);
        foreach (var (syntax, text) in HelpMac.Forms)
        {
            Assert.Equal(text, forms[syntax]);
        }

        Assert.DoesNotContain("Explorer", forms["/view <image>|<folder>"]);
        Assert.Equal(Printing.PrintText.NeedsWindows, forms["/print printers"]);
        Assert.Equal(ChatScreen.MacTerminalUnavailableError, forms["/terminal [<folder>]"]);
    }

    // ── the small differences ──

    [Fact]
    public void Type_GoesToReadFile_InPowerShellAndCmd_AndInEveryShellOffMacOS()
    {
        var offered = new HashSet<string>([ReadFileTool.ToolName], StringComparer.Ordinal);

        Assert.Equal(("type", ReadFileTool.ToolName), NativeRedirect.For("type notes.txt", offered, ShellKind.PowerShell));
        Assert.Equal(("type", ReadFileTool.ToolName), NativeRedirect.For("type notes.txt", offered, ShellKind.Cmd));
        Assert.Equal(("cat", ReadFileTool.ToolName), NativeRedirect.For("cat notes.txt", offered, ShellKind.Zsh));
        Assert.True(NativeRedirect.DescribesCommand("type", ShellKind.Zsh));
        Assert.True(NativeRedirect.DescribesCommand("type", ShellKind.Bash));
        Assert.False(NativeRedirect.DescribesCommand("type", ShellKind.PowerShell));
        Assert.False(NativeRedirect.DescribesCommand("cat", ShellKind.Zsh));
        var bash = NativeRedirect.For("type python3", offered, ShellKind.Bash);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Null(bash);   // zsh's and bash's type describes a command
            Assert.Null(NativeRedirect.For("type python3", offered, ShellKind.Zsh));
        }
        else
        {
            Assert.Equal(("type", ReadFileTool.ToolName), bash);   // Windows unchanged: Git Bash's type is sent back, as before
        }
    }

    [Fact]
    public void ASkillResource_SpelledWithBackslashes_IsReadThroughItsFolders_AndCannotLeave()
    {
        string skillDir = Path.Combine(_dir, "skill");
        Directory.CreateDirectory(Path.Combine(skillDir, "scripts"));
        File.WriteAllText(Path.Combine(skillDir, "scripts", "run.py"), "print('hi')\n");
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "no");
        var skill = new Skill("demo", "a demo", SkillScope.Profile, skillDir);

        var read = SkillCatalog.ReadResource(skill, @"scripts\run.py");
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, read.Outcome);
        Assert.Equal("print('hi')\n", read.Text);
        Assert.Equal(SkillCatalog.ReadOutcome.Outside, SkillCatalog.ReadResource(skill, @"..\secret.txt").Outcome);
        Assert.Equal(SkillCatalog.ReadOutcome.Ok, SkillCatalog.ReadResource(skill, "scripts/run.py").Outcome);
    }

    /// <summary>A folder's zip lists its entries by name, ordinal, whatever order the file system enumerates them in (2026-10-06: the same on Windows and a Mac; this changed Windows' zips too, on purpose).</summary>
    [Fact]
    public void AFoldersZip_ListsItsEntriesByName_Ordinal()
    {
        string root = Path.Combine(_dir, "root");
        string folder = Path.Combine(root, "pack");
        Directory.CreateDirectory(Path.Combine(folder, "b"));
        Directory.CreateDirectory(Path.Combine(folder, "A"));
        foreach (string name in new[] { "z.txt", "b/2.txt", "a.txt", "A/x.txt", "b/1.txt", "B.txt", "_.txt" })
        {
            File.WriteAllText(Path.Combine(folder, name), name);
        }

        var files = new WorkingDirectory(() => root, new ManualTimeProvider());
        var result = files.Zip("pack", null, overwrite: false);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        using var zip = ZipFile.OpenRead(Path.Combine(root, "pack.zip"));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Equal(["pack/A/x.txt", "pack/B.txt", "pack/_.txt", "pack/a.txt", "pack/b/1.txt", "pack/b/2.txt", "pack/z.txt"], names);
    }
}
