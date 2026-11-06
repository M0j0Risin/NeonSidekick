using System.Runtime.InteropServices;

namespace NeonSidekick.App;

/// <summary>
/// Whether Microsoft's Visual C++ runtime is here (2026-11-04, the self-update plan; the user's pick: named, never shipped). The app
/// doesn't import it, but the libraries behind voice and the embedded model do, and a PC without it was told
/// <c>onnxruntime.dll is missing beside the exe</c> (the file is there), the loader's message, or <c>exited with code -1073741515</c>.
/// Asked only after a failure, or before the embedded model's download or start, never at every launch; its answer names only a DLL
/// that really won't load, so another cause is never blamed on the runtime. A Mac has nothing to do here: its libraries link only
/// what macOS carries, so the real loader answers yes there. A copy that's present but too old loads, so it isn't reported.
/// </summary>
public static class VcRuntime
{
    /// <summary>The one DLL <c>llama-server.exe</c> itself imports. The embedded model refuses only on this one: what its server's own DLLs need hasn't been read, so a working setup can't be refused.</summary>
    public const string MainLibrary = "vcruntime140.dll";

    /// <summary>What the bundled libraries import, read from their PE headers; <c>vcomp140</c> is Whisper's.</summary>
    public static readonly IReadOnlyList<string> Libraries = [MainLibrary, "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "vcomp140.dll"];

    /// <summary>A process that couldn't find a DLL it imports exits with this: 0xC0000135, STATUS_DLL_NOT_FOUND.</summary>
    public const int DllNotFoundExitCode = -1073741515;

    /// <summary>The first of <see cref="Libraries"/> that won't load, or null when all do. <paramref name="loads"/> stands in for the loader in a test.</summary>
    public static string? Missing(Func<string, bool>? loads = null)
    {
        var loader = loads ?? Loads;
        foreach (string library in Libraries)
        {
            if (!loader(library))
            {
                return library;
            }
        }

        return null;
    }

    /// <summary>Whether <see cref="MainLibrary"/> won't load, the embedded model's one question.</summary>
    public static bool MainMissing(Func<string, bool>? loads = null) => !(loads ?? Loads)(MainLibrary);

    /// <summary>
    /// What a failed load says: the runtime's text when one of its DLLs is missing, <paramref name="otherwise"/> when none is. Asked in both
    /// of a load's catch branches, since the exception's type can vary under NativeAOT. The original detail stays behind the runtime's text (2026-11-04, review P1-16): an engine may not need the DLL that's missing, and a corrupt model's reason was lost to it.
    /// </summary>
    public static string Explain(string otherwise, Func<string, bool>? loads = null) =>
        Missing(loads) is { } library ? VcRuntimeText.Missing(library) + " " + VcRuntimeText.Detail(otherwise) : otherwise;

    /// <summary>
    /// <see cref="Explain"/> for a load that failed because a DLL wasn't found, where <paramref name="otherwise"/> is the app's own guess and not the load's words (2026-11-04, review P14-4): beside the runtime's text it told a PC without it that a file sitting beside the exe was missing, the false line this class exists to stop.
    /// </summary>
    public static string ExplainAlone(string otherwise, Func<string, bool>? loads = null) =>
        Missing(loads) is { } library ? VcRuntimeText.Missing(library) : otherwise;

    /// <summary>The real loader: a failed <see cref="NativeLibrary.TryLoad(string, out IntPtr)"/> raises no box under NativeAOT.</summary>
    private static bool Loads(string library)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (!NativeLibrary.TryLoad(library, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
}
