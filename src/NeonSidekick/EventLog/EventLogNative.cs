using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.EventLog;

/// <summary>
/// The <c>wevtapi.dll</c> imports behind <see cref="WindowsEventLogSystem"/> (2026-10-15, the event-log plan). One of the Windows-only
/// layers, <c>Screen/ScreenNative</c>'s shape: source-generated <see cref="LibraryImportAttribute"/>, every handle an <see cref="nint"/>,
/// buffers as raw pointers the caller owns, <c>EVT_RPC_LOGIN</c> a struct of <c>char*</c>s filled with <c>fixed</c>, as
/// <c>WindowsCredentials.LogonNetOnly</c> fills <c>LogonUserW</c>'s (a UPN's null domain is a null pointer). A system library:
/// nothing joins <c>SmokeChecks.RequiredNativeLibraries</c>; the smoke's <c>eventlog:wevtapi</c>, <c>eventlog:rpc-login</c> and
/// <c>eventlog:evtx</c> prove it on the published exe. <see cref="EvtQuery"/>'s flags never carry <c>EvtQueryTolerateQueryErrors</c>,
/// which silently drops a term the log rejects.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class EventLogNative
{
    private const string Library = "wevtapi.dll";

    // EVT_QUERY_FLAGS.
    public const int QueryChannelPath = 0x1;
    public const int QueryFilePath = 0x2;
    public const int QueryReverseDirection = 0x200;

    // EVT_SEEK_FLAGS.
    public const int SeekRelativeToCurrent = 3;

    // EVT_RENDER_CONTEXT_FLAGS and EVT_RENDER_FLAGS.
    public const int RenderContextSystem = 1;
    public const int RenderContextUser = 2;
    public const int RenderEventValues = 0;
    public const int RenderEventXml = 1;

    // EVT_FORMAT_MESSAGE_FLAGS.
    public const int FormatMessageEvent = 1;

    // EVT_OPEN_LOG_FLAGS.
    public const int OpenChannelPath = 1;

    // EVT_LOG_PROPERTY_ID.
    public const int LogLastWriteTime = 2;
    public const int LogFileSize = 3;
    public const int LogNumberOfLogRecords = 5;

    // EVT_CHANNEL_CONFIG_PROPERTY_ID.
    public const int ChannelConfigEnabled = 0;
    public const int ChannelConfigType = 2;

    // EVT_LOGIN_CLASS and EVT_RPC_LOGIN_FLAGS.
    public const int RpcLogin = 1;
    public const int RpcLoginAuthDefault = 0;
    public const int RpcLoginAuthNegotiate = 1;
    public const int RpcLoginAuthKerberos = 2;
    public const int RpcLoginAuthNtlm = 3;

    // The Win32 codes the reader acts on.
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorFileNotFound = 2;
    public const int RpcInvalidBound = 1734;
    public const int EvtPublisherMetadataNotFound = 15002;
    public const int EvtMessageNotFound = 15027;
    public const int EvtMessageIdNotFound = 15028;
    public const int EvtUnresolvedValueInsert = 15029;
    public const int EvtUnresolvedParameterInsert = 15030;
    public const int EvtMaxInsertsReached = 15031;
    public const int EvtMessageLocaleNotFound = 15033;

    /// <summary><c>EVT_RPC_LOGIN</c>: the host, the account's user and domain, its password, the auth method.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EvtRpcLogin
    {
        public char* Server;
        public char* User;
        public char* Domain;
        public char* Password;
        public int Flags;
    }

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtQuery(nint session, string? path, string? query, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNext(nint resultSet, int eventsSize, nint* events, int timeout, int flags, out int returned);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtSeek(nint resultSet, long position, nint bookmark, int timeout, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtClose(nint handle);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtCancel(nint handle);

    [LibraryImport(Library, SetLastError = true)]
    public static partial nint EvtCreateRenderContext(int valuePathsCount, nint valuePaths, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtRender(nint context, nint fragment, int flags, int bufferSize, void* buffer, out int bufferUsed, out int propertyCount);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenPublisherMetadata(nint session, string publisherId, string? logFilePath, int locale, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtFormatMessage(nint publisherMetadata, nint evt, int messageId, int valueCount, void* values, int flags, int bufferSize, char* buffer, out int bufferUsed);

    [LibraryImport(Library, SetLastError = true)]
    public static partial nint EvtOpenChannelEnum(nint session, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtNextChannelPath(nint channelEnum, int channelPathBufferSize, char* channelPathBuffer, out int channelPathBufferUsed);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenLog(nint session, string path, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtGetLogInfo(nint log, int propertyId, int propertyValueBufferSize, void* propertyValueBuffer, out int propertyValueBufferUsed);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint EvtOpenChannelConfig(nint session, string channelPath, int flags);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtGetChannelConfigProperty(nint channelConfig, int propertyId, int flags, int propertyValueBufferSize, void* propertyValueBuffer, out int propertyValueBufferUsed);

    [LibraryImport(Library, SetLastError = true)]
    public static partial nint EvtOpenSession(int loginClass, EvtRpcLogin* login, int timeout, int flags);

    [LibraryImport(Library, SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EvtExportLog(nint session, string? path, string? query, string targetFilePath, int flags);

    /// <summary>The last Win32 code, as <see cref="Marshal.GetLastPInvokeError"/> reads it.</summary>
    public static int LastError => Marshal.GetLastPInvokeError();
}
