using NeonSidekick.Diagnostics;

namespace NeonSidekick.Tests;

/// <summary>The run's diagnostic lines in memory (2026-10-02, <c>/log</c>'s window): the ring, the numbering a reader asks by, the event.</summary>
public class DiagnosticBufferTests
{
    private static DiagnosticEvent Event(string message, DiagnosticLevel level = DiagnosticLevel.Info) =>
        new(new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc), level, "Test", message, null);

    [Fact]
    public void Add_KeepsTheFileLine_AndTheLevel_NumberedFromZero()
    {
        var buffer = new DiagnosticBuffer(4);
        var evt = Event("hello", DiagnosticLevel.Warning);
        buffer.Add(evt);
        buffer.Add(Event("again"));

        var lines = new List<LogLine>();
        long first = buffer.CopySince(0, lines);

        Assert.Equal(0, first);
        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.NextSeq);
        Assert.Equal(new LogLine(0, DiagnosticLevel.Warning, DiagnosticFileSink.Format(evt)), lines[0]);
        Assert.Equal(1, lines[1].Seq);
    }

    [Fact]
    public void ARing_DropsTheOldest_AndCopySince_SaysWhereItNowStarts()
    {
        var buffer = new DiagnosticBuffer(3);
        for (int i = 0; i < 5; i++)
        {
            buffer.Add(Event("line " + i));
        }

        var lines = new List<LogLine>();
        long first = buffer.CopySince(0, lines);

        Assert.Equal(3, buffer.Capacity);
        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, first);   // lines 0 and 1 have gone from the front
        Assert.Equal([2L, 3L, 4L], lines.Select(l => l.Seq));
        Assert.EndsWith("line 4", lines[^1].Text);

        lines.Clear();
        Assert.Equal(2, buffer.CopySince(4, lines));   // only what the reader lacks
        Assert.Equal([4L], lines.Select(l => l.Seq));

        lines.Clear();
        buffer.CopySince(5, lines);
        Assert.Empty(lines);
    }

    [Fact]
    public void Appended_IsRaisedPerLine_AndAThrowingReaderNeverReachesTheCaller()
    {
        var buffer = new DiagnosticBuffer();
        int raised = 0;
        buffer.Appended += () => raised++;
        buffer.Appended += () => throw new InvalidOperationException("bad reader");

        buffer.Add(Event("one"));
        buffer.Add(Event("two"));

        Assert.Equal(2, raised);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Attach_TakesEveryLevel_FromDiagnosticLog_UntilDisposed()
    {
        bool echo = DiagnosticLog.EchoToConsole;
        DiagnosticLog.EchoToConsole = false;
        try
        {
            var buffer = DiagnosticBuffer.Attach(100);
            DiagnosticLog.Trace("BufferTest", "a trace line");
            DiagnosticLog.Error("BufferTest", "an error line");
            buffer.Dispose();
            DiagnosticLog.Info("BufferTest", "after dispose");

            var lines = new List<LogLine>();
            buffer.CopySince(0, lines);
            Assert.Contains(lines, l => l.Level == DiagnosticLevel.Trace && l.Text.EndsWith("[Trace] BufferTest: a trace line", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Level == DiagnosticLevel.Error && l.Text.EndsWith("BufferTest: an error line", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, l => l.Text.Contains("after dispose", StringComparison.Ordinal));
            buffer.Dispose();   // twice is harmless
        }
        finally
        {
            DiagnosticLog.EchoToConsole = echo;
        }
    }

    [Fact]
    public void ACapacityBelowOne_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticBuffer(0));
        Assert.Throws<ArgumentNullException>(() => new DiagnosticBuffer().CopySince(0, null!));
    }
}
