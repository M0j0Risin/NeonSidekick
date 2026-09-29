using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>Which llama.cpp build runs the embedded model: NVIDIA's CUDA, any GPU's Vulkan, or the CPU alone.</summary>
public enum LlamaBackend
{
    Cpu,
    Vulkan,
    Cuda,
}

/// <summary>One zip of a llama.cpp release: its asset name, exact size and SHA-256 (lowercase hex).</summary>
public sealed record LlamaAsset(string Name, long Bytes, string Sha256);

/// <summary>
/// The llama.cpp build the embedded model runs on (2026-09-29), pinned by tag, asset name, size and SHA-256 and downloaded
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
/// </summary>
public static class LlamaRelease
{
    /// <summary>The pinned build.</summary>
    public const string Tag = "b11258";

    /// <summary>Where the assets are downloaded from: <c>&lt;base&gt;&lt;tag&gt;/&lt;asset&gt;</c>.</summary>
    public const string DownloadBase = "https://github.com/ggml-org/llama.cpp/releases/download/";

    /// <summary>The server executable, in the runtime folder's root.</summary>
    public const string ServerExecutable = "llama-server.exe";

    private static readonly LlamaAsset CpuZip = new("llama-b11258-bin-win-cpu-x64.zip", 19_164_531, "ba9380f6d4bef30c5fd3b81ae560e32d923865f73ab0be8456f5ee75cb2a6611");
    private static readonly LlamaAsset VulkanZip = new("llama-b11258-bin-win-vulkan-x64.zip", 33_079_326, "2a1c64ccdfad23a0d6ddfaead5a9d4a3f65cbea41eafb7c0a665e5a7b877cec6");
    private static readonly LlamaAsset CudaZip = new("llama-b11258-bin-win-cuda-13.4-x64.zip", 153_546_576, "9b5000e468889662b4c815a5f2a58dc57c027e511371ebf7af741f5d0e9561f5");
    private static readonly LlamaAsset CudaRuntimeZip = new("cudart-llama-bin-win-cuda-13.4-x64.zip", 423_535_356, "738f8c251ac22b70c3ae6f83a10cf222725df0395246a2cf58f32bdb85fbe668");

    /// <summary>The files every backend's folder must hold: the server (a 9 KB launcher and its implementation), llama, ggml and the multimodal library.</summary>
    private static readonly string[] CommonFiles = [ServerExecutable, "llama-server-impl.dll", "llama.dll", "ggml.dll", "ggml-base.dll", "mtmd.dll"];

    /// <summary>The zips <paramref name="backend"/> needs, in download order.</summary>
    public static IReadOnlyList<LlamaAsset> Assets(LlamaBackend backend) => backend switch
    {
        LlamaBackend.Cuda => [CudaZip, CudaRuntimeZip],
        LlamaBackend.Vulkan => [VulkanZip],
        _ => [CpuZip],
    };

    /// <summary>The files whose presence means <paramref name="backend"/>'s folder is complete.</summary>
    public static IReadOnlyList<string> RequiredFiles(LlamaBackend backend) => backend switch
    {
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
        LlamaBackend.Cuda => "cuda",
        LlamaBackend.Vulkan => "vulkan",
        _ => "cpu",
    };

    /// <summary>The folder <paramref name="backend"/>'s runtime lives in: <c>&lt;llamaDirectory&gt;/&lt;tag&gt;-&lt;backend&gt;</c>.</summary>
    public static string Folder(string llamaDirectory, LlamaBackend backend) => Path.Combine(llamaDirectory, Tag + "-" + Name(backend));

    /// <summary>The server executable of <paramref name="backend"/>'s runtime.</summary>
    public static string Executable(string llamaDirectory, LlamaBackend backend) => Path.Combine(Folder(llamaDirectory, backend), ServerExecutable);

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
