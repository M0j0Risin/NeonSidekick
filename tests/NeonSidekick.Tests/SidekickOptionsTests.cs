using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class SidekickOptionsTests
{
    [Fact]
    public void Parse_NoArgs_IsNone()
    {
        var o = SidekickOptions.Parse(Array.Empty<string>());
        Assert.Equal(SidekickOptions.None, o);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("--smoke")]
    [InlineData("--SMOKE")]
    [InlineData("  --smoke ")]
    public void Parse_Smoke(string arg)
    {
        Assert.True(SidekickOptions.Parse(new[] { arg }).Smoke);
    }

    [Fact]
    public void Parse_Headless()
    {
        Assert.True(SidekickOptions.Parse(new[] { "--headless" }).Headless);
    }

    /// <summary>
    /// 2026-09-26: --profile over NEONSIDEKICK_PROFILE over, for a headless run, "default"; otherwise null, and the pointer
    /// in settings.json decides (the TUI and the check modes).
    /// </summary>
    [Theory]
    [InlineData("--headless --profile work", "home", "work")]
    [InlineData("--headless", "home", "home")]
    [InlineData("--headless", null, "default")]
    [InlineData("--headless --smoke", null, "default")]
    [InlineData("--profile work", null, "work")]
    [InlineData("", "home", "home")]
    [InlineData("", null, null)]
    [InlineData("--smoke", null, null)]
    public void LaunchProfile_HeadlessDefaultsToDefault_UnlessNamed(string args, string? environment, string? expected)
    {
        var o = SidekickOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expected, o.LaunchProfile(environment));
    }

    [Fact]
    public void Parse_AudioCheck()
    {
        var o = SidekickOptions.Parse(new[] { "--audio-check" });
        Assert.True(o.AudioCheck);
        Assert.False(o.Smoke);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Parse_VoiceCheck()
    {
        Assert.Null(SidekickOptions.Parse(new[] { "--voice-check" }).OracleCheck);
        var oracle = SidekickOptions.Parse(new[] { "--oracle-check", "free" });
        Assert.Equal("free", oracle.OracleCheck);
        Assert.True(oracle.IsCheck);
        Assert.False(SidekickOptions.None.IsCheck);
        Assert.NotNull(SidekickOptions.Parse(new[] { "--oracle-check" }).Error);   // the connection is required
        var mysql = SidekickOptions.Parse(new[] { "--mysql-check", "shop" });
        Assert.Equal("shop", mysql.MySqlCheck);
        Assert.True(mysql.IsCheck);
        Assert.Equal("mysql-check", mysql.Mode);
        Assert.NotNull(SidekickOptions.Parse(new[] { "--mysql-check" }).Error);
        var sql = SidekickOptions.Parse(new[] { "--sql-check", "aw" });   // 2026-10-03: the SQL tools' own, come late
        Assert.Equal("aw", sql.SqlCheck);
        Assert.True(sql.IsCheck);
        Assert.Equal("sql-check", sql.Mode);
        Assert.Equal("aw", SidekickOptions.Parse(new[] { "--sql-check=aw" }).SqlCheck);
        Assert.NotNull(SidekickOptions.Parse(new[] { "--sql-check" }).Error);
        Assert.Contains("--sql-check <connection>", SidekickOptions.Usage);
        var unc = SidekickOptions.Parse(new[] { "--unc-check", "eng" });   // later still on 2026-09-30
        Assert.Equal("eng", unc.UncCheck);
        Assert.True(unc.IsCheck);
        Assert.Equal("unc-check", unc.Mode);
        Assert.NotNull(SidekickOptions.Parse(new[] { "--unc-check" }).Error);
        var o = SidekickOptions.Parse(new[] { "--voice-check" });
        Assert.True(o.VoiceCheck);
        Assert.False(o.AudioCheck);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("-?")]
    [InlineData("/?")]
    public void Parse_Help(string arg)
    {
        Assert.True(SidekickOptions.Parse(new[] { arg }).ShowHelp);
    }

    [Fact]
    public void Parse_Version()
    {
        Assert.True(SidekickOptions.Parse(new[] { "--version" }).ShowVersion);
    }

    [Fact]
    public void Parse_UnknownArgument_SetsErrorNamingIt()
    {
        var o = SidekickOptions.Parse(new[] { "--smoke", "--bogus" });
        Assert.NotNull(o.Error);
        Assert.Contains("--bogus", o.Error);
        Assert.True(o.Smoke, "flags before the bad one are kept so the caller can report accurately");
    }

    [Theory]
    [InlineData("--url", "http://h:1", "--model", "m")]
    [InlineData("--URL=http://h:1", "--Model=m")]
    [InlineData("--headless", "--url=http://h:1", "--model", "m")]
    public void Parse_UrlAndModel_TakeValues(params string[] args)
    {
        var o = SidekickOptions.Parse(args);
        Assert.Null(o.Error);
        Assert.Equal("http://h:1", o.Url);
        Assert.Equal("m", o.Model);
        Assert.Equal(new[] { "--url", "--model" }, o.ActiveFlags());
    }

    [Theory]
    [InlineData("--log", @"C:\tmp\neon.log")]
    [InlineData(@"--LOG=C:\tmp\neon.log")]
    [InlineData("--headless", "--log", @"C:\tmp\neon.log", "--smoke")]
    public void Parse_Log_TakesAPath_AndIsNotAnOverride(params string[] args)
    {
        var o = SidekickOptions.Parse(args);
        Assert.Null(o.Error);
        Assert.Equal(@"C:\tmp\neon.log", o.LogPath);
        Assert.Empty(o.ActiveFlags());   // a log file overrides no setting
    }

    [Theory]
    [InlineData("--url")]
    [InlineData("--url", "--smoke")]
    [InlineData("--url=")]
    [InlineData("--model", "")]
    [InlineData("--log")]
    [InlineData("--log=")]
    public void Parse_ValueFlagWithoutValue_SetsError(params string[] args)
    {
        var o = SidekickOptions.Parse(args);
        Assert.NotNull(o.Error);
        Assert.Contains("needs a value", o.Error);
    }

    [Fact]
    public void ApplyTo_LayersFlagsOverTheEffectiveSnapshot_WithoutMutatingIt()
    {
        var effective = new AppSettingsData { LlmUrl = "http://env:1", LlmModel = "env-model", TtsVoice = "bf_emma" };
        var o = SidekickOptions.Parse(new[] { "--model", "flag-model" });

        var result = o.ApplyTo(effective);

        Assert.Equal("http://env:1", result.LlmUrl);      // no flag: untouched
        Assert.Equal("flag-model", result.LlmModel);
        Assert.Equal("bf_emma", result.TtsVoice);
        Assert.Equal("env-model", effective.LlmModel);
        Assert.Empty(SidekickOptions.None.ActiveFlags());
    }

    [Theory]
    [InlineData("--cwd", "D:\\proj")]
    [InlineData("--CWD=D:\\proj", null)]
    public void Parse_Cwd_TakesAValue_AndIsAnActiveFlag(string first, string? second)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        var o = SidekickOptions.Parse(args);
        Assert.Null(o.Error);
        Assert.Equal("D:\\proj", o.WorkingDirectory);
        Assert.Equal(new[] { SidekickOptions.CwdFlag }, o.ActiveFlags());
    }

    [Theory]
    [InlineData("--cwd")]
    [InlineData("--cwd=")]
    [InlineData("--cwd --smoke")]
    public void Parse_Cwd_WithoutAValue_IsAnError(string line)
    {
        var o = SidekickOptions.Parse(line.Split(' '));
        Assert.Equal("--cwd needs a value", o.Error);
    }

    [Theory]
    [InlineData("--profile", "work")]
    [InlineData("--PROFILE=work", null)]
    public void Parse_Profile_TakesAValue_AndIsAnActiveFlag(string first, string? second)
    {
        var args = second is null ? new[] { "--headless", first } : new[] { "--headless", first, second };
        var o = SidekickOptions.Parse(args);
        Assert.Null(o.Error);
        Assert.True(o.Headless);
        Assert.Equal("work", o.Profile);
        Assert.Equal(new[] { SidekickOptions.ProfileFlag }, o.ActiveFlags());
        Assert.Equal("--profile work", o.Describe());
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--profile=")]
    [InlineData("--profile --headless")]
    public void Parse_Profile_WithoutAValue_IsAnError(string line)
    {
        var o = SidekickOptions.Parse(line.Split(' '));
        Assert.Equal("--profile needs a value", o.Error);
    }

    [Theory]
    [InlineData("--yolo")]
    [InlineData("--YOLO")]
    public void Parse_Yolo_IsASwitch_AndAnActiveFlag(string flag)
    {
        var o = SidekickOptions.Parse(new[] { "--headless", flag });
        Assert.Null(o.Error);
        Assert.True(o.Yolo);
        Assert.Equal(new[] { SidekickOptions.YoloFlag }, o.ActiveFlags());
        Assert.Equal("--yolo", o.Describe());
        Assert.False(SidekickOptions.None.Yolo);
    }

    /// <summary><c>--yolo</c> (2026-09-26) sets the policy over whatever the variable and the file said, and leaves the path police alone.</summary>
    [Fact]
    public void ApplyTo_Yolo_SetsThePolicy_AndLeavesThePolice()
    {
        var effective = new AppSettingsData { ShellCommandPolicy = "off", ShellPoliceOutsidePaths = true };
        var o = SidekickOptions.Parse(new[] { "--yolo" });

        var result = o.ApplyTo(effective);

        Assert.Equal("yolo", result.ShellCommandPolicy);
        Assert.True(result.ShellPoliceOutsidePaths);
        Assert.Equal("off", effective.ShellCommandPolicy);   // input untouched
        Assert.Equal("off", SidekickOptions.None.ApplyTo(effective).ShellCommandPolicy);
    }

    [Theory]
    [InlineData("--no-police")]
    [InlineData("--NO-POLICE")]
    public void Parse_NoPolice_IsASwitch_AndAnActiveFlag(string flag)
    {
        var o = SidekickOptions.Parse(new[] { "--headless", "--yolo", flag });
        Assert.Null(o.Error);
        Assert.True(o.NoPolice);
        Assert.Equal(new[] { SidekickOptions.YoloFlag, SidekickOptions.NoPoliceFlag }, o.ActiveFlags());
        Assert.Equal("--yolo --no-police", o.Describe());
        Assert.False(SidekickOptions.None.NoPolice);
        Assert.False(SidekickOptions.Parse(new[] { "--yolo" }).NoPolice);   // never implied by --yolo
    }

    /// <summary><c>--no-police</c> (2026-09-26) turns the police off over the variable and the file, and leaves the command policy.</summary>
    [Fact]
    public void ApplyTo_NoPolice_TurnsThePoliceOff_AndLeavesThePolicy()
    {
        var effective = new AppSettingsData { ShellCommandPolicy = "ask", ShellPoliceOutsidePaths = true };

        var result = SidekickOptions.Parse(new[] { "--no-police" }).ApplyTo(effective);

        Assert.False(result.ShellPoliceOutsidePaths);
        Assert.Equal("ask", result.ShellCommandPolicy);
        Assert.True(effective.ShellPoliceOutsidePaths);   // input untouched
        Assert.True(SidekickOptions.None.ApplyTo(effective).ShellPoliceOutsidePaths);
    }

    [Fact]
    public void ApplyTo_LeavesTheProfileToAppSettings()
    {
        var o = SidekickOptions.Parse(new[] { "--profile", "work" });
        var saved = new AppSettingsData { LlmUrl = "http://saved:1" };
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(saved, SettingsJsonContext.Default.AppSettingsData),
            System.Text.Json.JsonSerializer.Serialize(o.ApplyTo(saved), SettingsJsonContext.Default.AppSettingsData));
    }

    [Fact]
    public void ApplyTo_MakesTheCwdFull_AgainstTheLaunchDirectory()
    {
        var o = SidekickOptions.Parse(new[] { "--cwd", "sub\\dir" });
        var result = o.ApplyTo(new AppSettingsData { WorkingDirectory = "D:\\saved" });
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "sub", "dir"), result.WorkingDirectory);
        Assert.Equal("D:\\saved", SidekickOptions.None.ApplyTo(new AppSettingsData { WorkingDirectory = "D:\\saved" }).WorkingDirectory);
    }

    [Fact]
    public void Usage_NamesEveryFlagAndTheKeyContract()
    {
        Assert.Contains("--headless", SidekickOptions.Usage);
        Assert.Contains("--smoke", SidekickOptions.Usage);
        Assert.Contains("--audio-check", SidekickOptions.Usage);
        Assert.Contains("--voice-check  record up to 5 s from the microphone, transcribe it, exit 0/1", SidekickOptions.Usage);
        Assert.Contains("--oracle-check <connection>  prove the Oracle tools against that connection of oracle.json (reads only), exit 0/1", SidekickOptions.Usage);
        Assert.Contains("--mysql-check <connection>   prove the MySQL tools against that connection of mysql.json (reads only), exit 0/1", SidekickOptions.Usage);
        Assert.Contains("--unc-check <share>          prove the UNC tools against that share of unc.json (reads only), exit 0/1", SidekickOptions.Usage);
        Assert.Contains("[--unc-check <share>]", SidekickOptions.Usage);
        Assert.Contains("--url <url>", SidekickOptions.Usage);
        Assert.Contains("--model <id>", SidekickOptions.Usage);
        Assert.Contains("--cwd <path>   working directory for this launch (outranks the saved setting)", SidekickOptions.Usage);
        Assert.Contains("[--cwd <path>]", SidekickOptions.Usage);
        Assert.Contains("[--profile <name>]", SidekickOptions.Usage);
        Assert.Contains("[--yolo]", SidekickOptions.Usage);
        Assert.Contains("--yolo         run every shell command without asking, this launch only (outranks NEONSIDEKICK_COMMAND_POLICY; the path police still applies unless --no-police)", SidekickOptions.Usage);
        Assert.Contains("[--no-police]", SidekickOptions.Usage);
        Assert.Contains("--no-police    let shell commands name paths outside the working directory, this launch only (outranks NEONSIDEKICK_SHELL_POLICE)", SidekickOptions.Usage);
        Assert.Contains("Exit codes: 0 done, 2 bad argument or unknown profile, 3 headless run in which a shell command was refused", SidekickOptions.Usage);
        Assert.Contains("--profile <name>  profile for this launch (outranks NEONSIDEKICK_PROFILE and settings.json, which it leaves alone)", SidekickOptions.Usage);
        Assert.Contains("--log <path>   append every diagnostic line (Trace and up) to a file", SidekickOptions.Usage);
        Assert.Contains("--version", SidekickOptions.Usage);
        Assert.Contains("--help", SidekickOptions.Usage);
        Assert.Contains("ESC = cancel/back", SidekickOptions.Usage);
        Assert.DoesNotContain("Ctrl+Q", SidekickOptions.Usage);
        Assert.DoesNotContain("quit", SidekickOptions.Usage.Split("Keys:")[1]);
    }
    [Fact]
    public void Mode_AndDescribe_NameTheLaunch()
    {
        Assert.Equal("interactive", SidekickOptions.Parse([]).Mode);
        Assert.Equal("headless", SidekickOptions.Parse(["--headless"]).Mode);
        Assert.Equal("smoke", SidekickOptions.Parse(["--smoke"]).Mode);
        Assert.Equal("audio-check", SidekickOptions.Parse(["--audio-check"]).Mode);
        Assert.Equal("voice-check", SidekickOptions.Parse(["--voice-check"]).Mode);
        Assert.Equal("oracle-check", SidekickOptions.Parse(["--oracle-check", "free"]).Mode);
        Assert.Null(SidekickOptions.Parse(["--headless"]).Describe());
        Assert.Equal(@"--url http://h:1/v1 --model m --cwd D:\x --log C:\t.log", SidekickOptions.Parse(["--url", "http://h:1/v1", "--model", "m", "--cwd", @"D:\x", "--log", @"C:\t.log"]).Describe());
    }
}
