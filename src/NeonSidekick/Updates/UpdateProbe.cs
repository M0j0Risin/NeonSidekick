using System.Diagnostics;
using System.Runtime.InteropServices;
using NeonSidekick.App;
using NeonSidekick.EmbeddedLlm;

namespace NeonSidekick.Updates;

/// <summary>What the probe found of its own manifest.</summary>
public enum UpdateProbeManifest
{
    Good,

    /// <summary>Gone, or a manifest this build can't read: a package no copy could later update from, and its fault.</summary>
    Bad,

    /// <summary>There, and held past the probe's few tries: the machine's doing, so it can't read as the package's (2026-11-04, review P8-7: a scanner on the fresh manifest dropped a whole download).</summary>
    Unreadable,
}

/// <summary>
/// <c>--update-probe &lt;result-file&gt;</c> (2026-11-04, self-update): the staged exe proving it can run from the stage, before anything installs it. A helper mode like <c>--mcp-relay</c> and <c>LlamaGuard</c>: an
/// <see cref="Asked"/> test at the top of <c>Program.cs</c>, before anything touches the console, the options, the home or the Mac sweeps, so no startup line can reach its result. A lasting contract: the old exe reads what the
/// new one writes here, so the lines are pinned by a test.
///
/// <para>The answer is a file and the exit code, never captured output, because <c>ChildJob</c> sends stdout to <c>NUL</c>. Each library's name goes in the file before it's loaded, so a crash names it, and a failed load goes on
/// to the next. No model is loaded. Whisper's two Windows builds are only checked to exist: an old CPU might crash on the AVX one, and <c>Avx.IsSupported</c> is a compile-time constant under NativeAOT, so the probe can't tell
/// (the smoke's <c>whisper:load</c> still proves the load on the build machine).</para>
///
/// <para>Neither a failed load nor a crash may raise a box: a child inherits Windows' default error mode, and a box left open would sit over the user's chat and run into the 30 s limit. .NET 10's
/// <see cref="NativeLibrary.TryLoad(string, out nint)"/> turns a load's boxes off under NativeAOT, but only there (2026-11-04: the simulation's Debug build raised "Bad Image" for a damaged library another one imports, and the user saw it),
/// so nothing rests on that: on Windows the first act is <c>SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX)</c>, for this process only. It ends through <c>ProcessEnd</c> on Windows, unlike the other helpers, since it has native libraries
/// loaded, and a hung detach would time out and reject a good release.</para>
/// </summary>
public static class UpdateProbe
{
    /// <summary>The flag. The name and its one argument are a lasting contract.</summary>
    public const string Flag = "--update-probe";

    /// <summary>The exit code of a run where every check passed.</summary>
    public const int Passed = 0;

    /// <summary>The exit code of a run where something failed: the result file says what.</summary>
    public const int Failed = 1;

    internal const string VersionLine = "version ";
    internal const string MissingLine = "missing ";
    internal const string LoadingLine = "loading ";
    internal const string LoadedLine = "loaded ";
    internal const string FailedLine = "failed ";
    internal const string DoneLine = "done";

    /// <summary>
    /// How long the probe gives itself. It ends itself after this, on both OSes (2026-11-04, review P5-13): the app's own 30 s kill dies with the app, and a Mac has no job object, so a hung probe outlived a killed app, holding its stage's files. Longer than the app's limit, so the app's answer comes first.
    /// </summary>
    public static readonly TimeSpan SelfLimit = TimeSpan.FromSeconds(60);

#if UPDATE_SIM
    /// <summary>A file beside a simulation build's exe that makes its probe hang, for the 30 s limit's scenario.</summary>
    public const string HangMarker = "probe-hang";

    /// <summary>A file beside a simulation build's exe that makes its probe crash after setting its error mode, for the no-box scenario.</summary>
    public const string CrashMarker = "probe-crash";

    /// <summary>What a simulation build's probe writes between its version line and its last, which <c>build.ps1 -Tag</c> refuses (2026-11-04, review P4-3): such a package must never become a release. Any other reader ignores a line it doesn't know.</summary>
    public const string SimBuildLine = "update-sim-build";
#endif

