using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>
/// Which llama.cpp build runs the embedded model: NVIDIA's CUDA, any GPU's Vulkan, or the CPU alone on Windows; Metal on an
/// Apple Silicon Mac (2026-10-07, last so the Windows values keep their numbers).
/// </summary>
public enum LlamaBackend
{
    Cpu,
    Vulkan,
    Cuda,
    Metal,
}

/// <summary>One archive of a llama.cpp release (a zip, or the Mac's tar.gz): its asset name, exact size and SHA-256 (lowercase hex).</summary>
public sealed record LlamaAsset(string Name, long Bytes, string Sha256);

/// <summary>
/// The llama.cpp build the embedded model runs on (2026-09-29; b12262 since 2026-11-12), pinned by tag, asset name, size and SHA-256 and downloaded
/// on first use into <c>&lt;home&gt;/llama/&lt;tag&gt;-&lt;backend&gt;/</c> — never shipped beside the exe (the CUDA
/// runtime alone is 420 MB). Pinning, not "latest", because the numbered builds are llama.cpp's pre-releases (its
/// <c>/releases/latest</c> is a tag with no zips at all) and because flags and asset names do drift.
///
/// <para>To move to a newer build: <c>gh api repos/ggml-org/llama.cpp/releases/tags/&lt;tag&gt;</c> lists
/// <c>.assets[]</c> with <c>name</c>, <c>size</c> and <c>digest</c> (<c>sha256:…</c>); update <see cref="Tag"/> and
/// the three assets per backend below, check the zips still hold <see cref="RequiredFiles"/> (the CUDA major version is
/// in the runtime's DLL names), and run the gated live test. The previous build's folder is pruned after the new one
/// installs (<see cref="EmbeddedModels.PruneOldRuntimes"/>).</para>
///
/// <para>Windows x64 only (<see cref="EmbeddedEndpoint.Offered"/>). CUDA is the 13.x build: its kernels cover Blackwell
/// (the 12.4 build predates sm_120), and it needs driver 580 or newer (<see cref="LlamaBackendDetect.MinimumCudaDriver"/>).
/// Its runtime DLLs come in a second zip without a build number in its name, unpacked into the same folder. Every zip
/// is flat.</para>
///
/// <para>The release's other builds are left out on purpose (2026-10-01, the user's call). ROCm/HIP
/// (<c>win-rocm-10.0-x64</c>): Vulkan already runs AMD cards at comparable speed, while ROCm's zip is 257 MB to download
/// and about 1.2 GB unpacked (a 993 MB <c>ggml-hip.dll</c> with its own <c>amdhip64_7.dll</c>), is built for RDNA1–4
/// (gfx101x–gfx120x) only, and a file check cannot tell those from a GCN/Vega card or an iGPU. SYCL and OpenVINO (Intel):
/// Vulkan covers Arc, and SYCL alone carries about 400 MB of MKL/oneDNN. CUDA 12.4: a driver older than 580 already falls
/// to Vulkan. The Windows arm64 builds: <see cref="EmbeddedEndpoint.Offered"/> is x64 there.</para>
///
/// <para>Metal (2026-10-07, the Mac port's Stage 2): the <c>macos-arm64</c> tarball is the Metal build, the only one a Mac
/// gets (<see cref="LlamaBackendDetect"/>). Unlike the zips it is a <c>.tar.gz</c> with one top folder (<c>llama-b12262/</c>,
/// which <see cref="ModelStore"/> unwraps), because a Mac build needs what a zip drops: the executable bits and the
/// <c>lib*.0.dylib</c> symlinks. <c>llama-server</c> loads its libraries as <c>@rpath/lib*.0.dylib</c> with rpath
/// <c>@loader_path</c>, and those names are links to the versioned files — so <see cref="RequiredFiles"/> names both, a
/// dangling link being something .NET's <c>File.Exists</c> still counts. The binaries arrive with an ad-hoc linker
/// signature and the download carries no quarantine flag, so they run as unpacked (checked that day on an M4: no
/// <c>codesign</c>, no <c>xattr</c>). The shaders are inside <c>libggml-metal</c>; there is no <c>.metallib</c> to ship.</para>
/// </summary>
public static class LlamaRelease
{
    /// <summary>The pinned build.</summary>
    public const string Tag = "b12262";

    /// <summary>Where the assets are downloaded from: <c>&lt;base&gt;&lt;tag&gt;/&lt;asset&gt;</c>.</summary>
    public const string DownloadBase = "https://github.com/ggml-org/llama.cpp/releases/download/";

    /// <summary>The Windows builds' server executable, in the runtime folder's root.</summary>
    public const string ServerExecutable = "llama-server.exe";

    /// <summary>The Metal build's server executable (2026-10-07): no extension on a Mac.</summary>
    public const string MacServerExecutable = "llama-server";

