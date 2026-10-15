using System.Runtime.Versioning;

namespace NeonSidekick.EventLog;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsEventLogSystem
{
    /// <summary>Every channel's name on <paramref name="target"/> (1,241 on the planning PC, in 3 ms locally).</summary>
    public unsafe IReadOnlyList<string> Channels(EventLogTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        nint session = 0;
        nint channels = 0;
        var registration = default(CancellationTokenRegistration);
        try
        {
            session = OpenSession(target);
            if (session != 0)
            {
                nint held = session;
                registration = cancellationToken.Register(() => EventLogNative.EvtCancel(held));
            }

            channels = EventLogNative.EvtOpenChannelEnum(session, 0);
            if (channels == 0)
            {
                throw Failure("EvtOpenChannelEnum");
            }

            var names = new List<string>(1024);
            var buffer = new char[512];
            while (true)
            {
                bool ok;
                int used;
                fixed (char* pointer = buffer)
                {
                    ok = EventLogNative.EvtNextChannelPath(channels, buffer.Length, pointer, out used);
                }

                if (ok)
                {
                    names.Add(new string(buffer, 0, Math.Max(0, used - 1)));
                    continue;
                }

                int code = EventLogNative.LastError;
                if (code == EventLogNative.ErrorInsufficientBuffer)
                {
                    buffer = new char[Math.Max(used, buffer.Length * 2)];
                    continue;
                }

                if (code == EventLogNative.ErrorNoMoreItems)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                throw new EventLogException(code, "EvtNextChannelPath failed with " + code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return names;
        }
        finally
        {
            registration.Dispose();
            Close(channels);
            Close(session);
        }
    }

    /// <summary>
    /// One log's facts: its configuration (enabled, kind) read first — it opens even where the log will not — then the log itself (records,
    /// size, last write), whose refusal is the call's (5 for Security unelevated). A file's facts come from the file.
    /// </summary>
    public unsafe EventLogInfo Info(EventLogTarget target, string log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);
        cancellationToken.ThrowIfCancellationRequested();
        nint session = 0;
        nint config = 0;
        nint opened = 0;
        var registration = default(CancellationTokenRegistration);
        using var buffer = new NativeBuffer(256);
        try
        {
            session = OpenSession(target);
            if (session != 0)
            {
                nint held = session;
                registration = cancellationToken.Register(() => EventLogNative.EvtCancel(held));
            }

            bool file = target.Kind == EventLogTargetKind.File;
            string path = file ? target.FilePath! : log;
            bool enabled = true;
            var type = EventChannelType.Admin;
            if (!file)
            {
                config = EventLogNative.EvtOpenChannelConfig(session, path, 0);
                if (config != 0)
                {
                    if (ConfigValue(config, EventLogNative.ChannelConfigEnabled, buffer) is bool on)
                    {
                        enabled = on;
                    }

                    if (ConfigValue(config, EventLogNative.ChannelConfigType, buffer) is long kind && kind is >= 0 and <= 3)
                    {
                        type = (EventChannelType)kind;
                    }
                }
                else if (EventLogNative.LastError is 15007 or 15000 or EventLogNative.ErrorFileNotFound)
                {
                    throw Failure("EvtOpenChannelConfig");
                }
            }

            opened = EventLogNative.EvtOpenLog(session, path, file ? EventLogNative.QueryFilePath : EventLogNative.OpenChannelPath);
            if (opened == 0)
            {
                throw Failure("EvtOpenLog");
            }

            long? records = LogValue(opened, EventLogNative.LogNumberOfLogRecords, buffer) switch
            {
                ulong n => (long)n,
                long n => n,
                _ => null,
            };
            long? size = LogValue(opened, EventLogNative.LogFileSize, buffer) switch
            {
                ulong n => (long)n,
                long n => n,
                _ => null,
            };
            var written = LogValue(opened, EventLogNative.LogLastWriteTime, buffer) is DateTimeOffset t ? t : (DateTimeOffset?)null;
            if (written is null && LogValue(opened, EventLogNative.LogLastWriteTime, buffer) is ulong fileTime && fileTime > 0 && fileTime < (ulong)DateTime.MaxValue.ToFileTimeUtc())
            {
                written = new DateTimeOffset(DateTime.FromFileTimeUtc((long)fileTime), TimeSpan.Zero);
            }

            return new EventLogInfo(log, records, size, written, enabled, type);
        }
        finally
        {
            registration.Dispose();
            Close(opened);
            Close(config);
            Close(session);
        }
    }

    /// <summary>One <c>EvtGetLogInfo</c> property decoded, or null.</summary>
    private static unsafe object? LogValue(nint log, int property, NativeBuffer buffer)
    {
        int used;
        if (!EventLogNative.EvtGetLogInfo(log, property, buffer.Size, buffer.Pointer, out used))
        {
            if (EventLogNative.LastError != EventLogNative.ErrorInsufficientBuffer)
            {
                return null;
            }

            buffer.Ensure(used);
            if (!EventLogNative.EvtGetLogInfo(log, property, buffer.Size, buffer.Pointer, out used))
            {
                return null;
            }
        }

        return EventLogVariant.Decode(new ReadOnlySpan<byte>(buffer.Pointer, Math.Max(used, EventLogVariant.Size)), (nint)buffer.Pointer, 0);
    }

    /// <summary>One <c>EvtGetChannelConfigProperty</c> property decoded, or null.</summary>
    private static unsafe object? ConfigValue(nint config, int property, NativeBuffer buffer)
    {
        int used;
        if (!EventLogNative.EvtGetChannelConfigProperty(config, property, 0, buffer.Size, buffer.Pointer, out used))
        {
            if (EventLogNative.LastError != EventLogNative.ErrorInsufficientBuffer)
            {
                return null;
            }

            buffer.Ensure(used);
            if (!EventLogNative.EvtGetChannelConfigProperty(config, property, 0, buffer.Size, buffer.Pointer, out used))
            {
                return null;
            }
        }

        return EventLogVariant.Decode(new ReadOnlySpan<byte>(buffer.Pointer, Math.Max(used, EventLogVariant.Size)), (nint)buffer.Pointer, 0);
    }
}
