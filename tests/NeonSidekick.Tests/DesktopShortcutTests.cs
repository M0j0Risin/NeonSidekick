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
        Assert.Equal("--profile samuel --log " + DesktopShortcut.Quote(Path.Combine(exeFolder, "logs", "neon-samuel-{ts}.log")), logged.Arguments);
        Assert.Equal("NeonSidekick on profile samuel", plain.Description);
    }

    [Fact]
    public void TheLogsStampIsTheOneTheLaunchReplaces()
    {
        // {ts} stays as typed in the shortcut; --log stamps it at each launch (SidekickOptions.WithLogStamp).
        Assert.Equal("neon-samuel-" + SidekickOptions.LogStampToken + ".log", DesktopShortcut.LogFileName("samuel"));
        Assert.Equal("neon-default-{ts}.log", DesktopShortcut.LogFileName("default"));
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

    [Theory]
    [InlineData("/Users/me/x", "'/Users/me/x'")]
    [InlineData("/a b/it's/$HOME/\"q\"/`x`/\\/{ts}!", "'/a b/it'\\''s/$HOME/\"q\"/`x`/\\/{ts}!'")]
    [InlineData("", "''")]
    public void ShellQuote_IsOneZshWord_NothingExpanded(string value, string quoted)
    {
        Assert.Equal(quoted, DesktopShortcut.ShellQuote(value));
    }

    [Fact]
    public void For_ACommandFile_IsNamedDotCommand_ItsLogQuotedForZsh()
    {
        const string exe = "/Users/me/Neon App/NeonSidekick";
        var spec = DesktopShortcut.For(exe, "/Users/me/Desktop", "samuel", log: true, ShortcutKind.Command);

        Assert.Equal("/Users/me/Desktop/NeonSidekick (samuel).command", spec.LinkPath.Replace('\\', '/'));
        Assert.Equal("NeonSidekick (samuel).command", DesktopShortcut.LinkName("samuel", ShortcutKind.Command));
        Assert.Equal("--profile samuel --log " + DesktopShortcut.ShellQuote(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "logs", "neon-samuel-{ts}.log")), spec.Arguments);
        Assert.Equal("--profile samuel", DesktopShortcut.Arguments("samuel", null, ShortcutKind.Command));
    }

    [Fact]
    public void CommandScript_MovesToTheExesFolder_AndExecsIt_OrSaysItHasGone()
    {
        var spec = new ShortcutSpec("/d/NeonSidekick (sam).command", "/opt/it's here/NeonSidekick", "--profile sam --log '/opt/it'\\''s here/logs/neon-{ts}.log'", "/opt/it's here", "NeonSidekick on profile sam");

        Assert.Equal(
            "#!/bin/zsh\n"
            + "# NeonSidekick on profile sam. " + ShortcutText.ScriptNote + "\n"
            + "if [[ ! -x '/opt/it'\\''s here/NeonSidekick' ]]; then\n"
            + "  print -r -- '" + ShortcutText.ScriptGone + "' '/opt/it'\\''s here/NeonSidekick'\n"
            + "  read -k1 '?" + ShortcutText.ScriptPressAKey + "'\n"
            + "  exit 1\n"
            + "fi\n"
            + "cd -- '/opt/it'\\''s here' || exit 1\n"
            + "exec '/opt/it'\\''s here/NeonSidekick' --profile sam --log '/opt/it'\\''s here/logs/neon-{ts}.log'\n",
            DesktopShortcut.CommandScript(spec));
    }

    /// <summary>
    /// The script run by zsh as Finder would run it (2026-10-08): from the home folder, with <c>/bin/echo</c> copied into an odd folder
    /// as the exe, so its output is the command line it got, word by word; and with the exe gone, the line and exit code 1.
    /// </summary>
    [MacFact]
    public void CommandScript_RunByZsh_StartsTheExeInItsFolder_WithItsArguments()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;   // [MacFact] skips it; the guard is for the platform analyzer (File.SetUnixFileMode)
        }

        string folder = Path.Combine(Path.GetTempPath(), "neon-command-" + Guid.NewGuid().ToString("N"), "it's $HOME \"x\"");
        Directory.CreateDirectory(folder);
        try
        {
            string exe = Path.Combine(folder, "fake-neon");
            File.WriteAllText(exe, "#!/bin/zsh\nprint -r -- \"$PWD\"; for a in \"$@\"; do print -r -- \"<$a>\"; done\n");
            File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
            var spec = DesktopShortcut.For(exe, folder, "sam", log: true, ShortcutKind.Command);
            File.WriteAllText(spec.LinkPath, DesktopShortcut.CommandScript(spec));

            var (code, output) = RunZsh(spec.LinkPath);
            Assert.Equal(0, code);
            Assert.Equal(
                [Path.GetFullPath(folder), "<--profile>", "<sam>", "<--log>", "<" + Path.Combine(Path.GetFullPath(folder), "logs", "neon-sam-{ts}.log") + ">"],
                output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Replace("/private/var/", "/var/", StringComparison.Ordinal)).ToArray(),
                StringComparer.Ordinal);

            File.Delete(exe);
            (code, output) = RunZsh(spec.LinkPath);
            Assert.Equal(1, code);
            Assert.StartsWith(ShortcutText.ScriptGone + " " + exe, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    private static (int Code, string Output) RunZsh(string script)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/bin/zsh", [script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        using var zsh = System.Diagnostics.Process.Start(start)!;
        zsh.StandardInput.Close();
        string output = zsh.StandardOutput.ReadToEnd();
        Assert.True(zsh.WaitForExit(10_000));
        return (zsh.ExitCode, output);
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
