using NeonSidekick.App;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class MessageQueueTests
{
    /// <summary>A queued line: its label and the line the live row handed back.</summary>
    private static QueuedMessage Line(string text) =>
        new(text, new SubmittedLine(text, text, [], text));

    private static IEnumerable<string> Labels(MessageQueue queue) => queue.Snapshot().Select(m => m.Label);

    [Fact]
    public void Enqueue_TryDequeue_IsFifo_AndCountFollows()
    {
        var queue = new MessageQueue();
        Assert.Equal(0, queue.Count);
        Assert.False(queue.TryDequeue(out _));

        var one = Line("one");
        queue.Enqueue(one);
        queue.Enqueue(Line("two"));
        Assert.Equal(2, queue.Count);
        Assert.Equal(new[] { "one", "two" }, Labels(queue));

        Assert.True(queue.TryDequeue(out var first));
        Assert.Same(one, first);
        Assert.Equal("one", first.Line.Text);   // the line rides along
        Assert.True(queue.TryDequeue(out var second));
        Assert.Equal("two", second.Label);
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Remove_TakesTheRow_OnlyWhileItStillReadsTheLabel()
    {
        var queue = new MessageQueue();
        queue.Enqueue(Line("one"));
        queue.Enqueue(Line("two"));
        queue.Enqueue(Line("three"));

        Assert.False(queue.Remove(1, "one"));          // a stale snapshot: the row moved on
        Assert.False(queue.Remove(3, "three"));        // past the end
        Assert.False(queue.Remove(-1, "one"));
        Assert.True(queue.Remove(1, "two"));
        Assert.Equal(new[] { "one", "three" }, Labels(queue));
    }

    [Fact]
    public void Clear_DropsEverything_ResetsTheHold_AndReportsTheCount()
    {
        var queue = new MessageQueue();
        Assert.Equal(0, queue.Clear());

        queue.Enqueue(Line("one"));
        queue.Enqueue(Line("two"));
        queue.Held = true;
        Assert.True(queue.Held);

        Assert.Equal(2, queue.Clear());
        Assert.Equal(0, queue.Count);
        Assert.False(queue.Held);
    }

    [Fact]
    public void Snapshot_IsACopy()
    {
        var queue = new MessageQueue();
        queue.Enqueue(Line("one"));
        var snapshot = queue.Snapshot();
        queue.Enqueue(Line("two"));
        Assert.Single(snapshot);
        Assert.Equal(2, queue.Count);
    }
    [Fact]
    public void EnqueueDequeueRemoveClear_EachLogAtDebug()
    {
        var queue = new MessageQueue();
        var lines = new List<NeonSidekick.Diagnostics.DiagnosticEvent>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == MessageQueue.Category && (e.Message.Contains("queue", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("message", StringComparison.Ordinal))) lines.Add(e); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            queue.Enqueue(Line("one"));
            queue.Enqueue(Line("two"));
            queue.Enqueue(Line("three"));
            queue.TryDequeue(out _);
            queue.Remove(1, "three");
            queue.Clear();
            queue.Clear();   // empty: nothing said
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(
            ["Queued message 1: \"one\"", "Queued message 2: \"two\"", "Queued message 3: \"three\"", "Dequeued message: \"one\" (2 waiting)", "Removed queued message: \"three\"", "Queue dropped (1 message)"],
            lines.Select(e => e.Message));
        Assert.All(lines, e => Assert.Equal(NeonSidekick.Diagnostics.DiagnosticLevel.Debug, e.Level));
        Assert.Equal("Queue dropped (2 messages)", MessageQueue.DroppedLogLine(2));
    }
}
