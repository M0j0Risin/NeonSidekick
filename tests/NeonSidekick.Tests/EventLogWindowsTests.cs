using System.Runtime.Versioning;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using Microsoft.Extensions.AI;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="WindowsEventLogSystem"/> over this PC's own logs (2026-10-15, the event-log plan's real-reader facts): read-only — the
/// channels, the newest Application event rendered, formatted and parsed, its message formatted from rewritten inserts, an export of the
/// last day's Application events to a temp folder read back as an <c>.evtx</c>, <see cref="EventLogSearch"/> and the tools over the real
/// reader, the combined System and EventData XPath accepted, and Security either read or refused with 5 (unelevated).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogWindowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neon-eventlog-").FullName;
    private readonly WindowsEventLogSystem _system = new();

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

    [WindowsFact]
    public void Channels_ListTheClassicLogs()
    {
        var channels = _system.Channels(EventLogTarget.Local, CancellationToken.None);

        Assert.Contains("Application", channels, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("System", channels, StringComparer.OrdinalIgnoreCase);
        Assert.True(channels.Count > 50);
    }

    [WindowsFact]
    public void Info_ReadsApplicationsFacts()
    {
        var info = _system.Info(EventLogTarget.Local, "Application", CancellationToken.None);

        Assert.True(info.Records > 0);
        Assert.True(info.SizeBytes > 0);
        Assert.NotNull(info.LastWrite);
        Assert.True(info.Enabled);
        Assert.Equal(EventChannelType.Admin, info.Type);
    }

    [WindowsFact]
    public void Query_TheNewestApplicationEvent_RendersFormatsAndParses()
    {
        using var reader = _system.Query(EventLogTarget.Local, "Application", "*", CancellationToken.None);
        var batch = reader.Next();

        Assert.NotNull(batch);
        Assert.NotEmpty(batch);
        var newest = batch[0];
        Assert.Equal("Application", newest.System.Channel, ignoreCase: true);
        Assert.True(newest.System.Record > 0);
        Assert.NotNull(newest.System.Time);
        Assert.StartsWith("<Event", newest.Xml, StringComparison.Ordinal);
        Assert.Equal(newest.System.Record, EventXml.System(newest.Xml).Record);
        Assert.Equal(newest.System.Provider, EventXml.System(newest.Xml).Provider);
        // Newest first.
        if (batch.Count > 1)
        {
            Assert.True(batch[0].System.Record > batch[1].System.Record);
        }

        var view = EventLogSearch.View(newest, redacted: false, withXml: true);
        Assert.Equal(newest.Xml, view.Xml);
    }

    [WindowsFact]
    public void Format_FromRewrittenInserts_ReachesTheMessage()
    {
        // An event with a string insert whose message shows it: the rewrite stands in the formatted text at its full length.
        using var reader = _system.Query(EventLogTarget.Local, "System", "*[System[Provider[@Name='Service Control Manager'] and (EventID=7036 or EventID=7040)]]", CancellationToken.None);
        var batch = reader.Next();
        if (batch is not { Count: > 0 })
        {
            return;   // nothing to format on this PC
        }

        var raw = batch[0];
        var own = raw.Format();
        var rewritten = raw.Format((_, _) => "REWRITTEN-VALUE-LONGER-THAN-THE-ORIGINAL");

        Assert.NotNull(own.Text);
        Assert.Contains("REWRITTEN-VALUE-LONGER-THAN-THE-ORIGINAL", rewritten.Text, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Query_SystemAndEventDataTogether_IsAccepted()
    {
        var query = new EventLogQuery
        {
            Log = "System",
            Providers = ["Service Control Manager"],
            Fields = [new EventFieldTerm("param1", "Windows Update")],
            Levels = EventLevels.Information,
        };
        var (xpath, error) = EventLogXPath.Build(query, serverFields: true);
        Assert.Null(error);

        using var reader = _system.Query(EventLogTarget.Local, "System", xpath!, CancellationToken.None);
        _ = reader.Next();   // no 15001: the expression is one the log takes
    }

    [WindowsFact]
    public void Query_OverTheTermBudget_IsRefusedBeforeTheLog()
    {
        var query = new EventLogQuery { Log = "System", Ids = Enumerable.Range(1, 25).Select(i => new EventIdRange(i, i)).ToList() };

        var (xpath, error) = EventLogXPath.Build(query, serverFields: true);

        Assert.Null(xpath);
        Assert.Contains("25 terms", error, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Export_ThenReadBack_TheFilesEvents()
    {
        string file = Path.Combine(_root, "app.evtx");
        WindowsEventLogSystem.Export("Application", "*[System[TimeCreated[timediff(@SystemTime) <= 604800000]]]", file);

        using var reader = _system.Query(EventLogTarget.File(file), "app.evtx", "*", CancellationToken.None);
        var batch = reader.Next();

        Assert.NotNull(batch);
        Assert.NotEmpty(batch);
        Assert.Equal("Application", batch[0].System.Channel, ignoreCase: true);
        var info = _system.Info(EventLogTarget.File(file), "app.evtx", CancellationToken.None);
        Assert.True(info.SizeBytes > 0);
    }

    [WindowsFact]
    public async Task Tools_OverTheRealReader_AnswerForThisPc()
    {
        string file = Path.Combine(_root, "app.evtx");
        WindowsEventLogSystem.Export("Application", "*[System[TimeCreated[timediff(@SystemTime) <= 604800000]]]", file);
        var files = new WorkingDirectory(() => _root, TimeProvider.System);
        var settings = new AppSettingsData { EventLogTools = true };
        var access = new EventLogAccess(_system, () => EventLogCatalog.LocalOnly, () => null, files, TimeProvider.System);

        string search = (string)(await new EventLogSearchTool(access, () => settings).InvokeAsync(new AIFunctionArguments { ["log"] = "app", ["level"] = "all", ["limit"] = 3 }))!;
        string fromFile = (string)(await new EventLogSearchTool(access, () => settings).InvokeAsync(new AIFunctionArguments { ["log"] = "app.evtx", ["level"] = "all", ["limit"] = 2 }))!;
        string summary = (string)(await new EventLogSummaryTool(access, () => settings).InvokeAsync(new AIFunctionArguments { ["log"] = "Application", ["since"] = "7d", ["level"] = "all" }))!;
        string logs = (string)(await new EventLogLogsTool(access, () => settings).InvokeAsync(new AIFunctionArguments { ["filter"] = "Application" }))!;

        Assert.StartsWith("Application on local: 3 events", search, StringComparison.Ordinal);
        Assert.Contains("next: log=Application level=all before_record=", search, StringComparison.Ordinal);
        Assert.StartsWith("app.evtx on this PC: 2 events", fromFile, StringComparison.Ordinal);
        Assert.StartsWith("Application on local since ", summary, StringComparison.Ordinal);
        Assert.Contains("  Application  ", logs, StringComparison.Ordinal);
        Assert.Contains("app.evtx", logs, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Security_IsReadOrRefusedWithAccessDenied()
    {
        try
        {
            using var reader = _system.Query(EventLogTarget.Local, "Security", "*", CancellationToken.None);
            _ = reader.Next();
        }
        catch (EventLogException ex)
        {
            Assert.Equal(5, ex.Code);
        }
    }

    [WindowsFact]
    public void Query_NoSuchChannel_Is15007()
    {
        var ex = Assert.Throws<EventLogException>(() => _system.Query(EventLogTarget.Local, "NoSuch/Channel", "*", CancellationToken.None).Dispose());

        Assert.Equal(15007, ex.Code);
    }

    [WindowsFact]
    public void Query_ABadExpression_Is15001()
    {
        var ex = Assert.Throws<EventLogException>(() => _system.Query(EventLogTarget.Local, "System", "*[System[Foo((]]", CancellationToken.None).Dispose());

        Assert.Equal(15001, ex.Code);
    }

    [WindowsFact]
    public void Cancel_BeforeTheRead_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _system.Query(EventLogTarget.Local, "System", "*", cts.Token).Dispose());
    }

    [WindowsFact]
    public void TwoConcurrentReads_OneCancelled_TheOtherCompletes()
    {
        using var cts = new CancellationTokenSource();
        using var kept = _system.Query(EventLogTarget.Local, "Application", "*", CancellationToken.None);
        using var cancelled = _system.Query(EventLogTarget.Local, "System", "*", cts.Token);
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => cancelled.Next());
        Assert.NotNull(kept.Next());
    }
}
