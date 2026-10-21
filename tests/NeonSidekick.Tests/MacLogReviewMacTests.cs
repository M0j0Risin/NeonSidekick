using System.Diagnostics;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Platform;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The 2026-10-20 code review's fixes that only a Mac shows: a crash report that is a link or a FIFO is never read (and never blocks), a
/// report folder that is a link is not listed (<see cref="MacFiles"/>), and stderr's quiet scope keeps its saved copy close-on-exec and
/// ends itself at its ceiling (<see cref="MacStderr"/>). Skipped off a Mac.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacLogReviewMacTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly string _root = Directory.CreateTempSubdirectory("neon-maclog-mac-").FullName;
    private readonly string _user;
    private readonly string _outside;

    public MacLogReviewMacTests()
    {
        _user = Path.Combine(_root, "user");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_user);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private MacLogPlaces Places => new([new MacCrashFolder(_user, "user")], Path.Combine(_root, "install.log"), _root);

    private AIFunction Tool(string name) =>
        ChatScreen.MacLogTools(new MacLogAccess(new FakeUnifiedLog(), () => true, new WorkingDirectory(() => _root, _time), _time, Places), () => new AppSettingsData { EventLogTools = true })
            .Single(t => t.Name == name);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "maclog", name);

    [MacFact]
    public async Task AReportThatIsALinkOrAFifo_IsNeverRead_AndNeverBlocks()
    {
        File.Copy(Fixture("Widget-2026-09-11-150000.ips"), Path.Combine(_user, "Widget-2026-09-11-150000.ips"));
        string secret = Path.Combine(_outside, "id_rsa");
        File.WriteAllText(secret, "Command: TOP SECRET KEY\n");
        File.CreateSymbolicLink(Path.Combine(_user, "Evil-2026-09-11-150000.diag"), secret);
        using (var mkfifo = Process.Start(new ProcessStartInfo("/usr/bin/mkfifo", Path.Combine(_user, "Pipe-2026-09-11-150000.ips")) { UseShellExecute = false })!)
        {
            await mkfifo.WaitForExitAsync();
            Assert.Equal(0, mkfifo.ExitCode);
        }

        var search = Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("text", "SECRET"));
        string found = await search.WaitAsync(TimeSpan.FromSeconds(20));
        string link = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Evil-2026-09-11-150000.diag")).WaitAsync(TimeSpan.FromSeconds(20));
        string pipe = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Pipe-2026-09-11-150000.ips")).WaitAsync(TimeSpan.FromSeconds(20));
        string all = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes")).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.DoesNotContain("TOP SECRET", found + link + pipe + all, StringComparison.Ordinal);
        Assert.Equal(MacLogText.NoEntry("Evil-2026-09-11-150000.diag", "crashes"), link);
        Assert.Equal(MacLogText.NoEntry("Pipe-2026-09-11-150000.ips", "crashes"), pipe);
        Assert.StartsWith("crashes on this Mac: 1 report,", all, StringComparison.Ordinal);
        Assert.Equal(MacFiles.Kind.Link, MacFiles.KindOf(Path.Combine(_user, "Evil-2026-09-11-150000.diag")));
        Assert.Equal(MacFiles.Kind.Other, MacFiles.KindOf(Path.Combine(_user, "Pipe-2026-09-11-150000.ips")));
        Assert.Null(MacFiles.OpenRegular(Path.Combine(_user, "Pipe-2026-09-11-150000.ips")));
        Assert.Throws<IOException>(() => CrashReports.ReadText(Path.Combine(_user, "Evil-2026-09-11-150000.diag"), 100, out _));
    }

    [MacFact]
    public async Task ARetiredFolderThatIsALink_IsNotListed()
    {
        File.Copy(Fixture("Widget-2026-09-11-150000.ips"), Path.Combine(_outside, "Widget-2026-09-11-150000.ips"));
        Directory.CreateSymbolicLink(Path.Combine(_user, CrashReports.Retired), _outside);

        string all = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"));
        string one = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Widget-2026-09-11-150000.ips"));

        Assert.StartsWith("crashes on this Mac: 0 reports,", all, StringComparison.Ordinal);
        Assert.Equal(MacLogText.NoEntry("Widget-2026-09-11-150000.ips", "crashes"), one);
    }

    [MacFact]
    public void TheQuietScope_KeepsItsCopyCloseOnExec_AndEndsItselfAtItsCeiling()
    {
        MacStderr.Restore();
        ulong? before = MacStderr.Inode();
        var scope = MacStderr.Quiet(TimeSpan.FromMilliseconds(100));
        try
        {
            Assert.True(MacStderr.SavedCloseOnExec);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (MacStderr.Depth > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.Equal(0, MacStderr.Depth);   // the owner never disposed it: the ceiling did
        }
        finally
        {
            scope.Dispose();   // a second end does nothing
            MacStderr.Restore();
        }

        Assert.Equal(0, MacStderr.Depth);
        Assert.False(MacStderr.Silenced);
        Assert.Equal(before, MacStderr.Inode());
    }
}
