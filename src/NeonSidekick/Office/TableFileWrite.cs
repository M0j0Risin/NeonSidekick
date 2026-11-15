using NeonSidekick.Diagnostics;
using NeonSidekick.Files;

namespace NeonSidekick.Office;

/// <summary>
/// The end of a table file's write that <see cref="XlsxTarget"/> and <see cref="TextTableTarget"/> share (2026-11-15, the database-tools
/// review's #129: the two had drifted copies): the late checks, then the pending file put in place; and each part let go in its own try.
/// </summary>
internal static class TableFileWrite
{
    /// <summary>
    /// <paramref name="stillOpen"/> judged again (a refusal discards the file: <see cref="FileText.SaveClosedLate"/>, or
    /// <see cref="FileText.SaveMovedLate"/> as it is), then <paramref name="changed"/> (a workbook saved elsewhere meanwhile), then the
    /// pending file committed: the refusal, or the result.
    /// </summary>
    public static (string? Refusal, WriteResult? Result) Put(PendingWrite pending, Func<string?>? stillOpen, Func<string?>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (stillOpen?.Invoke() is { } closed)
        {
            pending.Dispose();
            return (closed == FileText.SaveMovedLate ? closed : FileText.SaveClosedLate(closed), null);
        }

        if (changed?.Invoke() is { } moved)
        {
            pending.Dispose();
            return (moved, null);
        }

        var result = pending.Commit();
        return result.Outcome != FileOutcome.Ok ? (OfficeText.WriteFailed(result), null) : (null, result);
    }

    /// <summary>
    /// <paramref name="part"/> let go (2026-11-02, review P13-2's rule): a throw from it (a writer's flush on a full disk) is logged, naming
    /// the <paramref name="format"/> (#133), and swallowed, so the parts after it are still let go and the pending <c>.neon-tmp</c> never held open.
    /// </summary>
    public static void Release(IDisposable? part, string format, string what, string relative)
    {
        try
        {
            part?.Dispose();
        }
        catch (Exception ex) when (XlsxTarget.Failure(ex) || ex is InvalidOperationException or ObjectDisposedException)
        {
            DiagnosticLog.Warn(OfficeText.Category, OfficeText.ReleaseFailedLogLine(format, what, relative, ex.GetType().Name, LogText.Excerpt(ex.Message)));
        }
    }
}
