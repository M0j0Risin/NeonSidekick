using NeonSidekick.App;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>The pure parts of the macOS build's launches and sentences (2026-10-06): pinned on every OS.</summary>
public sealed class MacPortTests
{
    [Fact]
    public void UnixEditorStart_HandsThePathOverAsAnArgument_NeverSplicedIn()
    {
        var start = DraftFile.UnixStart("code --wait", "/Users/me/it's a \"draft\".md");

        Assert.Equal("/bin/sh", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(["-c", "code --wait \"$1\"", "sh", "/Users/me/it's a \"draft\".md"], start.ArgumentList);
    }

    [Fact]
    public void EditorStart_IsCmdOnWindows_AndShElsewhere()
    {
        var start = DraftFile.EditorStart("code --wait", "x.md");

        Assert.Equal(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh", start.FileName);
    }

    [Fact]
    public void TheCredentialSentences_NameThisOsStore()
    {
        Assert.Equal(OperatingSystem.IsMacOS() ? "the macOS Keychain" : "Windows Credential Manager", SqlText.CredentialStore);
        Assert.Equal(
            OperatingSystem.IsMacOS() ? "security add-generic-password -s t -a u -w" : "cmdkey /generic:t /user:u /pass",
            SqlText.CredentialCommand("t", "u"));
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), WindowsCredentials.CanProtect);
        Assert.True(WindowsCredentials.IsProtected("keychain:abc"));
        Assert.True(WindowsCredentials.IsProtected("dpapi:abc"));
        Assert.False(WindowsCredentials.IsProtected("plain"));
    }

    [UnixFact]
    public void TheKeychain_RoundTripsASecret_OnMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var stored = WindowsCredentials.Protect("päss-🔑");
        Assert.Null(stored.Error);
        Assert.StartsWith(WindowsCredentials.KeychainPrefix, stored.Value);
        Assert.Equal("päss-🔑", WindowsCredentials.Unprotect(stored.Value!).Value);
        Assert.NotNull(WindowsCredentials.Unprotect("dpapi:AAAA").Error);
    }
}
