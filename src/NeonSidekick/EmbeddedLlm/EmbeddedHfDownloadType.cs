using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>How the embedded models' files come down from Hugging Face (<see cref="EmbeddedHfDownloadTypes"/>).</summary>
public enum EmbeddedHfDownloadType
{
    /// <summary>One connection per file, the way every download went until 2026-09-30.</summary>
    Single,

    /// <summary><see cref="ModelStore.ParallelConnections"/> ranged connections per file, stitched together before the checksum.</summary>
    Parallel,
}

/// <summary>
/// The setting <c>Embedded HF download type</c> (2026-09-30, the user's ask and names): <c>parallel</c> — the default — fetches
/// each embedded model file over <see cref="ModelStore.ParallelConnections"/> ranged connections, <c>single</c> over one. Measured
/// that day on the user's line against a 1 GB catalog file: one stream ~112 MB/s, eight ranges ~206 MB/s, <c>hf download</c>
/// ~217 MB/s — the speed is the connections, not the <c>hf</c> executable or a token, so neither is used. The weights, the
/// vision projector and the drafter alone; the llama.cpp runtime (GitHub) and the voice models keep one stream. The
/// <see cref="EmbeddedFilterTypes"/> shape: <see cref="Resolve"/> is the one place the saved word becomes the enum, and a
/// hand-edited value that is neither falls back to <see cref="Default"/> with a warning, once per value.
/// </summary>
public static class EmbeddedHfDownloadTypes
{
    /// <summary>The user's pick. Pinned.</summary>
    public const string Default = "parallel";

    /// <summary>The types in menu order.</summary>
    public static readonly string[] Names = ["single", "parallel"];

    private const string Category = "EmbeddedLlm";

    /// <summary>Trims and ignores case; false (and <see cref="EmbeddedHfDownloadType.Parallel"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out EmbeddedHfDownloadType type)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "single": type = EmbeddedHfDownloadType.Single; return true;
            case "parallel": type = EmbeddedHfDownloadType.Parallel; return true;
            default: type = EmbeddedHfDownloadType.Parallel; return false;
        }
    }

    /// <summary>The menu hint next to a type.</summary>
    public static string Describe(string name) => name switch
    {
        "single" => "one connection per file",
        "parallel" => $"{ModelStore.ParallelConnections} connections per file, about twice as fast",
        _ => "",
    };

    // The last unknown value warned about: every download asks, the log hears once per value.
    private static string? _warned;

    /// <summary>The type in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static EmbeddedHfDownloadType Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.EmbeddedHfDownloadType, out var type))
        {
            return type;
        }

        if (!string.Equals(Interlocked.Exchange(ref _warned, effective.EmbeddedHfDownloadType), effective.EmbeddedHfDownloadType, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category,
                $"{nameof(AppSettingsData.EmbeddedHfDownloadType)}='{effective.EmbeddedHfDownloadType}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return EmbeddedHfDownloadType.Parallel;
    }

    /// <summary>The connections per file <paramref name="effective"/> asks for: 1 for <c>single</c>, <see cref="ModelStore.ParallelConnections"/> for <c>parallel</c>.</summary>
    public static int Connections(AppSettingsData effective) =>
        Resolve(effective) == EmbeddedHfDownloadType.Single ? 1 : ModelStore.ParallelConnections;
}
