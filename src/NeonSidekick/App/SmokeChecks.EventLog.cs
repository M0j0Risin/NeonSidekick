using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.EventLog;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>eventlog:wevtapi</c> (2026-10-15, the event-log plan): <c>wevtapi.dll</c>'s imports bound on the published binary over this PC's own
    /// logs, read-only — the channels enumerated, Application's facts read, System's newest event queried in reverse, rendered (values and
    /// XML), parsed and formatted — and the refusals' sentences from their codes: a malformed query (15001) and a missing channel (15007).
    /// A system library: nothing joins <see cref="RequiredNativeLibraries"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeEventLog()
    {
        const string name = "eventlog:wevtapi";
        try
        {
            var system = new WindowsEventLogSystem();
            int channels = system.Channels(EventLogTarget.Local, CancellationToken.None).Count;
            var info = system.Info(EventLogTarget.Local, "Application", CancellationToken.None);
            using (var reader = system.Query(EventLogTarget.Local, "System", "*", CancellationToken.None))
            {
                if (reader.Next() is not { Count: > 0 } batch)
                {
                    return new SmokeCheck(name, false, "the System log gave no event");
                }

                var view = EventLogSearch.View(batch[0], redacted: true, withXml: false);
                if (EventXml.System(batch[0].Xml).Record != batch[0].System.Record)
                {
                    return new SmokeCheck(name, false, "the event's XML and its values disagree");
                }

                var context = new EventLogErrorContext(EventLogText.ThisPc, "System");
                string refused = Sentence(() => system.Query(EventLogTarget.Local, "System", "*[System[Foo((]]", CancellationToken.None).Dispose(), context);
                string missing = Sentence(() => system.Query(EventLogTarget.Local, "NoSuch/Channel", "*", CancellationToken.None).Dispose(), context with { Log = "NoSuch/Channel" });
                bool sentences = refused == EventLogText.QueryRefused && missing == EventLogText.NoSuchLog("NoSuch/Channel", EventLogText.ThisPc);
                return new SmokeCheck(name, sentences && channels > 0,
                    $"{channels.ToString(CultureInfo.InvariantCulture)} channels; Application {info.Records?.ToString(CultureInfo.InvariantCulture) ?? "?"} records; System #{batch[0].System.Record.ToString(CultureInfo.InvariantCulture)} {batch[0].System.Provider} {(view.Message is null ? "(no message)" : "formatted")}; 15001 and 15007 {(sentences ? "worded" : "NOT worded: " + refused + " / " + missing)}");
            }
        }
        catch (EventLogException ex)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary>
    /// <c>eventlog:rpc-login</c>: an <c>EvtRpcLogin</c> session to this PC (a loopback, as the user) and its first real call, the channel
    /// list — the remote path's struct and session handling on the published binary. Access denied (5) passes too: the call reached RPC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeEventLogRpcLogin()
    {
        const string name = "eventlog:rpc-login";
        try
        {
            int channels = new WindowsEventLogSystem().Channels(EventLogTarget.Remote("localhost", null), CancellationToken.None).Count;
            return new SmokeCheck(name, channels > 0, $"a loopback session listed {channels.ToString(CultureInfo.InvariantCulture)} channels");
        }
        catch (EventLogException ex) when (ex.Code == 5)
        {
            return new SmokeCheck(name, true, "a loopback session reached RPC and was refused (5)");
        }
        catch (EventLogException ex)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
    }

    /// <summary><c>eventlog:evtx</c>: the last day of Application exported to a temp <c>.evtx</c> (<c>EvtExportLog</c>, this PC's own read) and read back newest first, then deleted.</summary>
    [SupportedOSPlatform("windows")]
    public static SmokeCheck ProbeEventLogEvtx()
    {
        const string name = "eventlog:evtx";
        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", "evtx-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "application.evtx");
            WindowsEventLogSystem.Export("Application", "*[System[TimeCreated[timediff(@SystemTime) <= 604800000]]]", file);
            using var reader = new WindowsEventLogSystem().Query(EventLogTarget.File(file), "application.evtx", "*", CancellationToken.None);
            var batch = reader.Next();
            return batch is { Count: > 0 }
                ? new SmokeCheck(name, true, $"exported {new FileInfo(file).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes; read #{batch[0].System.Record.ToString(CultureInfo.InvariantCulture)} back from {batch[0].System.Channel}")
                : new SmokeCheck(name, true, "exported an empty week of Application; nothing to read back");
        }
        catch (Exception ex) when (ex is EventLogException or IOException or UnauthorizedAccessException)
        {
            return new SmokeCheck(name, false, ex.Message);
        }
        finally
        {
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder);
        }
    }

    private static string Sentence(Action act, EventLogErrorContext context)
    {
        try
        {
            act();
            return "(no refusal)";
        }
        catch (EventLogException ex)
        {
            return EventLogErrors.Sentence(ex.Code, context, ex.Message);
        }
    }
}
