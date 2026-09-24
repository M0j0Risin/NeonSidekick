using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

public class EnvironmentOverridesTests
{
    private static EnvironmentOverrides With(params (string Name, string? Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
        return new EnvironmentOverrides(name => map.TryGetValue(name, out var v) ? v : null);
    }

    [Fact]
    public void Empty_SeesNothing()
    {
        Assert.Null(EnvironmentOverrides.Empty.LlmUrl);
        Assert.Empty(EnvironmentOverrides.Empty.ActiveVariables());
    }

    [Fact]
    public void ApplyTo_VariableOutranksSavedSetting()
    {
        var saved = new AppSettingsData { LlmUrl = "http://saved:1234/v1", LlmModel = "saved-model" };
        var env = With((EnvironmentOverrides.LlmUrlVariable, "http://env:8000/v1"));

        var effective = env.ApplyTo(saved);

        Assert.Equal("http://env:8000/v1", effective.LlmUrl);
        Assert.Equal("saved-model", effective.LlmModel);
        Assert.Equal("http://saved:1234/v1", saved.LlmUrl); // input untouched
    }

    [Fact]
    public void ApplyTo_CoversEveryOverridableField()
    {
        var env = With(
            (EnvironmentOverrides.LlmUrlVariable, "u"),
            (EnvironmentOverrides.LlmModelVariable, "m"),
            (EnvironmentOverrides.LlmApiKeyVariable, "k"),
            (EnvironmentOverrides.RequestTimeoutVariable, "10"),
            (EnvironmentOverrides.TurnTimeoutVariable, "20.5"),
            (EnvironmentOverrides.TtsUrlVariable, "t"),
            (EnvironmentOverrides.TtsVoiceVariable, "v"),
            (EnvironmentOverrides.TtsSpeedVariable, "1.5"),
            (EnvironmentOverrides.WhisperModelVariable, "ggml-tiny.en.bin"),
            (EnvironmentOverrides.LlmReasoningVariable, " High "),
            (EnvironmentOverrides.TtsVoice2Variable, " af_sky "),
            (EnvironmentOverrides.TtsMixVariable, "70"),
            (EnvironmentOverrides.InterruptEchoVariable, "80"),
            (EnvironmentOverrides.InterruptConfirmVariable, "600"),
            (EnvironmentOverrides.LlmContextVariable, "32768"),
            (EnvironmentOverrides.SearxngUrlVariable, " http://box:8080 "),
            (EnvironmentOverrides.CommandPolicyVariable, " YOLO "),
            (EnvironmentOverrides.ObsidianVaultVariable, @" D:\Notes "),
            (EnvironmentOverrides.ComfyUrlVariable, " http://gpu-box:8188 "));

        var e = env.ApplyTo(new AppSettingsData());

        Assert.Equal("u", e.LlmUrl);
        Assert.Equal("m", e.LlmModel);
        Assert.Equal("k", e.LlmApiKey);
        Assert.Equal(10, e.LlmRequestTimeoutSeconds);
        Assert.Equal(20.5, e.LlmTurnTimeoutSeconds);
        Assert.Equal("t", e.TtsHttpUrl);
        Assert.Equal("v", e.TtsVoice);
        Assert.Equal(1.5, e.TtsSpeed);
        Assert.Equal("ggml-tiny.en.bin", e.SttWhisperModel);
        Assert.Equal("high", e.LlmReasoning);   // normalised to the wire word
        Assert.Equal("af_sky", e.TtsVoice2);   // trimmed
        Assert.Equal(70, e.TtsVoiceMix);
        Assert.Equal(80, e.SttInterruptEchoGuard);
        Assert.Equal(600, e.SttInterruptConfirmMs);
        Assert.Equal(32_768, e.LlmContextLength);
        Assert.Equal("http://box:8080", e.WebSearxngUrl);   // trimmed
        Assert.Equal("yolo", e.ShellCommandPolicy);   // normalised to the saved word (2026-09-21)
        Assert.Equal(@"D:\Notes", e.ObsidianVault);   // trimmed (2026-09-22)
        Assert.Equal("http://gpu-box:8188", e.ComfyUrl);   // trimmed (2026-09-24)
        Assert.Equal(EnvironmentOverrides.AllVariables.Length - 1, env.ActiveVariables().Count);   // everything but HOME
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("1")]
    [InlineData("")]
    public void CommandPolicy_IgnoresAnythingButTheThreeWords(string raw)
    {
        var env = With((EnvironmentOverrides.CommandPolicyVariable, raw));
        Assert.Null(env.ShellCommandPolicy);
        Assert.Equal("ask", env.ApplyTo(new AppSettingsData()).ShellCommandPolicy);
        Assert.DoesNotContain(EnvironmentOverrides.CommandPolicyVariable, env.ActiveVariables());
    }

    [Fact]
    public void System_ReadsAnyVariableThroughTheSameDoor()
    {
        var env = With(("PATH", @" C:\Tools;D:\Bin "), ("PATHEXT", ""));
        Assert.Equal(@"C:\Tools;D:\Bin", env.System("PATH"));   // trimmed
        Assert.Null(env.System("PATHEXT"));   // blank is unset
        Assert.Null(env.System("NOPE"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-4096")]
    [InlineData("32k")]
    [InlineData("4096.5")]
    public void BadContextLength_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.LlmContextVariable, value));
        Assert.Null(env.LlmContextLength);
        Assert.Equal(8_192, env.ApplyTo(new AppSettingsData { LlmContextLength = 8_192 }).LlmContextLength);   // the saved value stands
        Assert.DoesNotContain(EnvironmentOverrides.LlmContextVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 151427 ", 151_427)]
    [InlineData("+4096", 4_096)]
    public void ContextLength_AcceptsAnyPositiveWholeNumber(string value, int expected)
    {
        var env = With((EnvironmentOverrides.LlmContextVariable, value));
        Assert.Equal(expected, env.LlmContextLength);
        Assert.Equal(expected, env.ApplyTo(new AppSettingsData { LlmContextLength = 8_192 }).LlmContextLength);
        Assert.Contains(EnvironmentOverrides.LlmContextVariable, env.ActiveVariables());
        Assert.Equal("NEONSIDEKICK_LLM_CONTEXT", EnvironmentOverrides.LlmContextVariable);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("1.5")]
    [InlineData("70 %")]
    public void BadMix_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.TtsMixVariable, value));
        Assert.Null(env.TtsVoiceMix);
        Assert.Equal(60, env.ApplyTo(new AppSettingsData { TtsVoiceMix = 60 }).TtsVoiceMix);   // the saved value stands
        Assert.DoesNotContain(EnvironmentOverrides.TtsMixVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 100 ", 100)]
    [InlineData("+35", 35)]
    public void Mix_AcceptsTheWholeRange(string value, int expected)
    {
        var env = With((EnvironmentOverrides.TtsMixVariable, value));
        Assert.Equal(expected, env.TtsVoiceMix);
        Assert.Equal(expected, env.ApplyTo(new AppSettingsData { TtsVoiceMix = 60 }).TtsVoiceMix);
        Assert.Contains(EnvironmentOverrides.TtsMixVariable, env.ActiveVariables());
    }

    [Fact]
    public void BlankVoice2_IsUnset_SoASavedSecondaryVoiceStands()
    {
        var env = With((EnvironmentOverrides.TtsVoice2Variable, "  "));
        Assert.Null(env.TtsVoice2);
        Assert.Equal("af_sky", env.ApplyTo(new AppSettingsData { TtsVoice2 = "af_sky" }).TtsVoice2);
        Assert.DoesNotContain(EnvironmentOverrides.TtsVoice2Variable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("49")]
    [InlineData("101")]
    [InlineData("65.5")]
    [InlineData("65 %")]
    public void BadInterruptEcho_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.InterruptEchoVariable, value));
        Assert.Null(env.SttInterruptEchoGuard);
        Assert.Equal(90, env.ApplyTo(new AppSettingsData { SttInterruptEchoGuard = 90 }).SttInterruptEchoGuard);   // the saved value stands
        Assert.DoesNotContain(EnvironmentOverrides.InterruptEchoVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("50", 50)]
    [InlineData(" 100 ", 100)]
    [InlineData("+65", 65)]
    public void InterruptEcho_AcceptsTheWholeRange(string value, int expected)
    {
        var env = With((EnvironmentOverrides.InterruptEchoVariable, value));
        Assert.Equal(expected, env.SttInterruptEchoGuard);
        Assert.Equal(expected, env.ApplyTo(new AppSettingsData { SttInterruptEchoGuard = 90 }).SttInterruptEchoGuard);
        Assert.Contains(EnvironmentOverrides.InterruptEchoVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("2001")]
    [InlineData("0.5")]
    public void BadInterruptConfirm_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.InterruptConfirmVariable, value));
        Assert.Null(env.SttInterruptConfirmMs);
        Assert.Equal(400, env.ApplyTo(new AppSettingsData { SttInterruptConfirmMs = 400 }).SttInterruptConfirmMs);
        Assert.DoesNotContain(EnvironmentOverrides.InterruptConfirmVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 2000 ", 2000)]
    [InlineData("+150", 150)]
    public void InterruptConfirm_AcceptsTheWholeRange(string value, int expected)
    {
        var env = With((EnvironmentOverrides.InterruptConfirmVariable, value));
        Assert.Equal(expected, env.SttInterruptConfirmMs);
        Assert.Equal(expected, env.ApplyTo(new AppSettingsData { SttInterruptConfirmMs = 400 }).SttInterruptConfirmMs);
        Assert.Contains(EnvironmentOverrides.InterruptConfirmVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("very high")]
    [InlineData("1")]
    public void BadReasoning_IsIgnored(string value)
    {
        var env = With((EnvironmentOverrides.LlmReasoningVariable, value));
        Assert.Null(env.LlmReasoning);
        Assert.Equal("none", env.ApplyTo(new AppSettingsData { LlmReasoning = "none" }).LlmReasoning);
        Assert.DoesNotContain(EnvironmentOverrides.LlmReasoningVariable, env.ActiveVariables());
    }

    [Fact]
    public void Reasoning_KeepsTheSavedLevel_WhenTheVariableIsBad()
    {
        var env = With((EnvironmentOverrides.LlmReasoningVariable, "turbo"));
        Assert.Equal("medium", env.ApplyTo(new AppSettingsData { LlmReasoning = "medium" }).LlmReasoning);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0.4")]
    [InlineData("2.1")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    public void BadSpeed_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.TtsSpeedVariable, value));
        Assert.Null(env.TtsSpeed);
        Assert.Equal(1.2, env.ApplyTo(new AppSettingsData()).TtsSpeed);   // the compiled default (1.0 until 2026-09-18)
        Assert.DoesNotContain(EnvironmentOverrides.TtsSpeedVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankVariable_IsUnset(string value)
    {
        var env = With((EnvironmentOverrides.LlmModelVariable, value));
        Assert.Null(env.LlmModel);
        Assert.Empty(env.ActiveVariables());
    }

    [Fact]
    public void Values_AreTrimmed()
    {
        var env = With((EnvironmentOverrides.TtsVoiceVariable, "  af_sky  "));
        Assert.Equal("af_sky", env.TtsVoice);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("3601")]
    [InlineData("NaN")]
    [InlineData("1,5")]
    public void BadTimeout_IsIgnoredNotClamped(string value)
    {
        var env = With((EnvironmentOverrides.RequestTimeoutVariable, value));
        Assert.Null(env.LlmRequestTimeoutSeconds);
        var effective = env.ApplyTo(new AppSettingsData());
        Assert.Equal(3600, effective.LlmRequestTimeoutSeconds);
        Assert.DoesNotContain(EnvironmentOverrides.RequestTimeoutVariable, env.ActiveVariables());
    }

    [Theory]
    [InlineData("3601", 3601.0)]      // over the request ceiling, under the turn's
    [InlineData("21600", 21600.0)]
    [InlineData("21601", null)]
    public void TurnTimeout_HasItsOwnCeiling(string value, double? expected)
    {
        var env = With((EnvironmentOverrides.TurnTimeoutVariable, value));
        Assert.Equal(expected, env.LlmTurnTimeoutSeconds);
        var effective = env.ApplyTo(new AppSettingsData());
        Assert.Equal(expected ?? 21600, effective.LlmTurnTimeoutSeconds);
        Assert.Equal(expected is not null, env.ActiveVariables().Contains(EnvironmentOverrides.TurnTimeoutVariable));
    }

    [Fact]
    public void Timeout_ParsesInvariantDecimal()
    {
        var env = With((EnvironmentOverrides.TurnTimeoutVariable, "1.5"));
        Assert.Equal(1.5, env.LlmTurnTimeoutSeconds);
    }

    [Fact]
    public void ActiveVariables_ListsOnlyWhatIsSet()
    {
        var env = With(
            (EnvironmentOverrides.HomeVariable, "C:/x"),
            (EnvironmentOverrides.LlmReasoningVariable, "xhigh"),
            (EnvironmentOverrides.TtsUrlVariable, "http://t"));
        Assert.Equal(new[] { EnvironmentOverrides.HomeVariable, EnvironmentOverrides.TtsUrlVariable, EnvironmentOverrides.LlmReasoningVariable }, env.ActiveVariables());   // declaration order, the newest last
    }
    [Fact]
    public void Describe_ListsTheVariablesInForce_TheKeyAsSet_NullForNone()
    {
        Assert.Null(With().Describe());
        Assert.Null(With((EnvironmentOverrides.TtsSpeedVariable, "fast")).Describe());   // an unusable value is not in force

        string? line = With((EnvironmentOverrides.LlmUrlVariable, "http://h:1/v1"), (EnvironmentOverrides.LlmApiKeyVariable, "sk-secret"), (EnvironmentOverrides.TtsSpeedVariable, "1.3")).Describe();

        Assert.Equal("NEONSIDEKICK_LLM_URL=http://h:1/v1, NEONSIDEKICK_LLM_API_KEY=(set), NEONSIDEKICK_TTS_SPEED=1.3", line);
        Assert.Equal("(set)", EnvironmentOverrides.SecretSet);
        Assert.Equal("Environment", EnvironmentOverrides.Category);
    }
}
