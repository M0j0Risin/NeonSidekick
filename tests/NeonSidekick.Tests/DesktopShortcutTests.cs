using NeonSidekick.App;
using NeonSidekick.Shortcuts;

namespace NeonSidekick.Tests;

/// <summary><c>/shortcut</c>'s pure decisions (2026-10-07): the line read, the name, the command line, the log path, the argument list.</summary>
public class DesktopShortcutTests
{
    [Theory]
    [InlineData("", null, false)]
    [InlineData("--log", null, true)]
    [InlineData("jason", "jason", false)]
    [InlineData("jason --log", "jason", true)]
    [InlineData("--LOG  jason ", "jason", true)]
    public void TryParse_ReadsTheFourForms(string args, string? profile, bool log)
    {
        Assert.True(DesktopShortcut.TryParse(args, out var request));
        Assert.Equal(new ShortcutRequest(profile, log), request);
    }

    [Theory]
    [InlineData("--logs")]
    [InlineData("-l")]
    [InlineData("jason samuel")]
    [InlineData("jason --log extra")]
    public void TryParse_RefusesAnythingElse(string args)
    {
        Assert.False(DesktopShortcut.TryParse(args, out _));
    }

    [Fact]
    public void Arguments_NameTheProfile_AndQuoteTheLog()
    {
        Assert.Equal("--profile samuel", DesktopShortcut.Arguments("samuel", null));
        Assert.Equal(@"--profile samuel --log ""D:\My Apps\logs\neon-{ts}.log""", DesktopShortcut.Arguments("samuel", @"D:\My Apps\logs\neon-{ts}.log"));
    }

    [Theory]
    [InlineData(@"D:\x", @"""D:\x""")]
    [InlineData(@"D:\", @"""D:\\""")]
    [InlineData(@"a b\\", @"""a b\\\\""")]
    [InlineData(@"say ""hi""\", @"""say \""hi\""\\""")]
    public void Quote_FollowsTheArgvRules(string value, string quoted)
    {
        Assert.Equal(quoted, DesktopShortcut.Quote(value));
    }

    [Fact]
    public void For_StartsInTheExesFolder_TheLogBesideIt_TheNameTheSameEitherWay()
    {
        string exeFolder = Path.Combine(Path.GetTempPath(), "neon app");
        string exe = Path.Combine(exeFolder, "NeonSidekick.exe");
        string desktop = Path.Combine(Path.GetTempPath(), "desk");

        var plain = DesktopShortcut.For(exe, desktop, "samuel", log: false);
        var logged = DesktopShortcut.For(exe, desktop, "samuel", log: true);

        Assert.Equal(Path.Combine(desktop, "NeonSidekick (samuel).lnk"), plain.LinkPath);
        Assert.Equal(plain.LinkPath, logged.LinkPath);
        Assert.Equal(exe, plain.Target);
        Assert.Equal(exeFolder, plain.WorkingDirectory);
        Assert.Equal("--profile samuel", plain.Arguments);
        Assert.Equal("--profile samuel --log " + DesktopShortcut.Quote(Path.Combine(exeFolder, "logs", "neon-{ts}.log")), logged.Arguments);
        Assert.Equal("NeonSidekick on profile samuel", plain.Description);
    }

    [Fact]
    public void TheLogsStampIsTheOneTheLaunchReplaces()
    {
        // {ts} stays as typed in the shortcut; --log stamps it at each launch (SidekickOptions.WithLogStamp).
        Assert.Contains(SidekickOptions.LogStampToken, DesktopShortcut.LogFileName, StringComparison.Ordinal);
        Assert.Equal("--log", DesktopShortcut.LogSwitch);
    }

    [Fact]
    public void Made_SaysPutOrReplaced_AndTheCommandLine()
    {
        Assert.Equal("(🔗 put NeonSidekick (jason) on the desktop: --profile jason)", ShortcutText.Made("NeonSidekick (jason).lnk", "--profile jason", replaced: false));
        Assert.Equal("(🔗 replaced NeonSidekick (jason) on the desktop: --profile jason)", ShortcutText.Made("NeonSidekick (jason).lnk", "--profile jason", replaced: true));
    }

    /// <summary>The writer hands over the link's full Windows path (2026-10-07, found in the first Mac run of main's
    /// /shortcut): off Windows Path does not split on <c>\</c>, and /shortcut needs Windows there anyway.</summary>
    [WindowsFact]
    public void Made_NamesTheLink_FromItsFullPath()
    {
        Assert.Equal("(🔗 put NeonSidekick (jason) on the desktop: --profile jason)", ShortcutText.Made(@"C:\Users\me\Desktop\NeonSidekick (jason).lnk", "--profile jason", replaced: false));
    }

    [Fact]
    public void ShortcutItems_ProfilesAndTheLog_ThenWhatIsLeft()
    {
        string[] profiles = ["default", "jason", "samuel"];

        var first = ChatScreen.ShortcutItems("", profiles, "samuel");
        Assert.Equal(["default", "jason", "samuel", "--log"], first.Select(i => i.Text));
        Assert.Equal(ShortcutText.LoadedProfileNote, first[2].Note);
        Assert.Equal(ShortcutText.ProfileNote, first[1].Note);
        Assert.Equal(["jason"], ChatScreen.ShortcutItems("j", profiles, "samuel").Select(i => i.Text));
        Assert.Equal(["jason --log"], ChatScreen.ShortcutItems("jason ", profiles, "samuel").Select(i => i.Text));
        Assert.Equal(["--log default", "--log jason", "--log samuel"], ChatScreen.ShortcutItems("--log ", profiles, "samuel").Select(i => i.Text));
        Assert.Empty(ChatScreen.ShortcutItems("jason --log", profiles, "samuel"));
    }

    [WindowsFact]
    public void TheShellsShellLink_WritesWhatReadsBack()
    {
        // The real writer into a temp folder, never the desktop: what the smoke's shortcut:shelllink proves on the published exe.
        string folder = Path.Combine(Path.GetTempPath(), "neon-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var spec = DesktopShortcut.For(exe, folder, "jason", log: true) with { WorkingDirectory = folder };
            new WindowsShortcutWriter().Write(spec);
            new WindowsShortcutWriter().Write(spec);   // a second write replaces the first

            var (target, arguments, start) = WindowsShortcutWriter.Read(spec.LinkPath);
            Assert.Equal(exe, target, ignoreCase: true);
            Assert.Equal(spec.Arguments, arguments);
            Assert.Equal(folder, start, ignoreCase: true);
            Assert.Single(Directory.GetFiles(folder));
            Assert.True(SmokeChecks.ProbeShortcut().Passed);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
