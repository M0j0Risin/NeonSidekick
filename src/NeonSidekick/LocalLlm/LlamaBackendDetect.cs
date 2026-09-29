using System.Diagnostics;
using System.Globalization;

namespace NeonSidekick.LocalLlm;

/// <summary>The backend a start will use, and why — the <c>Local backend</c> row and the log both say it.</summary>
public sealed record BackendChoice(LlamaBackend Backend, string Reason);

/// <summary>
/// The <c>Local backend</c> setting's values (2026-09-29): <c>auto</c> (the default) or one backend forced.
/// </summary>
public static class LocalBackends
{
    public const string Auto = "auto";

    /// <summary>The picker's rows, in order.</summary>
    public static readonly string[] Names = [Auto, "cuda", "vulkan", "cpu"];

    /// <summary>The settings-menu wording for a bad value. Pinned.</summary>
    public const string Error = "must be auto, cuda, vulkan or cpu";

    /// <summary>The forced backend a setting names (any case), or null for <c>auto</c> — and for anything unknown, which reads as auto.</summary>
    public static LlamaBackend? Forced(string? setting) => (setting ?? "").Trim().ToLowerInvariant() switch
    {
        "cuda" => LlamaBackend.Cuda,
        "vulkan" => LlamaBackend.Vulkan,
        "cpu" => LlamaBackend.Cpu,
        _ => null,
    };

    /// <summary>Whether <paramref name="setting"/> is one of <see cref="Names"/> (any case).</summary>
    public static bool IsValid(string? setting) => Names.Contains((setting ?? "").Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Chooses the llama.cpp build for this machine (2026-09-29, the user's call: auto, with a setting to force one).
/// <c>auto</c> is CUDA when an NVIDIA driver new enough for CUDA 13 is installed, else Vulkan when the Vulkan loader
/// is, else the CPU. It looks at files only — <c>System32\nvcuda.dll</c> and its version, <c>System32\vulkan-1.dll</c>
/// — never loads a driver into this process and never starts one, so asking costs nothing and cannot crash.
///
/// <para>The driver version sits in <c>nvcuda.dll</c>'s file version: 32.0.16.1088 is driver 610.88 — the last digit
/// of the third part and the first two of the fourth make the major, the last two the minor.</para>
///
/// <para>A CUDA start that fails before the server is healthy falls back to Vulkan once, when the setting is
/// <c>auto</c> (<see cref="LlamaServerHost"/>): a driver that is present but broken is not something a file check sees.</para>
/// </summary>
public static class LlamaBackendDetect
{
    /// <summary>The oldest NVIDIA driver CUDA 13 runs on (its release notes: R580).</summary>
    public const int MinimumCudaDriver = 580;

    /// <summary><see cref="Choose(string?, Func{string, bool}, Func{string, string?}, string)"/> over the real machine.</summary>
    public static BackendChoice Choose(string? setting) =>
        Choose(setting, File.Exists, FileVersionOf, Environment.SystemDirectory);

    /// <summary>The backend for <paramref name="setting"/> on a machine whose files <paramref name="exists"/> and <paramref name="fileVersion"/> describe.</summary>
    public static BackendChoice Choose(string? setting, Func<string, bool> exists, Func<string, string?> fileVersion, string systemDirectory)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(fileVersion);
        ArgumentNullException.ThrowIfNull(systemDirectory);

        if (LocalBackends.Forced(setting) is { } forced)
        {
            return new BackendChoice(forced, "forced in settings");
        }

        string nvcuda = Path.Combine(systemDirectory, "nvcuda.dll");
        string vulkan = Path.Combine(systemDirectory, "vulkan-1.dll");
        string? cudaWhyNot = null;
        if (exists(nvcuda))
        {
            var driver = DriverVersion(fileVersion(nvcuda));
            if (driver is { } d && d.Major >= MinimumCudaDriver)
            {
                return new BackendChoice(LlamaBackend.Cuda, "NVIDIA driver " + Format(d));
            }

            cudaWhyNot = driver is { } old
                ? $"NVIDIA driver {Format(old)} is older than {MinimumCudaDriver}"
                : "NVIDIA driver version unreadable";
        }

        if (exists(vulkan))
        {
            return new BackendChoice(LlamaBackend.Vulkan, cudaWhyNot ?? "no NVIDIA driver; Vulkan present");
        }

        return new BackendChoice(LlamaBackend.Cpu, cudaWhyNot is null ? "no GPU driver found" : cudaWhyNot + "; no Vulkan");
    }

    /// <summary>
    /// The NVIDIA driver version inside <c>nvcuda.dll</c>'s file version (<c>32.0.16.1088</c> → 610.88), or null for
    /// anything that is not four numeric parts.
    /// </summary>
    public static (int Major, int Minor)? DriverVersion(string? fileVersion)
    {
        var parts = (fileVersion ?? "").Trim().Split('.');
        if (parts.Length != 4
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int build)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int revision))
        {
            return null;
        }

        return ((build % 10) * 100 + revision / 100, revision % 100);
    }

    private static string Format((int Major, int Minor) driver) =>
        string.Create(CultureInfo.InvariantCulture, $"{driver.Major}.{driver.Minor:00}");

    private static string? FileVersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Create(CultureInfo.InvariantCulture, $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}");
        }
        catch
        {
            return null;
        }
    }
}
