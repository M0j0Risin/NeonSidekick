namespace NeonSidekick.EventLog;

/// <summary>
/// One entry of a Mac's unified log (2026-10-18, the Mac event-log plan): an <c>OSLogEntryLog</c> copied out of its autorelease pool,
/// so a batch never goes stale. <see cref="Level"/> is OSLog's own number: 1 debug, 2 info, 3 default (OSLog's "notice"), 4 error,
/// 5 fault. <see cref="Message"/> is capped at <see cref="MacLogLimits.MessageChars"/>; macOS has already put <c>&lt;private&gt;</c>
/// where an app did not mark a value public.
/// </summary>
public sealed record MacLogEntry(
    DateTimeOffset Time,
    int Level,
    string Process,
    int Pid,
    ulong Thread,
    string Subsystem,
    string Category,
    string Sender,
    string Message);

/// <summary>
/// What a read of the unified log asks for (2026-10-18). <see cref="Since"/> is required: the native read ends at the first entry
/// older than it, the clause <see cref="MacLogPredicate"/> always appends, since a predicate with no match otherwise makes
/// <c>nextObject</c> scan the store back with no end (Phase 0: <c>threadIdentifier == 1</c> ran the full 300 s). <see cref="Levels"/>
/// holds OSLog's type words (<c>debug info default error fault</c>); empty means every level. The rest narrow it; null or empty is no term.
/// </summary>
public sealed record MacLogFilter(
    DateTimeOffset Since,
    IReadOnlyList<string> Levels,
    DateTimeOffset? Until = null,
    string? Process = null,
    int? Pid = null,
    string? Subsystem = null,
    string? Category = null,
    string? Text = null,
    IReadOnlyList<string>? ExcludeProcesses = null);

/// <summary>Where a read goes: this Mac's own store, or a <c>.logarchive</c> folder by its full path (2026-10-18).</summary>
public sealed record UnifiedLogTarget(string? ArchivePath)
{
    /// <summary>This Mac's own log store.</summary>
    public static readonly UnifiedLogTarget Local = new((string?)null);

    /// <summary>A <c>.logarchive</c> folder.</summary>
    public static UnifiedLogTarget Archive(string fullPath) => new(fullPath ?? throw new ArgumentNullException(nameof(fullPath)));

    public bool IsLocal => ArchivePath is null;
}

/// <summary>
/// An open read, newest first, in batches. Not thread-safe: made, read and disposed on one thread (the access layer's work delegate).
/// <see cref="Next"/> returns null once the read reached <see cref="MacLogFilter.Since"/> or the store's end; it honors the
/// cancellation the read was opened with between entries.
/// </summary>
public interface IUnifiedLogReader : IDisposable
{
    IReadOnlyList<MacLogEntry>? Next();
}

/// <summary>
/// A Mac's unified log, read in-process (2026-10-18, the Mac event-log plan: OSLogStore over the Objective-C runtime, no
/// <c>/usr/bin/log</c> and so no process-start site). <see cref="MacUnifiedLog"/> in the app, <c>FakeUnifiedLog</c> in tests. Every
/// call may throw <see cref="UnifiedLogException"/> with OSLog's own domain and code.
/// </summary>
public interface IUnifiedLog
{
    IUnifiedLogReader Open(UnifiedLogTarget target, MacLogFilter filter, CancellationToken cancellationToken);

    /// <summary>Opens the store and reads nothing: throws as <see cref="Open"/> would when it cannot be read.</summary>
    void Probe(UnifiedLogTarget target);
}

/// <summary>A refusal from OSLog (2026-10-18): its <c>NSError</c>'s domain, code and description, for <see cref="MacLogErrors"/>.</summary>
public sealed class UnifiedLogException(string domain, long code, string description)
    : Exception($"{domain} {code}: {description}")
{
    public string Domain { get; } = domain;

    public long Code { get; } = code;

    public string Description { get; } = description;
}

/// <summary>The unified log's fixed limits (2026-10-18).</summary>
public static class MacLogLimits
{
    /// <summary>A message is cut here when it is copied out (an entry can carry a whole plist).</summary>
    public const int MessageChars = 8_192;

    /// <summary>Entries per <see cref="IUnifiedLogReader.Next"/> batch.</summary>
    public const int Batch = 64;
}
