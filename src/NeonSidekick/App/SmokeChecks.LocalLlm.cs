using System.Globalization;
using NeonSidekick.LocalLlm;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>llm:job-object</c> (2026-09-29): the local model's kill-on-close job in the published binary — a job created,
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
    /// models folder's sibling <c>llama</c>): every required file there and the executable a PE image. Nothing is
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
            var installed = Enum.GetValues<LlamaBackend>().Where(b => Directory.Exists(LlamaRelease.Folder(llama, b))).ToList();
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

                if (!StartsWithMz(LlamaRelease.Executable(llama, backend)))
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
}