    /// <summary>Whether this run is the probe.</summary>
    public static bool Asked(IReadOnlyList<string> args) =>
        args is { Count: 2 } && string.Equals(args[0], Flag, StringComparison.Ordinal);

    /// <summary>The real run, from <c>Program.cs</c>.</summary>
    public static int Run(string resultFile)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = JobObjectNative.SetErrorMode(JobObjectNative.FailCriticalErrors | JobObjectNative.NoGpFaultErrorBox);
        }

        Watchdog(SelfLimit, EndNow);
        string directory = AppContext.BaseDirectory;
        string version = SidekickApp.Version;
#if UPDATE_SIM
        // One binary has one version, and UpdateSim rewrites and signs manifests for the others. The unit test on the probe's output and the last Verification step hold the real behavior.
        if (UpdateManifest.ReadFile(Path.Combine(directory, UpdateManifest.FileName), out _) is { } staged)
        {
            version = staged.Version;
        }

        if (File.Exists(Path.Combine(directory, CrashMarker)))
        {
            Environment.FailFast("A simulated probe crash.");
        }

        if (File.Exists(Path.Combine(directory, HangMarker)))
        {
            Thread.Sleep(Timeout.Infinite);
        }
#endif

        return RunHere(resultFile, directory, version, Libraries(out var existOnly), [.. existOnly, .. SmokeChecks.RequiredContentFiles], library => NativeLibrary.TryLoad(library, out _), Thread.Sleep);
    }

    /// <summary>
    /// The probe with its own manifest check, the seam a test drives (2026-11-04, review P21-12: a test handing <see cref="Run(string, string, string, IReadOnlyList{string}, IReadOnlyList{string}, Func{string, bool}, UpdateProbeManifest)"/> an answer couldn't fail for a wrong check). With the app's own reader (review P2-14): a manifest this build can't read is a package no copy could later update from, and build.ps1's own checks would pass it.
    /// </summary>
    internal static int RunHere(string resultFile, string directory, string version, IReadOnlyList<string> libraries, IReadOnlyList<string> files, Func<string, bool> load, Action<TimeSpan> pause)
    {
        var read = UpdateManifest.LoadRetrying(Path.Combine(directory, UpdateManifest.FileName), pause);
        var manifest = read.Unreadable ? UpdateProbeManifest.Unreadable : read.Manifest is null ? UpdateProbeManifest.Bad : UpdateProbeManifest.Good;
        return Run(resultFile, directory, version, libraries, files, load, manifest);
    }

    /// <summary>Starts a background thread that calls <paramref name="end"/> after <paramref name="limit"/>; it takes both as arguments so a test drives it with a harmless ending.</summary>
    internal static Thread Watchdog(TimeSpan limit, Action end)
    {
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(limit);
            end();
        })
        { IsBackground = true, Name = "Update probe's limit" };
        watchdog.Start();
        return watchdog;
    }

    private static void EndNow()
    {
        if (OperatingSystem.IsWindows())
        {
            ProcessEnd.Now(unchecked((uint)Failed));
        }

        using var self = Process.GetCurrentProcess();
        self.Kill();
    }

    /// <summary>The probe over its seams: writes the version, then what's missing, then each load.</summary>
    public static int Run(string resultFile, string directory, string version, IReadOnlyList<string> libraries, IReadOnlyList<string> files, Func<string, bool> load, UpdateProbeManifest manifest = UpdateProbeManifest.Good)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(load);
        using var writer = new StreamWriter(new FileStream(resultFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true, NewLine = "\n" };
        writer.WriteLine(VersionLine + version);
#if UPDATE_SIM
        writer.WriteLine(SimBuildLine);
#endif
        bool passed = manifest == UpdateProbeManifest.Good;
        if (manifest != UpdateProbeManifest.Good)
        {
            writer.WriteLine((manifest == UpdateProbeManifest.Unreadable ? FailedLine : MissingLine) + UpdateManifest.FileName);
        }

        foreach (string file in files.Concat(libraries))
        {
            if (!File.Exists(Path.Combine(directory, file)))
            {
                writer.WriteLine(MissingLine + file);
                passed = false;
            }
        }

        foreach (string library in libraries)
        {
            string full = Path.Combine(directory, library);
            if (!File.Exists(full))
            {
                continue;
            }

            writer.WriteLine(LoadingLine + library);
            bool loaded = load(full);
            writer.WriteLine((loaded ? LoadedLine : FailedLine) + library);
            passed &= loaded;
        }

        writer.WriteLine(DoneLine);
        return passed ? Passed : Failed;
    }

    /// <summary>The libraries the probe loads, and through <paramref name="existOnly"/> the ones it only looks for.</summary>
    public static string[] Libraries(out string[] existOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            existOnly = [];
            return SmokeChecks.RequiredNativeLibraries;
        }

        string whisper = Path.Combine("runtimes", "win-x64");
        string noAvx = Path.Combine("runtimes", "noavx");
        bool IsWhisper(string library) => library.StartsWith(whisper, StringComparison.OrdinalIgnoreCase) || library.StartsWith(noAvx, StringComparison.OrdinalIgnoreCase);
        existOnly = [.. SmokeChecks.RequiredNativeLibraries.Where(IsWhisper)];
        return [.. SmokeChecks.RequiredNativeLibraries.Where(library => !IsWhisper(library))];
    }
}

