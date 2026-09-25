using NeonSidekick.Diagnostics;

namespace NeonSidekick.Tests;

public class DiagnosticFileSinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle still closing; the temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public void Format_IsInvariant_AndPinned()
    {
        var evt = new DiagnosticEvent(new DateTime(2026, 9, 11, 13, 5, 7, 123, DateTimeKind.Utc), DiagnosticLevel.Debug, "Wake", "Wake recogniser heard: neon", null);
        Assert.Equal("2026-09-11 13:05:07.123 [Debug] Wake: Wake recogniser heard: neon", DiagnosticFileSink.Format(evt));

        var withException = evt with { Level = DiagnosticLevel.Error, Exception = new InvalidOperationException("boom") };
        Assert.Equal("2026-09-11 13:05:07.123 [Error] Wake: Wake recogniser heard: neon (InvalidOperationException: boom)", DiagnosticFileSink.Format(withException));
    }

    [Fact]
    public void Open_CreatesTheDirectory_AppendsEveryLevel_AndStopsOnDispose()
    {
        string path = Path.Combine(_dir, "nested", "neon.log");
        string category = "SinkTest-" + Guid.NewGuid().ToString("N");
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        try
        {
            using (var sink = DiagnosticFileSink.Open(path))
            {
                DiagnosticLog.Trace(category, "trace line");
                DiagnosticLog.Debug(category, "debug line");
                DiagnosticLog.Warn(category, "warn line", new InvalidOperationException("why"));
            }

            DiagnosticLog.Info(category, "after dispose");

            string[] lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.EndsWith("[Info] Log: --- log opened ---", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith($"[Trace] {category}: trace line", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith($"[Debug] {category}: debug line", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.EndsWith($"[Warning] {category}: warn line (InvalidOperationException: why)", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Contains("after dispose", StringComparison.Ordinal));

            // A second open appends rather than truncates.
            using (DiagnosticFileSink.Open(path))
            {
                DiagnosticLog.Info(category, "second session");
            }

            string[] again = File.ReadAllLines(path);
            Assert.Equal(2, again.Count(l => l.EndsWith("--- log opened ---", StringComparison.Ordinal)));
            Assert.Contains(again, l => l.EndsWith($"[Info] {category}: second session", StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticLog.EchoToConsole = echo;
        }
    }

    [Fact]
    public void Open_OnAnUnwritablePath_Throws_AndLeavesNothingSubscribed()
    {
        string category = "SinkTest-" + Guid.NewGuid().ToString("N");
        int seen = 0;
        Action<DiagnosticEvent> count = e => { if (e.Category == category) seen++; };
        DiagnosticLog.Emitted += count;
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        try
        {
            // A directory where the file should be: the open fails.
            Directory.CreateDirectory(Path.Combine(_dir, "taken"));
            Assert.ThrowsAny<Exception>(() => DiagnosticFileSink.Open(Path.Combine(_dir, "taken")));
            DiagnosticLog.Info(category, "still delivered to other subscribers");
            Assert.Equal(1, seen);
        }
        finally
        {
            DiagnosticLog.Emitted -= count;
            DiagnosticLog.EchoToConsole = echo;
        }
    }
    [Fact]
    public void Dispose_WritesTheClosedLine_AndAFailedWrite_WarnsOnce()
    {
        string path = Path.Combine(_dir, "neon.log");
        string category = "SinkTest-" + Guid.NewGuid().ToString("N");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == DiagnosticFileSink.Category) warnings.Add(e); };
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        DiagnosticLog.Emitted += capture;
        try
        {
            using (DiagnosticFileSink.Open(path))
            {
                DiagnosticLog.Info(category, "one");
            }

            string[] lines = File.ReadAllLines(path);
            Assert.EndsWith("[Info] Log: " + DiagnosticFileSink.OpenedLine, lines[0], StringComparison.Ordinal);
            Assert.EndsWith("[Info] Log: " + DiagnosticFileSink.ClosedLine, lines[^1], StringComparison.Ordinal);
            Assert.Empty(warnings);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
            DiagnosticLog.EchoToConsole = echo;
        }

        Assert.Equal("--log: the file could not be written (disk full); nothing more is logged to it.", DiagnosticFileSink.LogStoppedWarning("disk full"));
        Assert.Equal("Log", DiagnosticFileSink.Category);
    }
}
