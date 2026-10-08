using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Shortcuts;

/// <summary>
/// The native calls behind <c>/shortcut</c> (2026-10-07): the shell's own <c>ShellLink</c> object, made with ole32's
/// <c>CoCreateInstance</c> (the first coclass the app makes; every other COM object so far came from a flat factory), set up
/// through <c>IShellLinkW</c> and saved through <c>IPersistFile</c>. Every interface is called through its vtable with unmanaged
/// function pointers (the <c>CameraNative</c> shape): no <c>ComWrappers</c>, nothing for AOT to marshal; a string is handed over
/// pinned (<c>fixed</c> on a .NET string is NUL-terminated UTF-16). Writing the <c>.lnk</c> format by hand (MS-SHLLINK) was the
/// other road and was not taken: the shell's object is what Explorer reads back. No process is started: Explorer launches the
/// shortcut later. System libraries, so none joins <c>SmokeChecks.RequiredNativeLibraries</c>; the smoke's <c>shortcut:shelllink</c>
/// proves the slots on the published exe.
///
/// <para>Slots from <c>ShObjIdl_core.h</c>'s and <c>ObjIdl.h</c>'s declaration order: IUnknown 0–2; IShellLinkW 3–20 (GetPath 3,
/// SetDescription 7, GetWorkingDirectory 8, SetWorkingDirectory 9, GetArguments 10, SetArguments 11, SetIconLocation 17,
/// SetPath 20); IPersist 3 under IPersistFile 4–8 (Load 5, Save 6).</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class ShortcutNative
{
    internal const uint CoinitApartmentThreaded = 0x2;
    internal const int RpcEChangedMode = unchecked((int)0x80010106);

    private const uint ClsctxInprocServer = 0x1;
    private const uint StgmRead = 0x0;

    /// <summary>The buffer a read-back hands the shell for a path or the arguments, in characters.</summary>
    private const int ReadBuffer = 4096;

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010b-0000-0000-C000-000000000046");

    private const int QueryInterfaceSlot = 0;
    private const int ReleaseSlot = 2;
    private const int GetPathSlot = 3;
    private const int SetDescriptionSlot = 7;
    private const int GetWorkingDirectorySlot = 8;
    private const int SetWorkingDirectorySlot = 9;
    private const int GetArgumentsSlot = 10;
    private const int SetArgumentsSlot = 11;
    private const int SetIconLocationSlot = 17;
    private const int SetPathSlot = 20;
    private const int PersistLoadSlot = 5;
    private const int PersistSaveSlot = 6;

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    /// <summary>Writes <paramref name="spec"/> as a <c>.lnk</c>, replacing one at its path; the icon is the target's first. On a thread with COM started.</summary>
    internal static void Write(ShortcutSpec spec)
    {
        nint link = CreateShellLink();
        try
        {
            Check(SetString(link, SetPathSlot, spec.Target), "SetPath");
            Check(SetString(link, SetArgumentsSlot, spec.Arguments), "SetArguments");
            Check(SetString(link, SetWorkingDirectorySlot, spec.WorkingDirectory), "SetWorkingDirectory");
            Check(SetString(link, SetDescriptionSlot, spec.Description), "SetDescription");
            Check(SetIconLocation(link, spec.Target, 0), "SetIconLocation");

            nint file = PersistFile(link);
            try
            {
                fixed (char* path = spec.LinkPath)
                {
                    Check(((delegate* unmanaged<nint, char*, int, int>)Slot(file, PersistSaveSlot))(file, path, 1), "IPersistFile.Save");
                }
            }
            finally
            {
                Release(file);
            }
        }
        finally
        {
            Release(link);
        }
    }

    /// <summary>A <c>.lnk</c>'s target, arguments and Start-in folder as the shell reads them back. On a thread with COM started.</summary>
    internal static (string Target, string Arguments, string WorkingDirectory) Read(string linkPath)
    {
        nint link = CreateShellLink();
        try
        {
            nint file = PersistFile(link);
            try
            {
                fixed (char* path = linkPath)
                {
                    Check(((delegate* unmanaged<nint, char*, uint, int>)Slot(file, PersistLoadSlot))(file, path, StgmRead), "IPersistFile.Load");
                }
            }
            finally
            {
                Release(file);
            }

            char* buffer = stackalloc char[ReadBuffer];
            Check(((delegate* unmanaged<nint, char*, int, nint, uint, int>)Slot(link, GetPathSlot))(link, buffer, ReadBuffer, 0, 0), "GetPath");
            string target = new(buffer);
            Check(((delegate* unmanaged<nint, char*, int, int>)Slot(link, GetArgumentsSlot))(link, buffer, ReadBuffer), "GetArguments");
            string arguments = new(buffer);
            Check(((delegate* unmanaged<nint, char*, int, int>)Slot(link, GetWorkingDirectorySlot))(link, buffer, ReadBuffer), "GetWorkingDirectory");
            return (target, arguments, new string(buffer));
        }
        finally
        {
            Release(link);
        }
    }

    private static nint CreateShellLink()
    {
        Guid clsid = ClsidShellLink;
        Guid iid = IidShellLinkW;
        nint link;
        Check(CoCreateInstance(&clsid, 0, ClsctxInprocServer, &iid, &link), "CoCreateInstance(ShellLink)");
        return link;
    }

    private static nint PersistFile(nint link)
    {
        Guid iid = IidPersistFile;
        nint file;
        Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(link, QueryInterfaceSlot))(link, &iid, &file), "QueryInterface(IPersistFile)");
        return file;
    }

    private static int SetString(nint link, int slot, string value)
    {
        fixed (char* text = value)
        {
            return ((delegate* unmanaged<nint, char*, int>)Slot(link, slot))(link, text);
        }
    }

    private static int SetIconLocation(nint link, string path, int index)
    {
        fixed (char* text = path)
        {
            return ((delegate* unmanaged<nint, char*, int, int>)Slot(link, SetIconLocationSlot))(link, text, index);
        }
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new IOException($"{call} failed (0x{hr:X8})");
        }
    }

    private static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, ReleaseSlot))(unknown);
        }
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
}
