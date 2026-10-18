using System.Diagnostics;
using System.Globalization;
using NeonSidekick.EventLog;

namespace NeonSidekick.App;

/// <summary>
/// The Mac event logs' proofs (2026-10-18, the Mac event-log plan's phase 5), shared by the smoke run (<c>eventlog:oslog</c>,
/// <c>eventlog:crashes</c>, <c>eventlog:archive-error</c>) and <c>--eventlog-check local</c>: the store opens; a short read comes back;
/// every predicate fragment is accepted by OSLog (one read naming them all, held to a ten-second window so it ends at once); fd 2 points
/// where it did before (the quiet scope put the terminal back); the crash folders list and the newest report is described; install.log's
/// newest entry reads; a missing archive is OSLog's refusal, never an Objective-C exception (which would end a NativeAOT process). Reads
/// only. Over any reader, so the tests run them on the fake; the inode is read on a Mac alone.
/// </summary>
internal static class MacLogProbes
{
    /// <summary>The store opens with nothing read.</summary>
    public static SmokeCheck Store(IUnifiedLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            log.Probe(UnifiedLogTarget.Local);
            return new SmokeCheck("eventlog:store", true, "this Mac's unified log opens");
        }
        catch (UnifiedLogException ex)
        {
            return new SmokeCheck("eventlog:store", false, MacLogErrors.Sentence(ex));
        }
    }

    /// <summary>The last two minutes at every level: how many came back in the first batch, the newest's time.</summary>
    public static SmokeCheck Read(IUnifiedLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            var started = Stopwatch.GetTimestamp();
            using var reader = log.Open(UnifiedLogTarget.Local, new MacLogFilter(time.GetUtcNow().AddMinutes(-2), []), CancellationToken.None);
            var batch = reader.Next();
            string took = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
            return batch is { Count: > 0 }
                ? new SmokeCheck("eventlog:read", true, $"{batch.Count.ToString(CultureInfo.InvariantCulture)} entries in the first batch, the newest at {EventLogText.Time(batch[0].Time, time.LocalTimeZone)} ({took} ms)")
                : new SmokeCheck("eventlog:read", true, $"no entry in the last two minutes ({took} ms)");
        }
        catch (UnifiedLogException ex)
        {
            return new SmokeCheck("eventlog:read", false, MacLogErrors.Sentence(ex));
        }
    }

    /// <summary>One read naming every fragment of <see cref="MacLogPredicate"/>, over ten seconds: accepted, and ended by the time limit.</summary>
    public static SmokeCheck Predicates(IUnifiedLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        var filter = new MacLogFilter(now.AddSeconds(-10), MacLogPredicate.LevelWords, now, "neonsidekick-smoke", 1, "com.example.neonsidekick", "smoke", "\" OR 1==1 %@ %K",
            ["mediaanalysisd"]);
        try
        {
            var started = Stopwatch.GetTimestamp();
            int read = 0;
            using (var reader = log.Open(UnifiedLogTarget.Local, filter, CancellationToken.None))
            {
                while (reader.Next() is { } batch)
                {
                    read += batch.Count;
                }
            }

            string took = Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
            int fragments = MacLogPredicate.Placeholders(MacLogPredicate.Build(filter).Format);
            return new SmokeCheck("eventlog:predicates", read == 0, $"{fragments.ToString(CultureInfo.InvariantCulture)} values in every fragment accepted; {read.ToString(CultureInfo.InvariantCulture)} matched; ended in {took} ms");
        }
        catch (UnifiedLogException ex)
        {
            return new SmokeCheck("eventlog:predicates", false, MacLogErrors.Sentence(ex));
        }
    }

    /// <summary>fd 2's inode before and after a read, and no quiet scope left open.</summary>
    public static SmokeCheck Stderr(IUnifiedLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck("eventlog:stderr", true, "skipped: not macOS");
        }

        Platform.MacStderr.Restore();
        ulong? before = Platform.MacStderr.Inode();
        _ = Read(log, time);
        Thread.Sleep(Platform.MacStderr.Linger + TimeSpan.FromMilliseconds(250));   // the linger keeps fd 2 quiet a moment after the read
        ulong? after = Platform.MacStderr.Inode();
        bool same = before is not null && before == after && Platform.MacStderr.Depth == 0 && !Platform.MacStderr.Silenced;
        return new SmokeCheck("eventlog:stderr", same, same ? "fd 2 is where it was a moment after a read" : $"fd 2 moved: {before?.ToString(CultureInfo.InvariantCulture) ?? "?"} → {after?.ToString(CultureInfo.InvariantCulture) ?? "?"}, depth {Platform.MacStderr.Depth.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>A missing archive: OSLog's refusal worded, never an exception the runtime cannot catch.</summary>
    public static SmokeCheck ArchiveError(IUnifiedLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string missing = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", "missing-" + Guid.NewGuid().ToString("N") + MacLogAccess.ArchiveExtension);
        try
        {
            log.Probe(UnifiedLogTarget.Archive(missing));
            return new SmokeCheck("eventlog:archive-error", false, "a missing archive opened");
        }
        catch (UnifiedLogException ex)
        {
            string sentence = MacLogErrors.Sentence(ex, missing);
            return new SmokeCheck("eventlog:archive-error", true, $"refused ({ex.Domain} {ex.Code.ToString(CultureInfo.InvariantCulture)}): {sentence}");
        }
    }

    /// <summary>Each crash folder's state, the week's count, and the newest report described.</summary>
    public static IReadOnlyList<SmokeCheck> Crashes(MacLogPlaces places, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(time);
        var (reports, folders) = CrashReports.List(places.CrashFolders, time.GetUtcNow() - MacFileLogs.CrashWindow, null, CancellationToken.None);
        var checks = new List<SmokeCheck>();
        foreach (var (folder, refused) in folders)
        {
            int count = reports.Count(r => r.Owner == folder.Owner);
            checks.Add(new SmokeCheck("eventlog:crashes-" + folder.Owner, true, refused is null
                ? $"{folder.Path}: {EventLogText.Count(count, "report")} in the last 7 days"
                : $"{folder.Path}: {refused}"));
        }

        if (reports.FirstOrDefault() is { } newest)
        {
            try
            {
                var detail = CrashReports.Describe(newest, MacFileLogs.Redactor(places, redacted: true));
                checks.Add(new SmokeCheck("eventlog:crash-report", true, $"{newest.Name}: {newest.Kind}, {detail.Summary}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                checks.Add(new SmokeCheck("eventlog:crash-report", false, $"{newest.Name}: {ex.Message}"));
            }
        }

        return checks;
    }

    /// <summary>install.log's newest entry.</summary>
    public static SmokeCheck Install(MacLogPlaces places, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(time);
        try
        {
            using var stream = new FileStream(places.InstallLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return InstallLog.Backward(stream).FirstOrDefault() is { } newest
                ? new SmokeCheck("eventlog:install", true, $"{places.InstallLog}: newest entry {EventLogText.Time(newest.Time, time.LocalTimeZone)} from {(newest.Process.Length > 0 ? newest.Process : "?")}")
                : new SmokeCheck("eventlog:install", true, $"{places.InstallLog}: empty");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SmokeCheck("eventlog:install", false, $"{places.InstallLog}: {ex.Message}");
        }
    }
}
