using System.Text.RegularExpressions;
using NeonSidekick.App;
using NeonSidekick.Sql;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// A wizard edit whose one change is a typed password writes only the password (2026-11-26, the user's call when the per-family
/// "set password" rows went): the entry's other bytes, comments inside it included, stay as they are; any other change rewrites the entry
/// as before. And the Directory wizard's edit opens on the page it lacks, as the other eight do.
/// </summary>
public partial class ToolsMenuTests
{
    /// <summary>The summary's rows: Save offered, Save hidden, Test, Cancel, then the wizard's labels from row 4 (File).</summary>
    private const int SummaryFirstLabelRow = 4;

    /// <summary>The file with every <c>"password": "…"</c> value blanked, so two texts compare apart from it.</summary>
    private static string WithoutPasswordValue(string text) => Regex.Replace(text, "\"password\"\\s*:\\s*\"[^\"]*\"", "\"password\": \"\"");

    private const string SqlPasswordEntry = """
        {
          "connections": {
            "aw": { "database": "db", "server": "x", "auth": "sql", "user": "reader", // the user's note inside the entry
                    "passwordStore": "file", "password": "old-secret" }
          }
        }
        """;

    /// <summary>The Password page of an edit opens on the stored password (masked): cleared before a new one is typed.</summary>
    private void ClearStored(string stored) => Push([.. Enumerable.Repeat(Keys.Backspace, stored.Length)]);

    /// <summary>From the summary's Save hidden (row 1: the entry isn't offered) down to <paramref name="label"/>'s row, then Enter.</summary>
    private void SummaryRow(IReadOnlyList<string> labels, string label) =>
        Push([.. Enumerable.Repeat(Keys.Down, SummaryFirstLabelRow + labels.ToList().IndexOf(label) - 1), Keys.Enter]);

