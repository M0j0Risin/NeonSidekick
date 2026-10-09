using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Printing;

/// <summary>
/// libcups' C API as <see cref="MacPrintSpooler"/> calls it (2026-10-08, printing on a Mac; the user's pick over <c>lp</c>, so no
/// process-start site): the destinations and their default paper, a job made, its one PDF written in chunks and finished, and a
/// job withdrawn. <c>/usr/lib/libcups.2.dylib</c> lives in the dyld shared cache, not on disk, so it is bound by that name and the
/// smoke's <c>print:cups</c> loads it with <see cref="NativeLibrary.TryLoad(string, out nint)"/>. Strings are UTF-8 by the source
/// generator; the structs are blittable and go by pointer (layouts measured on 15.7: <c>cups_dest_t</c> 32 bytes,
/// <c>cups_size_t</c> 152, <c>cups_job_t</c> 80). A null <c>http_t</c> is <c>CUPS_HTTP_DEFAULT</c>, the calling thread's own
/// connection, as is <see cref="cupsLastErrorString"/>: one job's calls stay on one thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class CupsNative
{
    private const string Cups = MacPrintRules.CupsLibrary;

    /// <summary><c>HTTP_STATUS_CONTINUE</c>: a document request open and taking data.</summary>
    public const int HttpContinue = 100;

    /// <summary>The first IPP status that is not some <c>successful-ok</c>.</summary>
    public const int IppFirstError = 0x0100;

    /// <summary><c>CUPS_WHICHJOBS_ACTIVE</c>: pending, held and processing jobs.</summary>
    public const int WhichJobsActive = 0;

    /// <summary><c>IPP_JSTATE_HELD</c>.</summary>
    public const int JobHeld = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct CupsOption
    {
        public byte* Name;
        public byte* Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CupsDest
    {
        public byte* Name;
        public byte* Instance;
        public int IsDefault;
        public int OptionCount;
        public CupsOption* Options;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CupsSize
    {
        public fixed byte Media[128];
        public int Width;
        public int Length;
        public int Bottom;
        public int Left;
        public int Right;
        public int Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CupsJob
    {
        public int Id;
        public byte* Dest;
        public byte* Title;
        public byte* User;
        public byte* Format;
        public int State;
        public int Size;
        public int Priority;
        public long CompletedTime;
        public long CreationTime;
        public long ProcessingTime;
    }

    [LibraryImport(Cups)]
    public static partial int cupsGetDests2(nint http, CupsDest** dests);

    [LibraryImport(Cups)]
    public static partial void cupsFreeDests(int count, CupsDest* dests);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial CupsDest* cupsGetDest(string name, string? instance, int count, CupsDest* dests);

    [LibraryImport(Cups)]
    public static partial nint cupsCopyDestInfo(nint http, CupsDest* dest);

    [LibraryImport(Cups)]
    public static partial void cupsFreeDestInfo(nint info);

    [LibraryImport(Cups)]
    public static partial int cupsGetDestMediaDefault(nint http, CupsDest* dest, nint info, uint flags, CupsSize* size);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsAddOption(string name, string value, int count, CupsOption** options);

    [LibraryImport(Cups)]
    public static partial void cupsFreeOptions(int count, CupsOption* options);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsCreateJob(nint http, string name, string title, int count, CupsOption* options);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsStartDocument(nint http, string name, int jobId, string docName, string format, int lastDocument);

    [LibraryImport(Cups)]
    public static partial int cupsWriteRequestData(nint http, byte* buffer, nuint length);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsFinishDocument(nint http, string name);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsCancelJob2(nint http, string name, int jobId, int purge);

    [LibraryImport(Cups, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsGetJobs2(nint http, CupsJob** jobs, string? name, int myJobs, int whichJobs);

    [LibraryImport(Cups)]
    public static partial void cupsFreeJobs(int count, CupsJob* jobs);

    [LibraryImport(Cups)]
    public static partial nint cupsLastErrorString();

    [LibraryImport(Cups)]
    public static partial nint cupsServer();

    [LibraryImport(Cups)]
    public static partial int ippPort();

    [LibraryImport(Cups)]
    public static partial int cupsEncryption();

    /// <summary><c>httpConnect2</c>: a connection of the job's own, closed to abandon a document half sent.</summary>
    [LibraryImport(Cups)]
    public static partial nint httpConnect2(nint host, int port, nint addrList, int family, int encryption, int blocking, int msec, int* cancel);

    [LibraryImport(Cups)]
    public static partial void httpClose(nint http);

    /// <summary>The thread's last CUPS error, as words.</summary>
    public static string LastError() => MacPrintRules.Detail(Marshal.PtrToStringUTF8(cupsLastErrorString()));

    /// <summary>A UTF-8 C string, or null.</summary>
    public static string? Text(byte* value) => value is null ? null : Marshal.PtrToStringUTF8((nint)value);
}
