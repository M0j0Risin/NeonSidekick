using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Platform;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.EventLog;

/// <summary>
/// A Mac's unified log read in-process (2026-10-18, the Mac event-log plan): <c>OSLogStore</c> over the Objective-C runtime, typed
/// <c>objc_msgSend</c> imports as <see cref="Files.MacTrash"/> does, no <c>/usr/bin/log</c> and so no process-start site. The spike
/// opened <c>+[OSLogStore localStoreAndReturnError:]</c> from an unsigned, ad-hoc-signed NativeAOT exe with no sudo or entitlement on an
/// admin account (a standard user may be refused: <see cref="MacLogErrors"/> says so); a <c>.logarchive</c> opens through
/// <c>storeWithURL:error:</c>. Reads run in reverse, newest first, under <see cref="MacLogPredicate"/>'s predicate, whose last clause
/// ends the read at the first entry older than the filter's <c>Since</c>; every open and read is inside <see cref="MacStderr.Quiet"/>
/// (OSLog writes "Wall Clock adjustment detected…" to stderr on each). Each entry is copied out inside an autorelease pool of its own;
/// the store and the enumerator are retained by the reader and released when it is disposed. A reader stays on the thread that made
/// it. Excluded from coverage with the other native layers; proven by <c>MacUnifiedLogTests</c> (a live <c>[MacFact]</c>) and the
/// smoke's <c>eventlog:oslog</c>.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class MacUnifiedLog : IUnifiedLog
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    public const string OSLogPath = "/System/Library/Frameworks/OSLog.framework/OSLog";

    /// <summary><c>OSLogEnumeratorReverse</c>: newest first.</summary>
    private const ulong Reverse = 1;

    /// <summary>OSLog's framework loaded (macOS 10.15+); false where it is not there.</summary>
    public static bool Load() => NativeLibrary.TryLoad(OSLogPath, out _);

    public IUnifiedLogReader Open(UnifiedLogTarget target, MacLogFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(filter);
        var (format, arguments) = MacLogPredicate.Build(filter);
        Require();
        using var quiet = MacStderr.Quiet();
        nint pool = objc_autoreleasePoolPush();
        try
        {
            nint store = OpenStore(target);
            nint predicate = Predicate(format, arguments);
            // A forward read starts at Since; a reverse one at its newest moment, never at Since (2026-10-20, the live check: put at a since two
            // days back the read began at the end of an older stretch of the store, put at 1970 it gave nothing; MacLogFilter's note).
            nint position = Send(store, Sel("positionWithDate:"), Date(filter.Forward ? filter.Since : filter.Until ?? DateTimeOffset.UtcNow));
            nint error = 0;
            nint enumerator = SendEnumerator(store, Sel("entriesEnumeratorWithOptions:position:predicate:error:"), filter.Forward ? 0 : Reverse, position, predicate, &error);
            if (enumerator == 0)
            {
                throw Failure(error, "the log could not be read");
            }

            return new Reader(Send(store, Sel("retain")), Send(enumerator, Sel("retain")), filter, cancellationToken);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    public void Probe(UnifiedLogTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Require();
        using var quiet = MacStderr.Quiet();
        nint pool = objc_autoreleasePoolPush();
        try
        {
            _ = OpenStore(target);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private static void Require()
    {
        if (!Load())
        {
            throw new UnifiedLogException("NeonSidekick", 1, "OSLog.framework is not on this Mac");
        }
    }

    /// <summary>The store, autoreleased (the caller's pool): this Mac's, or an archive's. Throws with OSLog's error.</summary>
    private static nint OpenStore(UnifiedLogTarget target)
    {
        nint error = 0;
        nint store = target.ArchivePath is { } path
            ? Send(Class("OSLogStore"), Sel("storeWithURL:error:"), Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path)), (nint)(&error))
            : Send(Class("OSLogStore"), Sel("localStoreAndReturnError:"), (nint)(&error));
        return store != 0 ? store : throw Failure(error, "the log store could not be opened");
    }

    /// <summary>The predicate: the constant format and its arguments as Foundation objects in an NSArray (never spliced into the format).</summary>
    private static nint Predicate(string format, IReadOnlyList<MacPredicateArgument> arguments)
    {
        var objects = new nint[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            objects[i] = Argument(arguments[i]);
            if (objects[i] == 0)
            {
                throw new UnifiedLogException("NeonSidekick", 2, "a filter value could not be passed to the log");   // an NSArray with nil in it would throw
            }
        }

        nint array;
        fixed (nint* items = objects)
        {
            array = SendArray(Class("NSArray"), Sel("arrayWithObjects:count:"), items, (nuint)objects.Length);
        }

        nint predicate = Send(Class("NSPredicate"), Sel("predicateWithFormat:argumentArray:"), NSString(format), array);
        return predicate != 0 ? predicate : throw new UnifiedLogException("NeonSidekick", 3, "the filter could not be made");
    }

    private static nint Argument(MacPredicateArgument argument) => argument.Kind switch
    {
        MacPredicateKind.String => NSString(argument.Text ?? ""),
        MacPredicateKind.Number => SendLongArg(Class("NSNumber"), Sel("numberWithLongLong:"), argument.Number),
        MacPredicateKind.Date => Date(argument.Date),
        MacPredicateKind.StringList => StringArray(argument.List ?? []),
        _ => 0,
    };

    private static nint StringArray(IReadOnlyList<string> values)
    {
        var objects = values.Select(NSString).ToArray();
        fixed (nint* items = objects)
        {
            return SendArray(Class("NSArray"), Sel("arrayWithObjects:count:"), items, (nuint)objects.Length);
        }
    }

    private static nint Date(DateTimeOffset time) =>
        SendDoubleArg(Class("NSDate"), Sel("dateWithTimeIntervalSince1970:"), (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (double)TimeSpan.TicksPerSecond);

    private static DateTimeOffset TimeOf(nint date) =>
        DateTimeOffset.UnixEpoch.AddTicks((long)(SendDouble(date, Selectors.TimeIntervalSince1970) * TimeSpan.TicksPerSecond));

    private static UnifiedLogException Failure(nint error, string fallback) =>
        error == 0
            ? new UnifiedLogException("NeonSidekick", 0, fallback)
            : new UnifiedLogException(FromNSString(Send(error, Sel("domain"))) ?? "", SendLong(error, Sel("code")), FromNSString(Send(error, Sel("localizedDescription"))) ?? fallback);

    /// <summary>
    /// An open read. A batch is handed back when it is full or after <see cref="MacLogLimits.BatchTime"/> (2026-10-20, the code review),
    /// even empty, so the policy above checks its scan budget and the caller's token between OSLog's calls rather than after 64 matches
    /// that may take minutes to come; one <c>nextObject</c> is the finest grain OSLog allows (it has no cancel, and a reverse read always
    /// starts at the newest entry, so cutting the window into slices would rescan from the top for each).
    /// </summary>
    private sealed class Reader(nint store, nint enumerator, MacLogFilter filter, CancellationToken cancellationToken) : IUnifiedLogReader
    {
        private nint _store = store;
        private nint _enumerator = enumerator;
        private bool _done;

        public IReadOnlyList<MacLogEntry>? Next()
        {
            if (_done || _enumerator == 0)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<MacLogEntry>(MacLogLimits.Batch);
            using var quiet = MacStderr.Quiet();
            nint logClass = Selectors.LogEntryClass;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (batch.Count < MacLogLimits.Batch && !cancellationToken.IsCancellationRequested
                && System.Diagnostics.Stopwatch.GetElapsedTime(started) < MacLogLimits.BatchTime)
            {
                nint pool = objc_autoreleasePoolPush();
                try
                {
                    nint entry = Send(_enumerator, Selectors.NextObject);
                    if (entry == 0)
                    {
                        _done = true;
                        break;
                    }

                    var time = TimeOf(Send(entry, Selectors.Date));
                    if (filter.Forward ? time > filter.Until : time < filter.Since)
                    {
                        _done = true;   // the time limit's own entry: the read is over
                        break;
                    }

                    if (SendBoolNint(entry, Selectors.IsKindOfClass, logClass) == 0)
                    {
                        continue;   // a signpost or an activity, not a log entry
                    }

                    var (copy, whole) = Copy(entry, time);
                    if (MacLogMatch.Matches(copy, filter, whole))   // the text checked on the whole message, as OSLog's predicate did
                    {
                        batch.Add(copy);
                    }
                }
                finally
                {
                    objc_autoreleasePoolPop(pool);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return batch.Count == 0 && _done ? null : batch;
        }

        public void Dispose()
        {
            nint enumerator = Interlocked.Exchange(ref _enumerator, 0);
            nint store = Interlocked.Exchange(ref _store, 0);
            using var quiet = MacStderr.Quiet();   // the release tears the read down, which OSLog may also report on stderr
            nint pool = objc_autoreleasePoolPush();   // a pool thread has none: what the teardown autoreleases is drained here (2026-10-20, the code review)
            try
            {
                if (enumerator != 0)
                {
                    SendVoid(enumerator, Sel("release"));
                }

                if (store != 0)
                {
                    SendVoid(store, Sel("release"));
                }
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        /// <summary>The entry copied out, its message cut at <see cref="MacLogLimits.MessageChars"/>, and the whole message for the text check.</summary>
        private static (MacLogEntry Entry, string Whole) Copy(nint entry, DateTimeOffset time)
        {
            string whole = FromNSString(Send(entry, Selectors.ComposedMessage)) ?? "";
            string message = whole.Length > MacLogLimits.MessageChars ? whole[..MacLogLimits.MessageChars] : whole;
            int level = (int)SendLong(entry, Selectors.Level);
            string subsystem = FromNSString(Send(entry, Selectors.Subsystem)) ?? "";
            string category = FromNSString(Send(entry, Selectors.Category)) ?? "";
            return (new MacLogEntry(
                time,
                level,
                FromNSString(Send(entry, Selectors.Process)) ?? "",
                SendInt(entry, Selectors.ProcessIdentifier),
                SendULong(entry, Selectors.ThreadIdentifier),
                subsystem,
                category,
                FromNSString(Send(entry, Selectors.Sender)) ?? "",
                message,
                MacLogHandle.MarkOf(level, subsystem, category, whole)), whole);   // X5: the mark over the whole message
        }
    }

    /// <summary>
    /// The selectors and the class read per entry, looked up once (2026-10-23, the admin review's X17: about eleven lookups an entry, each a lock
    /// and a dictionary). Initialized on first use, so only a Mac's read ever runs them.
    /// </summary>
    private static class Selectors
    {
        public static readonly nint NextObject = Sel("nextObject");
        public static readonly nint Date = Sel("date");
        public static readonly nint IsKindOfClass = Sel("isKindOfClass:");
        public static readonly nint ComposedMessage = Sel("composedMessage");
        public static readonly nint Level = Sel("level");
        public static readonly nint Process = Sel("process");
        public static readonly nint ProcessIdentifier = Sel("processIdentifier");
        public static readonly nint ThreadIdentifier = Sel("threadIdentifier");
        public static readonly nint Subsystem = Sel("subsystem");
        public static readonly nint Category = Sel("category");
        public static readonly nint Sender = Sel("sender");
        public static readonly nint TimeIntervalSince1970 = Sel("timeIntervalSince1970");
        public static readonly nint LogEntryClass = Class("OSLogEntryLog");
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendEnumerator(nint receiver, nint selector, ulong options, nint position, nint predicate, nint* error);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendArray(nint receiver, nint selector, nint* objects, nuint count);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendLongArg(nint receiver, nint selector, long value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendDoubleArg(nint receiver, nint selector, double value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolNint(nint receiver, nint selector, nint a);
}
