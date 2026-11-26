using NeonSidekick.Diagnostics;

namespace NeonSidekick.Tests;

public class CrashReportTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 9, 15, 21, 43, 57, 309, DateTimeKind.Utc);

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
            // The temp directory is not worth failing a test over.
        }
    }

    // A thrown exception carries a stack; a constructed one does not.
    private static Exception Thrown()
    {
        try
        {
            throw new ArgumentOutOfRangeException("length", "Index was out of range.");
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void Write_CreatesTheDirectoryAndTheFile_WithTheHeaderTypeMessageAndStack()
    {
        var ex = Thrown();

        string? path = CrashReport.Write(Path.Combine(_dir, "nested"), ex, Stamp);

        Assert.Equal(Path.Combine(_dir, "nested", CrashReport.FileName), path);
        string text = File.ReadAllText(path!);
        Assert.StartsWith("--- 2026-09-15 21:43:57.309 crash ---", text, StringComparison.Ordinal);
        Assert.Contains("System.ArgumentOutOfRangeException", text, StringComparison.Ordinal);
        Assert.Contains("Index was out of range.", text, StringComparison.Ordinal);
        Assert.Contains("   at ", text, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine + Environment.NewLine, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Appends()
    {
        CrashReport.Write(_dir, new InvalidOperationException("first"), Stamp);
        CrashReport.Write(_dir, new InvalidOperationException("second"), Stamp.AddMinutes(1));

        string text = File.ReadAllText(Path.Combine(_dir, CrashReport.FileName));
        Assert.Contains("--- 2026-09-15 21:43:57.309 crash ---", text, StringComparison.Ordinal);
        Assert.Contains("--- 2026-09-15 21:44:57.309 crash ---", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_ReturnsNull_WhenTheFileCannotBeWritten()
    {
        // A file where the directory should be: CreateDirectory throws, and the writer must not.
        Directory.CreateDirectory(_dir);
        string blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "not a directory");

        Assert.Null(CrashReport.Write(blocked, new InvalidOperationException("boom"), Stamp));
    }

    [Fact]
    public void Entry_Hidden_KeepsEveryInnerOfAnAggregate_AndNoMessage()
    {
        // 2026-11-26, the review's #33: the second inner's type and stack were dropped.
        Exception first, second;
        try
        {
            throw new InvalidOperationException("ZebraFirst");
        }
        catch (InvalidOperationException ex)
        {
            first = ex;
        }

        try
        {
            throw new FormatException("ZebraSecond");
        }
        catch (FormatException ex)
        {
            second = ex;
        }

        string hidden = CrashReport.Entry(new AggregateException("ZebraOuter", first, second), Stamp, hide: true);
        Assert.Contains(nameof(InvalidOperationException), hidden, StringComparison.Ordinal);
        Assert.Contains(nameof(FormatException), hidden, StringComparison.Ordinal);
        Assert.Contains(" ---> (Inner Exception #1) ", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("Zebra", hidden, StringComparison.Ordinal);
    }

    /// <summary>After the exit ended a private conversation under hide, a crash entry still keeps no message (2026-11-26, the review's #34/#72).</summary>
    [Fact]
    public void Write_AfterAPrivateExit_KeepsNoMessage()
    {
        try
        {
            PrivateMode.HidAtExit = true;
            Assert.True(PrivateMode.HideCrash);
            string path = CrashReport.Write(_dir, new InvalidOperationException("ZebraLate"), Stamp)!;
            Assert.DoesNotContain("ZebraLate", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            PrivateMode.HidAtExit = false;
        }
    }

    [Fact]
    public void Entry_IsInvariant_AndPinned()
    {
        var ex = new InvalidOperationException("boom");

        Assert.Equal("--- 2026-09-15 21:43:57.309 crash ---" + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine, CrashReport.Entry(ex, Stamp));
    }

    [Fact]
    public void Notice_AndLogLine_ArePinned()
    {
        var ex = new ArgumentOutOfRangeException("length", "Index was out of range.");

        Assert.Equal($"NeonSidekick crashed: ArgumentOutOfRangeException: {ex.Message}. Details in C:\\x\\crash.log.", CrashReport.Notice("C:\\x\\crash.log", ex));
        Assert.Equal($"NeonSidekick crashed: ArgumentOutOfRangeException: {ex.Message}. Details could not be written.", CrashReport.Notice(null, ex));
        Assert.Equal("Crashed; details in C:\\x\\crash.log", CrashReport.LogLine("C:\\x\\crash.log"));
        Assert.Equal("Crashed; the crash report could not be written.", CrashReport.LogLine(null));
    }
}
