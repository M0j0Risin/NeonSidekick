using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Images;

namespace NeonSidekick.Unc;

/// <summary>
/// A Mac's mounts as <see cref="ShareMounts"/> makes them (2026-10-11): NetFS's <c>NetFSMountURLSync</c> in-process — no
/// <c>mount_smbfs</c>, no process-start site — libc's <c>unmount(2)</c>, and <c>getfsstat(MNT_NOWAIT)</c> for what is mounted,
/// which reads the kernel's cached figures and never asks a server (a dead one cannot hang a check). Measured on 15.7 that day:
/// NetFS mounts as the user under <c>/Volumes</c> without root, <c>MNT_RDONLY</c> holds ("Read-only file system"), <c>nobrowse</c>
/// keeps it out of Finder, and <c>unmount</c> as the user removes the folder too.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacMountSystem : IMountSystem
{
    /// <summary>This process's effective user id: whose a mount is (<c>statfs</c>'s <c>f_owner</c>).</summary>
    public static uint UserId => NetFSNative.UserId();

    /// <inheritdoc />
    public int Mount(string url, string? user, string? password, bool readOnly, bool newSession, out string? mountPoint) =>
        NetFSNative.Mount(url, user, password, readOnly, newSession, out mountPoint);

    /// <inheritdoc />
    public MountInfo? Info(string path) => NetFSNative.Mounts().FirstOrDefault(m => string.Equals(m.On, path, StringComparison.Ordinal));

    /// <inheritdoc />
    public IReadOnlyList<MountInfo> SmbMounts() => [.. NetFSNative.Mounts().Where(m => m.Type == MacMountRules.SmbType)];

    /// <inheritdoc />
    public int Unmount(string mountPoint, bool force) => NetFSNative.Unmount(mountPoint, force);
}

