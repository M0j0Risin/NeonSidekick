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
        Assert.Contains("--url <url>", SidekickOptions.Usage);
        Assert.Contains("--model <id>", SidekickOptions.Usage);
        Assert.Contains("--cwd <path>   working directory for this launch (outranks the saved setting)", SidekickOptions.Usage);
        Assert.Contains("[--cwd <path>]", SidekickOptions.Usage);
        Assert.Contains("[--profile <name>]", SidekickOptions.Usage);
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
        Assert.Null(SidekickOptions.Parse(["--headless"]).Describe());
        Assert.Equal(@"--url http://h:1/v1 --model m --cwd D:\x --log C:\t.log", SidekickOptions.Parse(["--url", "http://h:1/v1", "--model", "m", "--cwd", @"D:\x", "--log", @"C:\t.log"]).Describe());
    }
}
