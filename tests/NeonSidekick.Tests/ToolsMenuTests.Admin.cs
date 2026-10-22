using NeonSidekick.App;
using NeonSidekick.Computers;
using NeonSidekick.Services;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin families' computer wizard on <c>/tools</c> (2026-10-15, the admin-tools plan), walked for services and for tasks: a readwrite
/// computer through every page (an unknown action word refused and typed again), the draft tested unsaved, then saved into a fresh file whose
/// comments stay; a read-only one skips the control, actions and guard pages.
/// </summary>
public partial class ToolsMenuTests
{
    private void OpenMenuRow(string menu, SettingsField field) =>
        Push([.. ToTab(menu), .. Enumerable.Repeat(Keys.Down, TabFields(menu).ToList().IndexOf(field)), Keys.Enter]);

    [WindowsFact]   // a Mac shows no Services or Tasks menu (2026-10-22)
    public async Task OnThePane_TheServicesWizard_WalksAReadWriteComputer_RefusesAnUnknownAction_TestsTheDraft_AndSavesIt()
    {
        string path = ServiceConfigFile.Family.ProfilePath(_settings.ProfileDirectory);
        var tested = new List<(Computer<ServiceComputerConfig> Computer, bool FileThere)>();
        var (menu, _, settings) = PaneMenu();
        settings.TestServiceComputer = (c, _) =>
        {
            tested.Add((c, File.Exists(path)));
            return Task.FromResult((true, ServiceText.TestOk(c.Name, 120, 80)));
        };
        OpenMenuRow(ToolsText.ServicesMenuTitle, SettingsField.ServiceAddComputer);
        Push(Keys.Enter);                         // a new computer
        Push(Keys.Enter);                         // the profile's file
        Type("app01");
        Type("app01.corp.example");
        Push(Keys.Enter);                         // windows: as you
        Push(Keys.Down, Keys.Enter);              // readwrite
        Type("MyApp*, W3SVC");
        Type("MyApp*");
        Push(Keys.Backspace, Keys.Backspace, Keys.Backspace);   // the field opens on "all"
        Type("start, reboot");                    // refused: no such action
        Push([.. Enumerable.Repeat(Keys.Backspace, "start, reboot".Length)]);
        Type("restart, start");
        Type("MSSQL*");
        Push(Keys.Enter);                         // redacted
        Type("app server");
        Push(Keys.Down, Keys.Down, Keys.Enter);   // Test
        Push(Keys.Up, Keys.Up, Keys.Enter);       // Save, and offer it
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var (draft, fileThere) = Assert.Single(tested);
        Assert.False(fileThere);
        Assert.Equal("app01.corp.example", draft.Config.Host);
        var (computers, problems) = ServiceConfigFile.Family.Load(path);
        Assert.Empty(problems);
        var app01 = Assert.Single(computers);
        Assert.Equal(("app01", "readwrite"), (app01.Name, app01.Config.Access));
        Assert.Equal(["MyApp*", "W3SVC"], app01.Config.Services);
        Assert.Equal(["MyApp*"], app01.Config.Control);
        Assert.Equal(["start", "restart"], app01.Config.ControlActions);   // in the family's order
        Assert.Equal(["MSSQL*"], app01.Config.Protected);
        Assert.Equal("app server", app01.Config.Description);
        Assert.StartsWith(ServiceConfigFile.EmptyText[..40], File.ReadAllText(path));   // the commented examples kept
        Assert.Contains(ComputerText.BadAction("reboot", ServiceConfigFile.Info), Output);
        Assert.Contains(ServiceText.TestOk("app01", 120, 80), Output);
        Assert.Equal(["local", "app01"], _settings.Current.ServiceComputersOffered);   // local was ticked by default: kept, the new one added
    }

    /// <summary>
    /// The directory wizard with no <c>ad.json</c> anywhere (2026-10-15, the user's report: the "new or saved?" page measured an empty list and
    /// the app crashed): it goes straight to a new domain, and saving writes the file with the entry, hidden until ticked.
    /// </summary>
    [Fact]
    public async Task OnThePane_TheDirectoryWizard_WithNoDomainSaved_StartsANewOne_AndSavesIt()
    {
        string path = NeonSidekick.ActiveDirectory.DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory);
        Assert.False(File.Exists(path));
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.DirectoryMenuTitle, SettingsField.DirectoryAddDomain);
        Push(Keys.Enter);                         // the profile's file: no "new or saved?" page before it
        Type("lab");
        Type("lab.example");
        Type("localhost");
        Type("");                                 // the domain's own base
        Push(Keys.Enter);                         // 389
        Push(Keys.Enter);                         // as you
        Push(Keys.Enter);                         // redacted
        Type("the lab");
        Push(Keys.Down, Keys.Enter);              // Save, hidden from the model
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var lab = Assert.Single(NeonSidekick.ActiveDirectory.DirectoryConfigFile.Load(path).Domains);
        Assert.Equal(("lab", "lab.example", "localhost", "the lab"), (lab.Name, lab.Config.Domain, lab.Config.Server, lab.Config.Description));
        Assert.Null(_settings.Current.DirectoryDomainsOffered);
    }

    /// <summary>ESC on the first page with nothing saved leaves the wizard (no endless loop back into it) and writes nothing.</summary>
    [Fact]
    public async Task OnThePane_TheDirectoryWizard_WithNoDomainSaved_EscLeavesAndWritesNothing()
    {
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.DirectoryMenuTitle, SettingsField.DirectoryAddDomain);
        Push(Keys.Escape);                        // the file page: back out of the wizard
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.False(File.Exists(NeonSidekick.ActiveDirectory.DirectoryConfigFile.ProfilePath(_settings.ProfileDirectory)));
        Assert.Contains(SettingsMenu.DirectoryWizardCancelledNotice, Output);
    }

    [WindowsFact]   // a Mac shows no Services or Tasks menu (2026-10-22)
    public async Task OnThePane_TheTasksWizard_AReadOnlyComputer_SkipsTheChangePages()
    {
        string path = TaskConfigFile.Family.ProfilePath(_settings.ProfileDirectory);
        var (menu, _, _) = PaneMenu();
        OpenMenuRow(ToolsText.TasksMenuTitle, SettingsField.TaskAddComputer);
        Push(Keys.Enter);                         // a new computer
        Push(Keys.Enter);                         // the profile's file
        Type("app02");
        Type(@"\\app02");                         // the leading backslashes dropped
        Push(Keys.Enter);                         // windows
        Push(Keys.Enter);                         // read: no control, actions or privileged pages
        Type(@"\Ops\*");
        Push(Keys.Down, Keys.Enter);              // full
        Type("");
        Push(Keys.Down, Keys.Enter);              // Save, hidden from the model
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        var app02 = Assert.Single(TaskConfigFile.Family.Load(path).Computers);
        Assert.Equal("app02", app02.Config.Host);
        Assert.False(app02.Config.ReadWrite);
        Assert.Null(app02.Config.Control);
        Assert.Null(app02.Config.Privileged);
        Assert.Equal([@"\Ops\*"], app02.Config.Folders);
        Assert.True(app02.Config.Full);
        Assert.Null(_settings.Current.TaskComputersOffered);   // hidden: the setting untouched
    }
}
