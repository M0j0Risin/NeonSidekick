using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>The three profile keys kept DPAPI-encrypted (2026-09-28): <see cref="SettingsSecrets"/> and <see cref="LlmEndpoint.KeyOf"/>.</summary>
public class SettingsSecretsTests
{
    [Fact]
    public void Reveal_ReadsPlainAsIs_EmptyAsNone_AndAnUnreadableBlobAsNone()
    {
        Assert.Null(SettingsSecrets.Reveal(null));
        Assert.Null(SettingsSecrets.Reveal("  "));
        Assert.Equal("plain-key", SettingsSecrets.Reveal(" plain-key "));
        Assert.Null(SettingsSecrets.Reveal(WindowsCredentials.ProtectedPrefix + "bm90IGEgYmxvYg=="));   // not ours to read
    }

    [Fact]
    public void Protect_RoundTrips_AndKeepsAnEncryptedValue()
    {
        Assert.Equal("", SettingsSecrets.Protect("  ", out string? none));
        Assert.Null(none);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string stored = SettingsSecrets.Protect(" sk-secret ", out string? error);
        Assert.Null(error);
        Assert.True(WindowsCredentials.IsProtected(stored));
        Assert.DoesNotContain("sk-secret", stored);
        Assert.Equal("sk-secret", SettingsSecrets.Reveal(stored));
        Assert.Equal(stored, SettingsSecrets.Protect(stored, out _));   // already encrypted: kept
    }

    [Fact]
    public void ProtectAtRest_EncryptsTheThreeKeys_ButNotThePlaceholder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var data = new AppSettingsData { LlmApiKey = "sk-llm", ClaudeApiKey = "sk-ant", HomeAssistantToken = "ha" };
        Assert.True(SettingsSecrets.ProtectAtRest(data));
        Assert.Equal(("sk-llm", "sk-ant", "ha"), (SettingsSecrets.Reveal(data.LlmApiKey), SettingsSecrets.Reveal(data.ClaudeApiKey), SettingsSecrets.Reveal(data.HomeAssistantToken)));
        Assert.True(WindowsCredentials.IsProtected(data.LlmApiKey));
        Assert.True(WindowsCredentials.IsProtected(data.ClaudeApiKey));
        Assert.True(WindowsCredentials.IsProtected(data.HomeAssistantToken));

        string before = data.LlmApiKey;
        Assert.False(SettingsSecrets.ProtectAtRest(data));   // nothing left to encrypt
        Assert.Equal(before, data.LlmApiKey);

        var fresh = new AppSettingsData();   // LLM API key "empty", the others unset
        Assert.False(SettingsSecrets.ProtectAtRest(fresh));
        Assert.Equal(LlmEndpoint.DefaultApiKey, fresh.LlmApiKey);
        Assert.Equal("", fresh.ClaudeApiKey);
    }

    [Fact]
    public void KeyOf_DecryptsTheLlmKey_PassesPlainThrough_AndAnUnreadableOneIsThePlaceholder()
    {
        Assert.Equal("k", LlmEndpoint.KeyOf(new AppSettingsData { LlmApiKey = "k" }));
        Assert.Equal("", LlmEndpoint.KeyOf(new AppSettingsData { LlmApiKey = "" }));   // blank as before: the callers default it
        Assert.Equal(LlmEndpoint.DefaultApiKey, LlmEndpoint.KeyOf(new AppSettingsData { LlmApiKey = WindowsCredentials.ProtectedPrefix + "bm90IGEgYmxvYg==" }));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("sk-real", LlmEndpoint.KeyOf(new AppSettingsData { LlmApiKey = SettingsSecrets.Protect("sk-real", out _) }));
        }
    }
}