/// <summary>
/// NetFS, libc's mount calls and the CoreFoundation they need (2026-10-11, <see cref="MacMountSystem"/>). Pointers and integers
/// only; <c>struct statfs</c> is read by offset from a byte buffer (64-bit inode layout, 2168 bytes, offsets measured with the
/// SDK's own header that day). CoreFoundation objects are <see cref="ImageIONative"/>'s. The smoke's <c>unc:netfs</c> binds all of it
/// on the published binary without mounting anything.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class NetFSNative
{
    private const string NetFS = "/System/Library/Frameworks/NetFS.framework/NetFS";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibC = "libc";

    private const int StatfsSize = 2168;
    private const int FsidOffset = 48;
    private const int OwnerOffset = 56;
    private const int TypeNameOffset = 72;
    private const int OnOffset = 88;
    private const int FromOffset = 1112;
    private const int NameLength = 1024;
    private const int NoWait = 2;              // MNT_NOWAIT
    private const int ReadOnly = 0x1;          // MNT_RDONLY
    private const int DontBrowse = 0x100000;   // MNT_DONTBROWSE
    private const int Force = 0x80000;         // MNT_FORCE

    [LibraryImport(NetFS)]
    private static partial int NetFSMountURLSync(nint url, nint mountPath, nint user, nint password, nint openOptions, nint mountOptions, nint* mountPoints);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, nint baseUrl);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte* buffer, nint length, byte isDirectory);

    [LibraryImport(LibC, EntryPoint = "getfsstat", SetLastError = true)]
    private static partial int GetFsStat(byte* buffer, int size, int mode);

    [LibraryImport(LibC, EntryPoint = "unmount", SetLastError = true)]
    private static partial int UnmountCall(byte* path, int flags);

    [LibraryImport(LibC, EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    /// <summary>This process's effective user id.</summary>
    public static uint UserId() => GetEffectiveUserId();

    private static nint s_true;

    /// <summary><c>kCFBooleanTrue</c>.</summary>
    private static nint True => s_true != 0 ? s_true : s_true = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFBooleanTrue");

    /// <summary>
    /// Mounts <paramref name="url"/> under <c>/Volumes</c>: no UI, loopback allowed, a new session when asked, <c>nobrowse</c>, and
    /// <c>rdonly</c> when asked. NetFS's status, and the mount point on success.
    /// </summary>
    public static int Mount(string url, string? user, string? password, bool readOnly, bool newSession, out string? mountPoint)
    {
        mountPoint = null;
        var made = new List<nint>();
        try
        {
            nint cfUrl = Keep(made, Url(url));
            nint volumes = Keep(made, FolderUrl("/Volumes"));
            nint open = Keep(made, ImageIONative.CreateDictionary());
            nint mount = Keep(made, ImageIONative.CreateDictionary());
            ImageIONative.CFDictionarySetValue(open, Keep(made, ImageIONative.CreateString("UIOption")), Keep(made, ImageIONative.CreateString("NoUI")));
            ImageIONative.CFDictionarySetValue(open, Keep(made, ImageIONative.CreateString("AllowLoopback")), True);
            if (newSession)
            {
                ImageIONative.CFDictionarySetValue(open, Keep(made, ImageIONative.CreateString("ForceNewSession")), True);
            }

            int flags = DontBrowse | (readOnly ? ReadOnly : 0);
            ImageIONative.CFDictionarySetValue(mount, Keep(made, ImageIONative.CreateString("MountFlags")), Keep(made, ImageIONative.CFNumberCreate(0, ImageIONative.NumberSInt32, &flags)));
            nint cfUser = user is null ? 0 : Keep(made, ImageIONative.CreateString(user));
            nint cfPassword = password is null ? 0 : Keep(made, ImageIONative.CreateString(password));
            nint points = 0;
            int status = NetFSMountURLSync(cfUrl, volumes, cfUser, cfPassword, open, mount, &points);
            if (points != 0)
            {
                made.Add(points);
                if (ImageIONative.CFArrayGetCount(points) > 0)
                {
                    mountPoint = ImageIONative.Text(ImageIONative.CFArrayGetValueAtIndex(points, 0));
                }
            }

            return status;
        }
        finally
        {
            foreach (nint cf in made)
            {
                if (cf != 0)
                {
                    ImageIONative.CFRelease(cf);
                }
            }
        }
    }

    /// <summary>Every mounted file system, from the kernel's cache (<c>MNT_NOWAIT</c>); empty when it cannot be read.</summary>
    public static IReadOnlyList<MountInfo> Mounts()
    {
        int count = GetFsStat(null, 0, NoWait);
        if (count <= 0)
        {
            return [];
        }

        int room = count + 8;
        byte[] buffer = new byte[room * StatfsSize];
        fixed (byte* p = buffer)
        {
            count = GetFsStat(p, buffer.Length, NoWait);
            var mounts = new List<MountInfo>(Math.Max(count, 0));
            for (int i = 0; i < count; i++)
            {
                byte* entry = p + (i * StatfsSize);
                long fsid = ((long)*(int*)(entry + FsidOffset + 4) << 32) | (uint)*(int*)(entry + FsidOffset);
                uint owner = *(uint*)(entry + OwnerOffset);
                mounts.Add(new MountInfo(fsid, owner, CString(entry + TypeNameOffset, 16), CString(entry + FromOffset, NameLength), CString(entry + OnOffset, NameLength)));
            }

            return mounts;
        }
    }

    /// <summary>Unmounts <paramref name="mountPoint"/>; 0 or the errno.</summary>
    public static int Unmount(string mountPoint, bool force)
    {
        byte[] path = [.. Encoding.UTF8.GetBytes(mountPoint), 0];
        fixed (byte* p = path)
        {
            return UnmountCall(p, force ? Force : 0) == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    /// <summary>Binds every call and builds the option dictionaries, mounting nothing (the smoke's probe). Null, or what failed.</summary>
    public static string? Probe()
    {
        foreach (string name in new[] { "NetFSMountURLSync" })
        {
            if (!NativeLibrary.TryGetExport(NativeLibrary.Load(NetFS), name, out _))
            {
                return "NetFS has no " + name;
            }
        }

        nint url = Url("smb://probe.invalid/share%20name");
        nint folder = FolderUrl("/Volumes");
        try
        {
            if (url == 0 || folder == 0 || True == 0)
            {
                return "CoreFoundation refused the URLs";
            }
        }
        finally
        {
            if (url != 0)
            {
                ImageIONative.CFRelease(url);
            }

            if (folder != 0)
            {
                ImageIONative.CFRelease(folder);
            }
        }

        if (Mounts().Count == 0)
        {
            return "getfsstat listed no file systems";
        }

        return Unmount("/nonexistent-neonsidekick-probe", force: false) is 2 or 22 ? null : "unmount did not answer ENOENT";
    }

    private static nint Keep(List<nint> made, nint cf)
    {
        made.Add(cf);
        return cf;
    }

    private static nint Url(string url)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(url);
        fixed (byte* p = bytes)
        {
            return CFURLCreateWithBytes(0, p, bytes.Length, ImageIONative.Utf8, 0);
        }
    }

    private static nint FolderUrl(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(path);
        fixed (byte* p = bytes)
        {
            return CFURLCreateFromFileSystemRepresentation(0, p, bytes.Length, 1);
        }
    }

    private static string CString(byte* text, int max)
    {
        int length = 0;
        while (length < max && text[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(text, length);
    }
}
