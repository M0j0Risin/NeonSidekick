using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves once per assembly whether a computer whose event logs can be read over RPC is named (2026-10-15, the event-log plan's phase 3):
/// <c>NEONSIDEKICK_TEST_EVENTLOG_COMPUTER</c> holds its host (<c>localhost</c> is a loopback session, which proves the session path on any
/// Windows PC), read as the user running the tests. Windows only; skipped without; never a CI safety net. Nothing is ever changed.
/// </summary>
internal static class LiveEventLog
{
    public const string ComputerVariable = "NEONSIDEKICK_TEST_EVENTLOG_COMPUTER";

    public static readonly string? Host = Environment.GetEnvironmentVariable(ComputerVariable) is { Length: > 0 } host ? host.Trim() : null;

    public static string? Unavailable =>
        !OperatingSystem.IsWindows() ? "The event logs are Windows' own."
        : Host is null ? $"{ComputerVariable} is not set to a computer's host."
        : null;
}

/// <summary>Skips unless <see cref="LiveEventLog"/> names a computer.</summary>
public sealed class LiveEventLogFactAttribute : FactAttribute
{
    public LiveEventLogFactAttribute()
    {
        if (LiveEventLog.Unavailable is { } why)
        {
            Skip = why;
        }
    }
}

/// <summary>The event-log tools against a real computer over RPC (2026-10-15): its channels, its newest System events, and the check.</summary>
public sealed class LiveEventLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neon-eventlog-live-").FullName;
    private readonly AppSettingsData _settings = new() { EventLogTools = true, EventLogComputersOffered = ["live"] };
    private readonly EventLogComputer _computer = new("live", new EventLogComputerConfig { Host = LiveEventLog.Host, Description = "the live gate's computer" });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private EventLogAccess Access()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        return new EventLogAccess(new WindowsEventLogSystem(), () => new EventLogCatalog([EventLogCatalog.LocalOnly.Computers[0], _computer], []), () => _settings.EventLogComputersOffered, new WorkingDirectory(() => _root, TimeProvider.System), TimeProvider.System);
    }

    [LiveEventLogFact]
    public async Task Logs_ListTheRemoteChannels_UnderTheRemoteCap()
    {
        string answer = (string)(await new EventLogLogsTool(Access(), () => _settings).InvokeAsync(new AIFunctionArguments { ["filter"] = "System", ["computer"] = "live" }))!;

        Assert.Contains("Logs on live", answer, StringComparison.Ordinal);
        Assert.Contains("  ⟪System⟫  ", answer, StringComparison.Ordinal);   // the channel's name fenced (2026-10-23, the admin review's C10)
    }

    [LiveEventLogFact]
    public async Task Search_ReadsTheNewestSystemEvents_Redacted()
    {
        string answer = (string)(await new EventLogSearchTool(Access(), () => _settings).InvokeAsync(new AIFunctionArguments { ["log"] = "System", ["level"] = "all", ["limit"] = 3, ["computer"] = "live" }))!;

        Assert.StartsWith("System on live: 3 events", answer, StringComparison.Ordinal);
        Assert.Contains(EventLogText.RedactedNote, answer, StringComparison.Ordinal);
        Assert.Contains("computer=live", answer, StringComparison.Ordinal);
    }

    [LiveEventLogFact]
    public async Task Check_ProvesTheComputer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var console = new TestConsole();
        int code = await EventLogCheck.RunAsync(console, new WindowsEventLogSystem(), new EventLogCatalog([_computer], []), "live", new WorkingDirectory(() => _root, TimeProvider.System), TimeProvider.System, CancellationToken.None);

        Assert.True(code == 0, console.Output);
        Assert.Contains("EVENT LOG CHECK PASS", console.Output, StringComparison.Ordinal);
    }
}