/// <summary>What a probe's result file says.</summary>
/// <param name="Version">The version it reports, or null if the file holds none.</param>
/// <param name="Missing">Files that aren't there: a package's fault, never the machine's.</param>
/// <param name="FailedLoads">Libraries that wouldn't load.</param>
/// <param name="CrashedOn">The library it was loading when it stopped with no answer.</param>
/// <param name="Finished">True when the run reached its end, so a crash can't pass for a clean file.</param>
public sealed record UpdateProbeResult(string? Version, IReadOnlyList<string> Missing, IReadOnlyList<string> FailedLoads, string? CrashedOn, bool Finished)
{
    /// <summary>The libraries that failed or crashed, which the installed copy's own run is asked about.</summary>
    public IEnumerable<string> Suspects => CrashedOn is null ? FailedLoads : [.. FailedLoads, CrashedOn];

    /// <summary>Reads the file's text; no file is the same as an empty one, and each run deletes its own result first, so an older run's answer can't be read as a crash's.</summary>
    public static UpdateProbeResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? version = null;
        string? loading = null;
        bool finished = false;
        var missing = new List<string>();
        var failed = new List<string>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(UpdateProbe.VersionLine, StringComparison.Ordinal))
            {
                version = line[UpdateProbe.VersionLine.Length..];
            }
            else if (line.StartsWith(UpdateProbe.MissingLine, StringComparison.Ordinal))
            {
                missing.Add(line[UpdateProbe.MissingLine.Length..]);
            }
            else if (line.StartsWith(UpdateProbe.LoadingLine, StringComparison.Ordinal))
            {
                loading = line[UpdateProbe.LoadingLine.Length..];
            }
            else if (line.StartsWith(UpdateProbe.LoadedLine, StringComparison.Ordinal))
            {
                loading = null;
            }
            else if (line.StartsWith(UpdateProbe.FailedLine, StringComparison.Ordinal))
            {
                failed.Add(line[UpdateProbe.FailedLine.Length..]);
                loading = null;
            }
            else if (line == UpdateProbe.DoneLine)
            {
                finished = true;
            }
        }

        return new UpdateProbeResult(version, missing, failed, finished ? null : loading, finished);
    }

    /// <summary>The file at <paramref name="path"/>, read: a missing or unreadable one is empty.</summary>
    public static UpdateProbeResult ParseFile(string path)
    {
        try
        {
            return Parse(UpdateFolder.ReadCappedText(path) ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Parse("");
        }
    }
}