    /// <summary>The SQL wizard: Edit › Password › a new one › Save, hidden. Only the password changes; the entry's comment and key order stay.</summary>
    [WindowsFact]
    public async Task OnThePane_TheSqlWizard_APasswordAloneWritesOnlyThePassword()
    {
        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, SqlPasswordEntry);
        string before = File.ReadAllText(path);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);                                  // aw, under + New connection
        SummaryRow(SettingsMenu.SqlWizardLabels, "Password");
        ClearStored("old-secret");
        Type("n3w-secret");
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // Save, hidden
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string after = File.ReadAllText(path);
        Assert.Equal(WithoutPasswordValue(before), WithoutPasswordValue(after));
        Assert.Contains("// the user's note inside the entry", after, StringComparison.Ordinal);
        var loaded = SqlConfigFile.Load(path);
        Assert.Equal("n3w-secret", SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains(SqlText.PasswordSavedToFile("aw", path), Output);
        Assert.DoesNotContain(SqlText.ConnectionChanged("aw", path), Output);
        Assert.Contains("••••••", Output);
        Assert.DoesNotContain("n3w-secret", Output);
    }

    /// <summary>The Unix twin of <see cref="OnThePane_TheSqlWizard_APasswordAloneWritesOnlyThePassword"/>: a <c>keychain:</c> value, where Windows writes <c>dpapi:</c>.</summary>
    [UnixFact]
    public async Task OnThePane_TheSqlWizard_APasswordAloneWritesOnlyThePassword_Unix()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, SqlPasswordEntry);
        string before = File.ReadAllText(path);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);
        SummaryRow(SettingsMenu.SqlWizardLabels, "Password");
        ClearStored("old-secret");
        Type("n3w-secret");
        Push(Keys.Home, Keys.Down, Keys.Enter);
        Push(Keys.Escape, Keys.Escape);   // one more for the group menu

        await menu.ShowAsync(CancellationToken.None);

        string after = File.ReadAllText(path);
        Assert.Equal(WithoutPasswordValue(before), WithoutPasswordValue(after));
        var loaded = SqlConfigFile.Load(path);
        Assert.StartsWith(WindowsCredentials.KeychainPrefix, loaded.Connections[0].Config.Password);
        Assert.Equal("n3w-secret", SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.DoesNotContain(SqlText.ConnectionChanged("aw", path), Output);
        Assert.DoesNotContain("n3w-secret", Output);
    }

    /// <summary>A password and a description: two changes, so the entry is rewritten (the changed line said), as before.</summary>
    [WindowsFact]
    public async Task OnThePane_TheSqlWizard_APasswordAndADescription_RewriteTheEntry()
    {
        string path = SqlConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, SqlPasswordEntry);
        var (menu, _, _) = PaneMenu();
        OpenSqlWizard();
        Push(Keys.Down, Keys.Enter);
        SummaryRow(SettingsMenu.SqlWizardLabels, "Password");
        ClearStored("old-secret");
        Type("n3w-secret");
        int password = SettingsMenu.SqlWizardLabels.ToList().IndexOf("Password"), description = SettingsMenu.SqlWizardLabels.ToList().IndexOf("Description");
        Push([.. Enumerable.Repeat(Keys.Down, description - password), Keys.Enter]);   // the cursor came back on Password
        Type("the sample");
        Push(Keys.Home, Keys.Down, Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var loaded = SqlConfigFile.Load(path);
        Assert.Equal("the sample", loaded.Connections[0].Config.Description);
        Assert.Equal("n3w-secret", SqlSecrets.Resolve(loaded.Connections[0]).Value);
        Assert.Contains(SqlText.ConnectionChanged("aw", path), Output);
    }

    /// <summary>UNC: a password alone writes only it, its <c>"access": "read"</c> (which the full save drops as the default) kept.</summary>
    [WindowsFact]
    public async Task OnThePane_TheUncWizard_APasswordAloneWritesOnlyThePassword()
    {
        string path = NeonSidekick.Unc.UncConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(path, """
            {
              "shares": {
                "finance": { "access": "read", "path": "\\\\fs02\\finance", "auth": "runas", "user": "CORP\\svc_reader", // kept
                             "passwordStore": "file", "password": "old-secret" }
              }
            }
            """);
        string before = File.ReadAllText(path);
        var (menu, _, _) = PaneMenu();
        OpenUncRow(3);
        Push(Keys.Down, Keys.Enter);                                  // finance, under + New share
        SummaryRow(SettingsMenu.UncWizardLabels, "Password");
        ClearStored("old-secret");
        Type("n3w-secret");
        Push(Keys.Home, Keys.Down, Keys.Enter);
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        string after = File.ReadAllText(path);
        Assert.Equal(WithoutPasswordValue(before), WithoutPasswordValue(after));
        var finance = NeonSidekick.Unc.UncConfigFile.Load(path).Shares.Single();
        Assert.Equal("n3w-secret", NeonSidekick.Unc.UncSecrets.Resolve(finance).Value);
        Assert.DoesNotContain(SqlText.ConnectionChanged("finance", path), Output);
        Assert.DoesNotContain("n3w-secret", Output);
    }

    /// <summary>
    /// Directory: a runas domain with no stored password opens its edit on Password (2026-11-26, the user's call: it opened on the summary,
    /// where Save refused); the typed one brings the summary back, and Save writes only the password.
    /// </summary>
    [WindowsFact]
    public async Task OnThePane_TheDirectoryWizard_AnEditMissingItsPassword_OpensOnPassword()
    {
        string path = NeonSidekick.ActiveDirectory.DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {
              "domains": {
                "lab": { "domain": "lab.example", "auth": "runas", "user": "LAB\\reader", "passwordStore": "file" } // kept
              }
            }
            """);
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.DirectoryMenuTitle, SettingsField.DirectoryAddDomain);
        Push(Keys.Down, Keys.Enter);                                  // lab: its first page is Password
        Type("n3w-secret");
        Push(Keys.Enter);                                             // the summary, on Save hidden
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        int question = Output.IndexOf(SettingsMenu.SqlWizardPasswordQuestion, StringComparison.Ordinal);
        int summary = Output.IndexOf(SettingsMenu.DirectoryWizardTestRow, StringComparison.Ordinal);
        Assert.True(question >= 0 && (summary < 0 || question < summary), $"Password page {question}, summary {summary}");
        Assert.Contains("} // kept", File.ReadAllText(path), StringComparison.Ordinal);
        var lab = Assert.Single(NeonSidekick.ActiveDirectory.DirectoryConfigFile.Load(path).Domains);
        Assert.StartsWith(WindowsCredentials.ProtectedPrefix, lab.Config.Password);
        Assert.Contains(SqlText.PasswordSavedToFile("lab", path), Output);
        Assert.DoesNotContain(SqlText.ConnectionChanged("lab", path), Output);
    }

    /// <summary>
    /// Directory: Sign-in switched to runas from the summary goes to User, then straight to Password, the next page missing, then the
    /// summary (2026-11-26: it went back to the summary after User, and Save refused for the password), as the other eight wizards do.
    /// </summary>
    [WindowsFact]
    public async Task OnThePane_TheDirectoryWizard_ARunAsSwitch_AsksUserThenPassword()
    {
        string path = NeonSidekick.ActiveDirectory.DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "domains": { "lab": { "domain": "lab.example" } } }""");
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.DirectoryMenuTitle, SettingsField.DirectoryAddDomain);
        Push(Keys.Down, Keys.Enter);                                  // lab: its summary
        SummaryRow(SettingsMenu.DirectoryWizardLabels, "Sign-in");
        Push(Keys.Down, Keys.Enter);                                  // runas
        Type("LAB\\reader");                                         // User, the first page missing
        Type("n3w-secret");                                           // then Password
        Push(Keys.Home, Keys.Down, Keys.Enter);                       // the summary (the cursor back on Sign-in): Save, hidden
        Push(Keys.Escape, Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        int user = Output.IndexOf(SettingsMenu.ComputerWizardUserQuestion, StringComparison.Ordinal);
        int password = Output.IndexOf(SettingsMenu.SqlWizardPasswordQuestion, StringComparison.Ordinal);
        Assert.True(user >= 0 && password > user, $"User page {user}, Password page {password}");
        var lab = Assert.Single(NeonSidekick.ActiveDirectory.DirectoryConfigFile.Load(path).Domains);
        Assert.Equal("LAB\\reader", lab.Config.User);
        Assert.Contains(SqlText.ConnectionChanged("lab", path), Output);
    }

    /// <summary>The nine rows are gone from the settings (the enum holds none), so the help has no entry for them either.</summary>
    [Fact]
    public void TheSetPasswordRows_AreGone()
    {
        Assert.DoesNotContain(Enum.GetNames<SettingsField>(), name => name.EndsWith("SetPassword", StringComparison.Ordinal));
        foreach (var field in new[] { SettingsField.SqlAddConnection, SettingsField.OracleAddConnection, SettingsField.MySqlAddConnection, SettingsField.PostgresAddConnection, SettingsField.UncAddShare, SettingsField.EventLogAddComputer, SettingsField.ServiceAddComputer, SettingsField.TaskAddComputer, SettingsField.DirectoryAddDomain })
        {
            string help = NeonSidekick.Help.HelpSettings.Describe(field);
            Assert.Contains("Password row saves to the entry's store", help, StringComparison.Ordinal);
        }
    }
}
