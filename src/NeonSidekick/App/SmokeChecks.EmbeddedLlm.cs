using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>llm:job-object</c> (2026-09-29): the embedded model's kill-on-close job in the published binary — a job created,
    /// <c>KILL_ON_JOB_CLOSE</c> set through the extended-limit struct, the flags read back through the same struct (which
    /// proves the source-generated marshalling kept its layout under AOT), and the job closed. No process is put in it.
    /// </summary>
    public static SmokeCheck ProbeJobObject()
    {
        const string name = "llm:job-object";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var (job, error) = ChildJob.CreateKillOnClose();
            if (job == 0)
            {
                return new SmokeCheck(name, false, error);
            }

            try
            {
                var (ok, flags, queryError) = ChildJob.QueryFlags(job);
                if (!ok)
                {
                    return new SmokeCheck(name, false, queryError);
                }

                bool kill = (flags & JobObjectNative.JobObjectLimitKillOnJobClose) != 0;
                return new SmokeCheck(name, kill, string.Create(CultureInfo.InvariantCulture, $"limit flags 0x{flags:X4}{(kill ? "" : ": kill-on-close is not set")}"));
            }
            finally
            {
                ChildJob.Close(job);
            }
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>llm:llama-server</c> (2026-09-29): the pinned llama.cpp runtime, when one is installed under the home (the
    /// models folder's sibling <c>llama</c>): every required file there and the executable a PE image — a Mach-O one for the
    /// Metal build (2026-10-07), whose folder alone a Mac looks at. Nothing is
    /// downloaded and nothing is started — a smoke run must not fetch hundreds of megabytes or load a model — so a home
    /// without the runtime passes as not exercised, as the Kokoro synthesis probe does without its model.
    /// </summary>
    public static SmokeCheck ProbeLlamaServer(string? modelsDirectory)
    {
        const string name = "llm:llama-server";
        try
        {
            string? home = modelsDirectory is null ? null : Path.GetDirectoryName(Path.GetFullPath(modelsDirectory));
            if (home is null)
            {
                return new SmokeCheck(name, true, "not exercised: no home");
            }

            string llama = Path.Combine(home, "llama");
            var installed = Enum.GetValues<LlamaBackend>().Where(b => (b == LlamaBackend.Metal) == OperatingSystem.IsMacOS() && Directory.Exists(LlamaRelease.Folder(llama, b))).ToList();
            if (installed.Count == 0)
            {
                return new SmokeCheck(name, true, $"not exercised: no llama.cpp {LlamaRelease.Tag} runtime under {llama}");
            }

            foreach (var backend in installed)
            {
                if (!LlamaRelease.Installed(llama, backend))
                {
                    return new SmokeCheck(name, false, $"{LlamaRelease.Folder(llama, backend)} is missing some of {string.Join(", ", LlamaRelease.RequiredFiles(backend))}");
                }

                if (!(backend == LlamaBackend.Metal ? IsMachO(LlamaRelease.Executable(llama, backend)) : StartsWithMz(LlamaRelease.Executable(llama, backend))))
                {
                    return new SmokeCheck(name, false, $"{LlamaRelease.Executable(llama, backend)} is not an executable");
                }
            }

            return new SmokeCheck(name, true, $"llama.cpp {LlamaRelease.Tag}: {string.Join(", ", installed.Select(LlamaRelease.Name))} complete");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool StartsWithMz(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    /// <summary>Whether <paramref name="path"/> starts with the 64-bit Mach-O magic, <c>MH_MAGIC_64</c> little-endian (<c>CF FA ED FE</c>).</summary>
    private static bool IsMachO(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0xCF && stream.ReadByte() == 0xFA && stream.ReadByte() == 0xED && stream.ReadByte() == 0xFE;
    }

    /// <summary>
    /// <c>llm:tar-unpack</c> (2026-10-07, the embedded LLM on a Mac): <see cref="Speech.ModelStore.ExtractTarGz"/> in the
    /// published binary — a tiny gzipped tar made here, as llama.cpp's is laid out (one top folder, an executable file, a
    /// <c>.0</c> link to a versioned one), unpacked and read back: the exec bits kept, the link a link to its file. Then a
    /// tar whose link points out of the folder must be refused. Off Windows only: Windows' runtimes are zips.
    /// </summary>
    public static SmokeCheck ProbeTarUnpack()
    {
        const string name = "llm:tar-unpack";
        if (OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: Windows' runtimes are zips");
        }

        string root = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-tar-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string archive = Path.Combine(root, "runtime.tar.gz");
            WriteTarGz(archive, writer =>
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "top/"));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "top/server") { Mode = (UnixFileMode)0b111_101_101, DataStream = new MemoryStream([0xCF, 0xFA, 0xED, 0xFE]) });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "top/libx.0.1.dylib") { Mode = (UnixFileMode)0b110_100_100, DataStream = new MemoryStream([1, 2, 3]) });
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "top/libx.0.dylib") { LinkName = "libx.0.1.dylib" });
            });
            string into = Path.Combine(root, "into");
            Speech.ModelStore.ExtractTarGz(archive, into);
            string server = Path.Combine(into, "top", "server");
            var link = new FileInfo(Path.Combine(into, "top", "libx.0.dylib"));
            bool executable = (File.GetUnixFileMode(server) & UnixFileMode.UserExecute) != 0;
            bool linked = link.LinkTarget == "libx.0.1.dylib" && link.ResolveLinkTarget(true) is FileInfo { Exists: true, Length: 3 };
            if (!executable || !linked)
            {
                return new SmokeCheck(name, false, $"unpacked, but executable {executable}, link {linked}");
            }

            string hostile = Path.Combine(root, "hostile.tar.gz");
            WriteTarGz(hostile, writer => writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "top/escape") { LinkName = "../../../etc/passwd" }));
            try
            {
                Speech.ModelStore.ExtractTarGz(hostile, Path.Combine(root, "hostile"));
                return new SmokeCheck(name, false, "a link out of the folder was unpacked");
            }
            catch (IOException)
            {
                return new SmokeCheck(name, true, "exec bits and a .0 link kept; a link out of the folder refused");
            }
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is harmless
            }
        }
    }

    private static void WriteTarGz(string path, Action<TarWriter> entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        entries(writer);
    }

    /// <summary>
    /// <c>llm:metal</c> (2026-10-07): Metal's recommended working set read in the published binary through Metal.framework and
    /// libobjc (<see cref="Perf.MetalNative"/>), what the Embedded VRAM budget measures on a Mac. A Mac with no Metal device (a
    /// CI virtual machine may have none) passes as not exercised: the frameworks loaded and answered.
    /// </summary>
    public static SmokeCheck ProbeMetal()
    {
        const string name = "llm:metal";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        try
        {
            // A virtual machine's paravirtual GPU (GitHub's Mac runner) may answer with no working set: the frameworks still
            // loaded and answered, so that is not exercised, not a failure.
            return Perf.MetalNative.Read() switch
            {
                { WorkingSetBytes: > 0 } device => new SmokeCheck(name, true, string.Create(CultureInfo.InvariantCulture, $"{device.Name}: working set {device.WorkingSetBytes / 1_048_576} MiB")),
                { } device => new SmokeCheck(name, true, $"not exercised: {device.Name} reports no working set"),
                null => new SmokeCheck(name, true, "not exercised: no Metal device"),
            };
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>llm:records</c> (2026-10-07): what the Mac's server records stand on, in the published binary — this process's start
    /// time and executable read back through .NET's <see cref="Process"/> (proc_pidinfo/proc_pidpath underneath), a record of
    /// it written and parsed back, and a sweep over it that kills nothing because its owner (this process) is alive.
    /// </summary>
    public static SmokeCheck ProbeRecords()
    {
        const string name = "llm:records";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        string llama = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var self = Process.GetCurrentProcess();
            string? path = LlamaRecords.ExecutableOf(self.Id);
            if (LlamaRecords.StartTicks(self.Id) is null || !string.Equals(path, Environment.ProcessPath, StringComparison.Ordinal))
            {
                return new SmokeCheck(name, false, $"this process read back as {path ?? "nothing"}, not {Environment.ProcessPath}");
            }

            string? record = LlamaRecords.Write(LlamaRecords.Folder(llama), self.Id, self, Path.Combine(llama, "b0-metal", "llama-server"));
            if (record is null || LlamaRecords.Parse(File.ReadAllText(record)) is not { } parsed || parsed.ServerPid != self.Id)
            {
                return new SmokeCheck(name, false, "the record did not read back");
            }

            var killed = LlamaRecords.Sweep(llama, kill: _ => throw new InvalidOperationException("the sweep killed a live owner's server"));
            return new SmokeCheck(name, killed.Count == 0 && File.Exists(record), "this process read back; a live owner's record kept");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(llama, recursive: true);
            }
            catch (IOException)
            {
                // a temp folder left behind is harmless
            }
        }
    }
}
