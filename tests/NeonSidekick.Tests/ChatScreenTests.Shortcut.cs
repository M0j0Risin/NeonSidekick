using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Shortcuts;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>/shortcut</c> on the screen (2026-10-07): the four forms, a replaced one, the refusals, the error off Windows.</summary>
public partial class ChatScreenTests
{
    /// <summary>The writer the chat screen gets; null (the default) is a platform without one.</summary>
    private FakeShortcutWriter? _shortcutWriter;

    private string ShortcutExe => Path.Combine(_dir, "app", "NeonSidekick.exe");

    private FakeShortcutWriter UseShortcutWriter() => _shortcutWriter = new FakeShortcutWriter(Path.Combine(_dir, "Desktop"), ShortcutExe);

    [Fact]
    public async Task Shortcut_TheFourForms_TheLoadedProfileOrTheNamedOne_WithOrWithoutTheLog()
    {
        var writer = UseShortcutWriter();
        Profiles.Create(_dir, "Jason", new AppSettingsData());
        PushLine("/shortcut");
        PushLine("/shortcut --log");
        PushLine("/shortcut jason");
        PushLine("/shortcut jason --log");
        PushLine("/exit");

        string output = await RunAsync();

        string exeFolder = Path.GetDirectoryName(ShortcutExe)!;
        string Log(string profile) => " --log " + DesktopShortcut.Quote(Path.Combine(exeFolder, "logs", "neon-" + profile + "-{ts}.log"));
        string log = Log("default");
        Assert.Equal(
            ["--profile default", "--profile default" + log, "--profile Jason", "--profile Jason" + Log("Jason")],
            writer.Written.Select(s => s.Arguments));
        Assert.All(writer.Written, s => Assert.Equal(ShortcutExe, s.Target));
        Assert.All(writer.Written, s => Assert.Equal(exeFolder, s.WorkingDirectory));
        Assert.Equal(Path.Combine(writer.DesktopFolder, "NeonSidekick (Jason).lnk"), writer.Written[2].LinkPath);   // as the profile list spells it
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[0].LinkPath, "--profile default", replaced: false), output);
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[1].LinkPath, "--profile default" + log, replaced: true), output);   // the same name: replaced
        Assert.Contains("  · " + ShortcutText.Made(writer.Written[2].LinkPath, "--profile Jason", replaced: false), output);
        Assert.Equal(2, Directory.GetFiles(writer.DesktopFolder).Length);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>A Mac's writer (2026-10-08): the same forms as a .command file, its log quoted for zsh.</summary>
    [Fact]
    public async Task Shortcut_AMacsWriter_MakesACommandFile_ItsLogQuotedForZsh()
    {
        var writer = _shortcutWriter = new FakeShortcutWriter(Path.Combine(_dir, "Desktop"), ShortcutExe, ShortcutKind.Command);
        PushLine("/shortcut --log");
        PushLine("/exit");

        string output = await RunAsync();

        string exeFolder = Path.GetDirectoryName(ShortcutExe)!;
        var spec = Assert.Single(writer.Written);
        Assert.Equal(Path.Combine(writer.DesktopFolder, "NeonSidekick (default).command"), spec.LinkPath);
        Assert.Equal("--profile default --log " + DesktopShortcut.ShellQuote(Path.Combine(exeFolder, "logs", "neon-default-{ts}.log")), spec.Arguments);
        Assert.Contains("  · " + ShortcutText.Made(spec.LinkPath, spec.Arguments, replaced: false), output.Replace("\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shortcut_Refusals_AreOneLineEach_AndWriteNothing()
    {
        var writer = UseShortcutWriter();
        PushLine("/shortcut ghost");
        PushLine("/shortcut a b");
        PushLine("/shortcut --verbose");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ChatScreen.ProfileMissingError("ghost"), output);
        Assert.Equal(2, output.Split("  ✗ " + ShortcutText.Usage).Length - 1);
        Assert.Empty(writer.Written);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Shortcut_AWriterThatThrows_OrNoExe_PrintsTheError_AndTheScreenGoesOn()
    {
        var writer = UseShortcutWriter();
        writer.Failure = new IOException("IPersistFile.Save failed (0x80070005)");
        _chat.EnqueueText("Hello.");
        PushLine("/shortcut");
        PushLine("hi");
        PushLine("/exit");

        string output = await RunAsync();

        string link = Path.Combine(writer.DesktopFolder, DesktopShortcut.LinkName(Profiles.DefaultName));
        Assert.Contains(ShortcutText.WriteFailed(link, "IPersistFile.Save failed (0x80070005)"), output.Replace("\n", "", StringComparison.Ordinal));
        Assert.Contains("Hello.", output);
        Assert.Single(_chat.Requests);
    }

    [Fact]
    public async Task Shortcut_NoOwnExecutable_IsAnError()
    {
        var writer = UseShortcutWriter();
        writer.Executable = null;
        PushLine("/shortcut");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ShortcutText.NoOwnExecutable, output);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task Shortcut_WithoutAWriter_IsNotHere()
    {
        PushLine("/shortcut");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + ShortcutText.NotHere, output);
    }
}
