using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Sql;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Unc;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a real share is named (2026-09-30): <c>NEONSIDEKICK_TEST_UNC_SHARE</c> holds a
/// <c>\\server\share[\folder]</c> path the user running the tests can read (on a workstation <c>\\localhost\C$\Windows</c> does);
/// <c>NEONSIDEKICK_TEST_UNC_USER</c> and <c>NEONSIDEKICK_TEST_UNC_PASSWORD</c>, when both are set, are a second account that can read
/// it too, for the runas path. Skipped without; never a CI safety net. Nothing is ever written to the share.
/// </summary>
internal static class LiveUnc
{
    public const string ShareVariable = "NEONSIDEKICK_TEST_UNC_SHARE";
    public const string UserVariable = "NEONSIDEKICK_TEST_UNC_USER";
    public const string PasswordVariable = "NEONSIDEKICK_TEST_UNC_PASSWORD";

    public static readonly string? Share = Usable(Environment.GetEnvironmentVariable(ShareVariable));
    public static readonly string? User = Environment.GetEnvironmentVariable(UserVariable) is { Length: > 0 } user ? user : null;
    public static readonly string? Password = Environment.GetEnvironmentVariable(PasswordVariable) is { Length: > 0 } password ? password : null;

    public static string Unavailable => $"{ShareVariable} is not set to a \\\\server\\share path.";

    private static string? Usable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.Trim().StartsWith(@"\\", StringComparison.Ordinal) && UncShareConfig.PathProblem(path) is null ? path.Trim() : null;
}

/// <summary>Skips unless <see cref="LiveUnc.Share"/> names a share.</summary>
public sealed class LiveUncFactAttribute : FactAttribute
{
    public LiveUncFactAttribute()
    {
        if (LiveUnc.Share is null)
        {
            Skip = LiveUnc.Unavailable;
        }
    }
}

/// <summary>The UNC tools against a real share (2026-09-30): reached as the user, read and searched over SMB, a bogus runas account refused at the network sign-in, the second account's runas when one is given.</summary>
public sealed class LiveUncTests
{
    private readonly AppSettingsData _settings = new() { UncTools = true };
    private readonly ManualTimeProvider _time = new();

    private static UncNamedShare Windows() => new("live", new UncShareConfig { Path = LiveUnc.Share }, "test");

    private static UncNamedShare RunAs(string user, string password) =>
        new("live", new UncShareConfig { Path = LiveUnc.Share, Auth = "runas", User = user, Password = WindowsCredentials.Protect(password).Value }, "test");

    private IReadOnlyList<AIFunction> Tools(UncNamedShare share) =>
        ChatScreen.UncTools(new UncAccess(() => new UncCatalog([share], []), _time), new WorkingDirectory(() => Path.GetTempPath(), _time), () => _settings);

    private static async Task<string> Invoke<T>(IReadOnlyList<AIFunction> tools, params (string Name, object? Value)[] pairs) where T : AIFunction =>
        ToolAnswers.Text(await tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))));

    [LiveUncFact]
    public async Task AsTheUser_TheShareIsReached_Listed_AndSearched_OverSmb()
    {
        var tools = Tools(Windows());

        string listed = await Invoke<UncSharesTool>(tools, ("check", true));
        Assert.Contains("reachable (", listed);
        string listing = await Invoke<UncSearchTool>(tools);
        Assert.StartsWith(UncText.ShareName(Windows()) + " (", listing);
        string found = await Invoke<UncSearchTool>(tools, ("files", "*.ini"), ("depth", 1));
        Assert.DoesNotContain("Error:", found);
    }

    /// <summary>
    /// A bogus netonly account against a remote share. Against this machine (<c>\\localhost</c>, <c>\\127.0.0.1</c>, its own name) it
    /// proves nothing: NTLM's loopback detection signs the session in with the caller's own token, the netonly credentials unread —
    /// found on 2026-09-30, where <c>\\localhost\C$</c> listed under a bogus account — so the test returns there.
    /// </summary>
    [LiveUncFact]
    public async Task ABogusRunAsAccount_IsRefusedAtTheNetworkSignIn_NotAtTheLogon()
    {
        string server = LiveUnc.Share![2..].Split('\\')[0];
        if (server is "localhost" or "127.0.0.1" or "::1" || string.Equals(server, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var share = RunAs(@"NEONSIDEKICK-TEST\nobody", "not-a-password");

        var result = await new UncAccess(() => new UncCatalog([share], []), _time).RunAsync(share, write: false, files => files.List("").Entries.Count, CancellationToken.None);

        // The logon itself never checks (NEW_CREDENTIALS); the server does, at the SMB sign-in: a bad account or a refusal, never a listing.
        Assert.NotNull(result.Error);
        Assert.True(result.Error == UncText.BadAccount("live", @"NEONSIDEKICK-TEST\nobody") || result.Error!.StartsWith("Error: share 'live'", StringComparison.Ordinal), result.Error);
    }

    [LiveUncFact]
    public async Task AsTheSecondAccount_TheShareIsRead_WhenOneIsGiven()
    {
        if (LiveUnc.User is null || LiveUnc.Password is null)
        {
            return;   // NEONSIDEKICK_TEST_UNC_USER / _PASSWORD not set: nothing to prove
        }

        var tools = Tools(RunAs(LiveUnc.User, LiveUnc.Password));
        string listing = await Invoke<UncSearchTool>(tools);
        Assert.DoesNotContain("Error:", listing);
    }

    [LiveUncFact]
    public async Task UncCheck_PassesOnTheShare()
    {
        var console = new Spectre.Console.Testing.TestConsole();
        int exit = await UncCheck.RunAsync(console, new UncCatalog([Windows()], []), "live", _time, CancellationToken.None);
        Assert.True(exit == 0, console.Output);
        Assert.Contains("UNC CHECK PASS", console.Output);
    }
}
