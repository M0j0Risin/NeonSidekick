using System.Runtime.InteropServices;
using System.Text;
using NeonSidekick.Updates;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>update:signature</c> (2026-11-04, self-update): nothing else in the app uses <c>ECDsa</c>, so this is its first proof under NativeAOT, on both OSes: a key made, a line signed and verified, and a changed byte refused.
    /// </summary>
    public static SmokeCheck ProbeUpdateSignature()
    {
        const string name = "update:signature";
        try
        {
            byte[] signed = Encoding.ASCII.GetBytes("hash *archive\n");
            using var key = UpdateSignature.NewKey();
            string line = UpdateSignature.SignatureLine(signed, key);
            string[] keys = [UpdateSignature.PublicKeyText(key)];
            bool good = UpdateSignature.Verify(signed, line, keys);
            byte[] changed = [.. signed];
            changed[0] ^= 1;
            bool bad = UpdateSignature.Verify(changed, line, keys);
            return new SmokeCheck(name, good && !bad, good && !bad ? "ECDSA P-256 signed, verified and refused a changed byte" : $"verified {good}, changed byte passed {bad}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// <c>update:keys</c> (2026-11-04): two embedded keys that parse, so a release can't ship with none or a throwaway one and strand its users. A simulation build skips this: its keys come from its own run.
    /// </summary>
    public static SmokeCheck ProbeUpdateKeys()
    {
        const string name = "update:keys";
#if UPDATE_SIM
        return new SmokeCheck(name, true, "skipped: a simulation build");
#else
        var keys = UpdateKeys.Embedded;
        bool good = keys.Count == UpdateKeys.Required && keys.All(UpdateSignature.IsPublicKey) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Count;
        return new SmokeCheck(name, good, good ? "the daily and backup keys are embedded" : $"needs {UpdateKeys.Required} different keys that parse, has {keys.Count}");
#endif
    }

    /// <summary>
    /// <c>vcruntime:load</c> (2026-11-04): the real loader on the published exe finds the Visual C++ runtime. The unit tests only fake it, and
    /// the build machines already need it for <c>whisper:load</c>, so this can't fail a good build.
    /// </summary>
    public static SmokeCheck ProbeVcRuntime()
    {
        string? missing = VcRuntime.Missing();
        return new SmokeCheck("vcruntime:load", missing is null, missing is null ? $"{VcRuntime.Libraries.Count} DLLs load" : missing + " won't load");
    }

    /// <summary>
    /// <c>update:no-sim-flags</c> (2026-11-04): a shipped build doesn't know the simulation's flags, which could point it at another server or another key. They're compiled in only under <c>-UpdateSim</c>, which skips this.
    /// </summary>
    public static SmokeCheck ProbeNoSimulationFlags()
    {
        const string name = "update:no-sim-flags";
#if UPDATE_SIM
        return new SmokeCheck(name, true, "skipped: a simulation build");
#else
        string[] flags = ["--update-source", "--update-as-version", "--update-check", "--update-move-all", "--update-swap-delay", "--update-test-key"];
        string[] known = [.. flags.Where(flag => SidekickOptions.Parse([flag, "x"]).Error is null)];
        return new SmokeCheck(name, known.Length == 0, known.Length == 0 ? "a shipped build refuses every one" : "knows " + string.Join(", ", known));
#endif
    }

    /// <summary>
    /// <c>update:rename-loaded</c> (2026-11-04): the swap's premise, a library loaded by this process renamed across folders on one volume, with Defender on where it runs. <paramref name="nativeDirectory"/> is the binary's folder.
    /// </summary>
    public static SmokeCheck ProbeUpdateRenameLoaded(string nativeDirectory)
    {
        const string name = "update:rename-loaded";
        string library = OperatingSystem.IsWindows() ? "libwinpthread-1.dll" : "libe_sqlite3.dylib";
        string source = Path.Combine(nativeDirectory, library);
        if (!File.Exists(source))
        {
            return new SmokeCheck(name, false, $"{library} is missing from {nativeDirectory}");
        }

        string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N"));
        nint handle = 0;
        try
        {
            string first = Path.Combine(folder, "a", library);
            string second = Path.Combine(folder, "b", library);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!);
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.Copy(source, first);
            if (!NativeLibrary.TryLoad(first, out handle))
            {
                return new SmokeCheck(name, false, $"{library} wouldn't load from a temp folder");
            }

            File.Move(first, second, overwrite: false);
            bool moved = File.Exists(second) && !File.Exists(first);
            return new SmokeCheck(name, moved, moved ? "moved a loaded library across folders" : "the move left it where it was");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (handle != 0)
            {
                NativeLibrary.Free(handle);
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            }
        }
    }

    /// <summary>
    /// <c>update:lock</c> (2026-11-04): the shared and exclusive lock, two handles in one process, which conflict on both OSes, so the smoke starts no process: shared allows shared and refuses exclusive, and exclusive refuses both.
    /// </summary>
    public static SmokeCheck ProbeUpdateLock()
    {
        const string name = "update:lock";
        string file = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N") + ".lock");
        try
        {
            bool sharedShares;
            bool sharedRefuses;
            using (var shared = UpdateLock.TryShared(file))
            {
                using var second = UpdateLock.TryShared(file);
                using var refused = UpdateLock.TryExclusive(file);
                sharedShares = shared is not null && second is not null;
                sharedRefuses = refused is null;
            }

            bool exclusiveRefuses;
            using (var exclusive = UpdateLock.TryExclusive(file))
            {
                using var refusedShared = UpdateLock.TryShared(file);
                using var refusedExclusive = UpdateLock.TryExclusive(file);
                exclusiveRefuses = exclusive is not null && refusedShared is null && refusedExclusive is null;
            }

            bool good = sharedShares && sharedRefuses && exclusiveRefuses;
            return new SmokeCheck(name, good, good ? "shared allows shared and refuses exclusive; exclusive refuses both" : $"shared shares {sharedShares}, refuses exclusive {sharedRefuses}, exclusive refuses both {exclusiveRefuses}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.DiagnosticLog.Info(UpdateJournal.Category, ex.Message);
            }
        }
    }
}
