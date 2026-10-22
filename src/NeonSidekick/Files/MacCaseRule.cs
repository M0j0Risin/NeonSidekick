using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Files;

/// <summary>
/// A Mac's half of <see cref="CaseRule"/> (2026-10-22, review G16): <c>pathconf(_PC_CASE_SENSITIVE)</c> on the folder, 1 for a case-sensitive
/// APFS or HFS+ volume, 0 for the case-blind default; libSystem, every Mac process's. Null when the volume doesn't say. Unproven until a Mac
/// runs it (<c>.notes/mac_todo.md</c>); excluded from coverage with the other native layers.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MacCaseRule
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int CaseSensitiveName = 11;   // _PC_CASE_SENSITIVE

    /// <summary>Whether <paramref name="folder"/>'s names differ by case alone; null when the volume doesn't say.</summary>
    public static bool? IsCaseSensitive(string folder)
    {
        nint answer = PathConf(folder, CaseSensitiveName);
        return answer < 0 ? null : answer != 0;
    }

    [LibraryImport(LibSystem, EntryPoint = "pathconf", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint PathConf(string path, int name);
}
