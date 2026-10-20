using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.EventLog;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsEventLogSystem
{
    /// <summary>How many events one <c>EvtNext</c> asks for (halved on 1734, doubled back after each batch read).</summary>
    public const int BatchSize = 32;

    /// <summary>The most publishers one read keeps open.</summary>
    public const int MaxPublishers = 64;

    /// <summary>English, the locale a publisher is opened again in when the user's has no messages (15033).</summary>
    private const int EnglishLocale = 1033;

    /// <summary>Opens <paramref name="xpath"/> over <paramref name="log"/> (or the target's file) newest first.</summary>
    public IEventLogReader Query(EventLogTarget target, string log, string xpath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(xpath);
        cancellationToken.ThrowIfCancellationRequested();
        return Reader.Open(target, log, xpath, cancellationToken);
    }

    /// <summary>One query's events, a batch at a time, with the handles, buffers and publishers it needs; disposed in one place.</summary>
    private sealed unsafe class Reader : IEventLogReader
    {
        private readonly EventLogTarget _target;
        private readonly CancellationToken _token;
        private readonly nint[] _events = new nint[BatchSize];
        private readonly NativeBuffer _system = new(1024);
        private readonly NativeBuffer _xml = new(8192);
        private readonly NativeBuffer _user = new(2048);
        private readonly Dictionary<string, (nint Handle, int Refused)> _publishers = new(StringComparer.OrdinalIgnoreCase);
        private char[] _message = new char[2048];
        private nint _session;
        private nint _query;
        private nint _systemContext;
        private nint _userContext;
        private int _count;
        private int _batch = BatchSize;
        private int _skipped;
        private bool _done;
        private CancellationTokenRegistration _cancelQuery;
        private CancellationTokenRegistration _cancelSession;

        private Reader(EventLogTarget target, CancellationToken token)
        {
            _target = target;
            _token = token;
        }

        public static Reader Open(EventLogTarget target, string log, string xpath, CancellationToken token)
        {
            var reader = new Reader(target, token);
            try
            {
                reader._session = OpenSession(target);
                if (reader._session != 0)
                {
                    // A remote call stuck in EvtQuery ends when its own session is cancelled (never the local one: that is every read's).
                    nint session = reader._session;
                    reader._cancelSession = token.Register(() => EventLogNative.EvtCancel(session));
                }

                bool file = target.Kind == EventLogTargetKind.File;
                reader._query = EventLogNative.EvtQuery(reader._session, file ? target.FilePath : log, xpath,
                    (file ? EventLogNative.QueryFilePath : EventLogNative.QueryChannelPath) | EventLogNative.QueryReverseDirection);
                if (reader._query == 0)
                {
                    token.ThrowIfCancellationRequested();
                    throw Failure("EvtQuery");
                }

                nint query = reader._query;
                reader._cancelQuery = token.Register(() => EventLogNative.EvtCancel(query));
                reader._systemContext = EventLogNative.EvtCreateRenderContext(0, 0, EventLogNative.RenderContextSystem);
                reader._userContext = EventLogNative.EvtCreateRenderContext(0, 0, EventLogNative.RenderContextUser);
                if (reader._systemContext == 0 || reader._userContext == 0)
                {
                    throw Failure("EvtCreateRenderContext");
                }

                return reader;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public int Skipped => _skipped;

        public IReadOnlyList<RawEvent>? Next()
        {
            ReleaseBatch();
            if (_done)
            {
                return null;
            }

            _token.ThrowIfCancellationRequested();
            while (true)
            {
                bool ok;
                int returned;
                fixed (nint* events = _events)
                {
                    ok = EventLogNative.EvtNext(_query, _batch, events, -1, 0, out returned);
                }

                if (ok)
                {
                    _count = returned;

                    // Back toward the full batch once the large events are behind (2026-10-20, a review: one large event left the rest of
                    // a remote read at one event a round trip, and its 10 s budget stopped the scan a few hundred events in).
                    _batch = Math.Min(BatchSize, _batch * 2);
                    break;
                }

                int code = EventLogNative.LastError;
                if (code == EventLogNative.ErrorNoMoreItems)
                {
                    _done = true;
                    return null;
                }

                if (code == EventLogNative.RpcInvalidBound && _batch > 1)
                {
                    // A batch too large for one RPC reply (large 4104 events): half as many, down to one.
                    _batch = Math.Max(1, _batch / 2);
                    continue;
                }

                if (code == EventLogNative.RpcInvalidBound && EventLogNative.EvtSeek(_query, 1, 0, 0, EventLogNative.SeekRelativeToCurrent))
                {
                    // One event too large even alone (2026-10-20, a review: it failed the read, and every later page at that place): stepped
                    // over and counted, so the page says so and the search goes on past it. Unproven against a real host's oversized event.
                    _skipped++;
                    continue;
                }

                _token.ThrowIfCancellationRequested();
                throw new EventLogException(code, "EvtNext failed with " + code.ToString(CultureInfo.InvariantCulture));
            }

            var batch = new List<RawEvent>(_count);
            for (int i = 0; i < _count; i++)
            {
                nint handle = _events[i];
                RawEvent? raw = null;
                raw = new RawEvent(() => RenderSystem(handle), () => RenderXml(handle), insert => Format(handle, raw!.System.Provider, insert));
                batch.Add(raw);
            }

            return batch;
        }

        private void ReleaseBatch()
        {
            for (int i = 0; i < _count; i++)
            {
                Close(_events[i]);
                _events[i] = 0;
            }

            _count = 0;
        }

        /// <summary>The system values: the System render context's 18 values decoded.</summary>
        private EventSystemValues RenderSystem(nint handle)
        {
            int count = Render(_systemContext, handle, EventLogNative.RenderEventValues, _system, out int used);
            byte* pointer = _system.Pointer;
            object? Value(int i) => i < count ? EventLogVariant.Decode(new ReadOnlySpan<byte>(pointer, used), (nint)pointer, i) : null;
            static long Number(object? value) => value switch
            {
                long n => n,
                ulong n => unchecked((long)n),
                _ => 0,
            };

            return new EventSystemValues(
                Provider: Value(0) as string ?? "",
                Id: (int)(Number(Value(2)) & 0xFFFF),
                Level: (int)Number(Value(4)),
                Keywords: Value(7) switch { ulong k => k, long k => unchecked((ulong)k), _ => 0 },
                Time: Value(8) as DateTimeOffset?,
                Record: Number(Value(9)),
                Channel: Value(14) as string ?? "",
                Computer: Value(15) as string ?? "",
                UserSid: Value(16) as string,
                Task: (int)Number(Value(5)),
                Opcode: (int)Number(Value(6)),
                ProcessId: Value(12) is null ? null : (int)Number(Value(12)),
                ThreadId: Value(13) is null ? null : (int)Number(Value(13)));
        }

        /// <summary>The event's XML, copied out of the buffer.</summary>
        private string RenderXml(nint handle)
        {
            Render(0, handle, EventLogNative.RenderEventXml, _xml, out int used);
            return used <= 2 ? "" : new string((char*)_xml.Pointer, 0, used / 2 - 1);
        }

        /// <summary><c>EvtRender</c> into <paramref name="buffer"/>, grown on 122: the property count, the bytes used.</summary>
        private static int Render(nint context, nint handle, int flags, NativeBuffer buffer, out int used)
        {
            int count;
            if (!EventLogNative.EvtRender(context, handle, flags, buffer.Size, buffer.Pointer, out used, out count))
            {
                if (EventLogNative.LastError != EventLogNative.ErrorInsufficientBuffer)
                {
                    throw Failure("EvtRender");
                }

                buffer.Ensure(used);
                if (!EventLogNative.EvtRender(context, handle, flags, buffer.Size, buffer.Pointer, out used, out count))
                {
                    throw Failure("EvtRender");
                }
            }

            return count;
        }

        /// <summary>
        /// The event's message: its own, or (<paramref name="insert"/> given) from its User values with every string one rewritten — a
        /// copy of the value array whose rewritten strings point at memory of this call's, freed after. A message that will not format is
        /// the problem, never a throw.
        /// </summary>
        private EventMessage Format(nint handle, string provider, Func<int, string, string>? insert)
        {
            if (provider.Length == 0)
            {
                return new EventMessage(null, EventMessageProblem.Unregistered);
            }

            var (publisher, refused) = Publisher(provider, 0);
            if (publisher == 0)
            {
                return new EventMessage(null, refused is EventLogNative.EvtPublisherMetadataNotFound or EventLogNative.ErrorFileNotFound ? EventMessageProblem.Unregistered : EventMessageProblem.Failed);
            }

            byte* values = null;
            var strings = new List<nint>();
            int valueCount = 0;
            try
            {
                if (insert is not null)
                {
                    valueCount = Render(_userContext, handle, EventLogNative.RenderEventValues, _user, out int used);
                    var span = new ReadOnlySpan<byte>(_user.Pointer, used);
                    values = (byte*)NativeMemory.Alloc((nuint)Math.Max(EventLogVariant.Size, valueCount * EventLogVariant.Size));
                    Buffer.MemoryCopy(_user.Pointer, values, valueCount * EventLogVariant.Size, valueCount * EventLogVariant.Size);
                    foreach (var (i, rewritten) in EventLogVariant.Rewrites(span, (nint)_user.Pointer, valueCount, insert))
                    {
                        // Any value the redaction changed — an ANSI string, an array, a number under a secret's name — goes in as a plain
                        // string (2026-10-20, a review). The formatter reads a string by its count (the spike: a longer value was cut to
                        // the old length), so the pointer, the count and the type are all set.
                        nint text = Marshal.StringToHGlobalUni(rewritten);
                        strings.Add(text);
                        byte* variant = values + i * EventLogVariant.Size;
                        *(nint*)variant = text;
                        *(int*)(variant + 8) = rewritten.Length;
                        *(int*)(variant + 12) = EventLogVariant.TypeString;
                    }
                }

                var message = FormatWith(publisher, handle, valueCount, values);
                if (message.Problem == EventMessageProblem.Failed && EventLogNative.LastError == EventLogNative.EvtMessageLocaleNotFound)
                {
                    // No messages in the user's language: the publisher once more in English.
                    publisher = Reopen(provider, EnglishLocale);
                    message = publisher == 0 ? message : FormatWith(publisher, handle, valueCount, values);
                }

                return message;
            }
            catch (EventLogException)
            {
                return new EventMessage(null, EventMessageProblem.Failed);
            }
            finally
            {
                foreach (nint text in strings)
                {
                    Marshal.FreeHGlobal(text);
                }

                if (values != null)
                {
                    NativeMemory.Free(values);
                }
            }
        }

        private EventMessage FormatWith(nint publisher, nint handle, int valueCount, byte* values)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool ok;
                int used;
                fixed (char* buffer = _message)
                {
                    ok = EventLogNative.EvtFormatMessage(publisher, handle, 0, valueCount, values, EventLogNative.FormatMessageEvent, _message.Length, buffer, out used);
                }

                if (ok)
                {
                    return new EventMessage(new string(_message, 0, Math.Max(0, used - 1)));
                }

                int code = EventLogNative.LastError;
                switch (code)
                {
                    case EventLogNative.ErrorInsufficientBuffer:
                        _message = new char[Math.Max(used, _message.Length * 2)];
                        continue;
                    case EventLogNative.EvtUnresolvedValueInsert or EventLogNative.EvtUnresolvedParameterInsert or EventLogNative.EvtMaxInsertsReached:
                        // A usable partial message: what was formatted is in the buffer.
                        int length = Array.IndexOf(_message, '\0');
                        return new EventMessage(new string(_message, 0, length < 0 ? Math.Min(_message.Length, Math.Max(0, used - 1)) : length));
                    case EventLogNative.EvtMessageNotFound or EventLogNative.EvtMessageIdNotFound:
                        return new EventMessage(null, EventMessageProblem.NoMessage);
                    default:
                        Marshal.SetLastPInvokeError(code);
                        return new EventMessage(null, EventMessageProblem.Failed);
                }
            }

            return new EventMessage(null, EventMessageProblem.Failed);
        }

        /// <summary>
        /// The provider's metadata, opened once per read (<see cref="MaxPublishers"/> at most, failures kept too). A file's is looked for as
        /// registered here first, then in the file's own archived metadata.
        /// </summary>
        private (nint Handle, int Refused) Publisher(string provider, int locale)
        {
            if (_publishers.TryGetValue(provider, out var known))
            {
                return known;
            }

            if (_publishers.Count >= MaxPublishers)
            {
                foreach (var open in _publishers.Values)
                {
                    Close(open.Handle);
                }

                _publishers.Clear();
            }

            nint publisher = EventLogNative.EvtOpenPublisherMetadata(_session, provider, null, locale, 0);
            int refused = publisher == 0 ? EventLogNative.LastError : 0;
            if (publisher == 0 && _target.Kind == EventLogTargetKind.File)
            {
                publisher = EventLogNative.EvtOpenPublisherMetadata(_session, provider, _target.FilePath, locale, 0);
                refused = publisher == 0 ? EventLogNative.LastError : 0;
            }

            var entry = (publisher, refused);
            _publishers[provider] = entry;
            return entry;
        }

        private nint Reopen(string provider, int locale)
        {
            if (_publishers.Remove(provider, out var old))
            {
                Close(old.Handle);
            }

            return Publisher(provider, locale).Handle;
        }

        public void Dispose()
        {
            // The registrations first: nothing may cancel a handle being closed.
            _cancelQuery.Dispose();
            _cancelSession.Dispose();
            ReleaseBatch();
            Close(_systemContext);
            Close(_userContext);
            Close(_query);
            foreach (var publisher in _publishers.Values)
            {
                Close(publisher.Handle);
            }

            _publishers.Clear();
            Close(_session);
            _systemContext = _userContext = _query = _session = 0;
            _system.Dispose();
            _xml.Dispose();
            _user.Dispose();
        }
    }
}