    private static readonly LlamaAsset CpuZip = new("llama-b12262-bin-win-cpu-x64.zip", 25_044_988, "7d28975f1acc6c142954c4a0d1dfb9c3a3a13736c41f898890748cc067487d7a");
    private static readonly LlamaAsset VulkanZip = new("llama-b12262-bin-win-vulkan-x64.zip", 38_007_544, "07717635401eb384ef640c0ebd7d8d2abd17d73e69023930d62149d2d528344a");
    private static readonly LlamaAsset CudaZip = new("llama-b12262-bin-win-cuda-13.5-x64.zip", 171_015_108, "86354c7c12be7c4912990e309762f51ff44f9f0a6007442fc4aa11fbe2cb936d");
    private static readonly LlamaAsset CudaRuntimeZip = new("cudart-llama-bin-win-cuda-13.5-x64.zip", 426_314_199, "c2bf4cc202bfbaaa7a0b75f5082457c3811326947bb216059b72839d844e9cba");
    private static readonly LlamaAsset MetalTarball = new("llama-b12262-bin-macos-arm64.tar.gz", 16_857_745, "c52efb2ff664010329d00a6b88aa253ecb50096999201a69838ae28211161f0a");

    /// <summary>The files every backend's folder must hold: the server (a 9 KB launcher and its implementation), llama, ggml and the multimodal library.</summary>
    private static readonly string[] CommonFiles = [ServerExecutable, "llama-server-impl.dll", "llama.dll", "ggml.dll", "ggml-base.dll", "mtmd.dll"];

    /// <summary>
    /// The Metal build's files (2026-10-07): the server, its implementation and every library it loads, each as the
    /// versioned file and as the <c>.0</c> link dyld asks for (the tarball also holds the unversioned links and the other
    /// tools, which nothing here needs).
    /// </summary>
    private static readonly string[] MetalFiles =
    [
        MacServerExecutable, "libllama-server-impl.dylib",
        "libllama-common.0.dylib", "libllama-common.0.9.0.dylib",
        "libllama.0.dylib", "libllama.0.9.0.dylib",
        "libmtmd.0.dylib", "libmtmd.0.9.0.dylib",
        "libggml.0.dylib", "libggml.0.29.0.dylib",
        "libggml-base.0.dylib", "libggml-base.0.29.0.dylib",
        "libggml-cpu.0.dylib", "libggml-cpu.0.29.0.dylib",
        "libggml-blas.0.dylib", "libggml-blas.0.29.0.dylib",
        "libggml-metal.0.dylib", "libggml-metal.0.29.0.dylib",
        "libggml-rpc.0.dylib", "libggml-rpc.0.29.0.dylib",
    ];

    /// <summary>The archives <paramref name="backend"/> needs, in download order.</summary>
    public static IReadOnlyList<LlamaAsset> Assets(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Metal => [MetalTarball],
        LlamaBackend.Cuda => [CudaZip, CudaRuntimeZip],
        LlamaBackend.Vulkan => [VulkanZip],
        _ => [CpuZip],
    };

    /// <summary>The files whose presence means <paramref name="backend"/>'s folder is complete.</summary>
    public static IReadOnlyList<string> RequiredFiles(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Metal => MetalFiles,
        LlamaBackend.Cuda => [.. CommonFiles, "ggml-cuda.dll", "cudart64_13.dll", "cublas64_13.dll", "cublasLt64_13.dll"],
        LlamaBackend.Vulkan => [.. CommonFiles, "ggml-vulkan.dll"],
        _ => CommonFiles,
    };

    /// <summary>The download URL of <paramref name="asset"/>.</summary>
    public static Uri Url(LlamaAsset asset) => new(DownloadBase + Tag + "/" + asset.Name);

    /// <summary>What <paramref name="backend"/>'s runtime downloads.</summary>
    public static long Bytes(LlamaBackend backend) => Assets(backend).Sum(a => a.Bytes);

    /// <summary>The backend's lowercase name, as the folder, the setting and the status line spell it.</summary>
    public static string Name(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Metal => "metal",
        LlamaBackend.Cuda => "cuda",
        LlamaBackend.Vulkan => "vulkan",
        _ => "cpu",
    };

    /// <summary>The folder <paramref name="backend"/>'s runtime lives in: <c>&lt;llamaDirectory&gt;/&lt;tag&gt;-&lt;backend&gt;</c>.</summary>
    public static string Folder(string llamaDirectory, LlamaBackend backend) => Path.Combine(llamaDirectory, Tag + "-" + Name(backend));

    /// <summary>The server executable's file name in <paramref name="backend"/>'s folder: <c>llama-server</c> for Metal, <c>llama-server.exe</c> else.</summary>
    public static string ServerFile(LlamaBackend backend) => backend == LlamaBackend.Metal ? MacServerExecutable : ServerExecutable;

    /// <summary>The server executable of <paramref name="backend"/>'s runtime.</summary>
    public static string Executable(string llamaDirectory, LlamaBackend backend) => Path.Combine(Folder(llamaDirectory, backend), ServerFile(backend));

    /// <summary>Whether <paramref name="backend"/>'s runtime is installed and complete.</summary>
    public static bool Installed(string llamaDirectory, LlamaBackend backend) =>
        ModelStore.IsCompleteModelDirectory(Folder(llamaDirectory, backend), RequiredFiles(backend));

    /// <summary>The runtime as a <see cref="ModelStore"/> archive set.</summary>
    public static ArchiveSetSpec Spec(string llamaDirectory, LlamaBackend backend) => new(
        EmbeddedLlmText.RuntimeDisplay(backend),
        Folder(llamaDirectory, backend),
        Assets(backend).Select(a => new ArchivePart(a.Name, Url(a), a.Bytes, a.Sha256)).ToList(),
        RequiredFiles(backend));
}
