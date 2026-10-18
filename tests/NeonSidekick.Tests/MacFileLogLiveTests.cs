using NeonSidekick.EventLog;

namespace NeonSidekick.Tests;

/// <summary>
/// This Mac's own logs that are files read for real (2026-10-18, the Mac event-log plan's phase 3): every crash and diagnostic report in
/// the account's and the system's folders (a folder this account may not list is named, never thrown), each described without a throw,
/// and install.log read backward against a forward read of its newest megabyte. Nothing is printed: the reports hold paths and names.
/// Skipped off a Mac.
/// </summary>
public sealed class MacFileLogLiveTests
{
    [MacFact]
    public void EveryReportHere_IsListedAndDescribed_WithoutAThrow()
    {
        var places = MacLogPlaces.Default;
        var (reports, folders) = CrashReports.List(places.CrashFolders, DateTimeOffset.UnixEpoch, null, CancellationToken.None);

        Assert.Equal(places.CrashFolders.Count, folders.Count);
        foreach (var report in reports)
        {
            Assert.True(CrashReports.IsReportName(report.Name), report.Name);
            Assert.False(string.IsNullOrWhiteSpace(report.Process), report.Name);
            var detail = CrashReports.Describe(report, MacFileLogs.Redactor(places, redacted: true));
            Assert.False(string.IsNullOrWhiteSpace(detail.Summary), report.Name);
            Assert.DoesNotContain(places.Home + "/", detail.Text, StringComparison.Ordinal);
            Assert.Equal(report.Name, CrashReports.Find(places.CrashFolders, report.Name)?.Name);
        }
    }

    [MacFact]
    public void InstallLog_ReadsBackward_AsAForwardReadOfItsEnd()
    {
        string path = MacLogPlaces.Default.InstallLog;
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = stream.Length;
        var backward = InstallLog.Backward(stream, end).TakeWhile(e => e.Offset >= Math.Max(0, end - 1_000_000)).ToList();
        Assert.NotEmpty(backward);
        for (int i = 1; i < backward.Count; i++)
        {
            Assert.True(backward[i].Offset < backward[i - 1].Offset);
        }

        foreach (var entry in backward.Take(200))
        {
            Assert.Equal(entry, InstallLog.At(stream, entry.Offset));
        }
    }
}
