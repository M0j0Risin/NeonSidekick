using NeonSidekick.Settings;

namespace NeonSidekick.EmbeddedLlm;

/// <summary>A catalog row's fit mark (2026-11-16, plan lucid-roaming-lantern #43): whether the model's files and some context fit the GPU.</summary>
public enum FitMark
{
    /// <summary>✅ The files and a working context fit.</summary>
    Fits,

    /// <summary>⚠️ The files fit with a small context only, about llama.cpp's 4096-token floor.</summary>
    SmallContext,

    /// <summary>✗ Every layer is held on the GPU (<c>Embedded VRAM only</c>, or <c>Embedded GPU layers</c> <c>all</c>) and the files don't fit.</summary>
    WontFit,

    /// <summary>🐢 The files don't fit, so llama.cpp's fit puts some layers on the CPU: it runs, slower.</summary>
    PartlyCpu,
}

/// <summary>Why a list gives no fit marks (2026-11-16, #43): the caption says it in their place.</summary>
public enum FitNone
{
    /// <summary>The CPU backend: there is no GPU to fit.</summary>
    Cpu,

    /// <summary>No GPU memory was read (or a read of 0, a driver that reports nothing).</summary>
    NoRead,

    /// <summary><c>Embedded GPU layers</c> a count with <c>Embedded VRAM only</c> off: the count, not a fit, places the layers.</summary>
    LayerCount,
}

/// <summary>
/// Whether an embedded model fits, before the download (2026-11-16, plan lucid-roaming-lantern #43, the user's pick of a fixed allowance
/// over a per-build KV figure from each GGUF's header): the files a start would load — the weights, the projector when <c>Embedded vision</c>
/// is on and pictures are readable here, the drafter with <c>Embedded drafter</c> on — against the GPU's usable memory: its total less
/// llama.cpp's own 1 GiB margin, or under <c>Embedded VRAM budget</c> the share the launch's <c>--fit-target</c> leaves
/// (<see cref="EmbeddedVramBudget.FitTargetMiB"/>, so the two agree to the MiB). A working context is a fixed
/// <see cref="WorkingContextBytes"/>; a small one <see cref="SmallContextBytes"/>. The read is the GPU's total, not what is free, so the
/// marks are for the GPU with nothing else on it (the recommended choice: what is in use now would count the app's own loaded model against
/// its replacement, reads nothing while the perf bar is off, and moves with every browser tab). The settings are read through the launch's
/// own helpers, so a hand-edited bad value marks as the default it launches with. Pure.
/// </summary>
public static class EmbeddedFit
{
    /// <summary>The working context's allowance: 3 GiB, the user's pick. Pinned.</summary>
    public const long WorkingContextBytes = 3L << 30;

    /// <summary>A small context's allowance: 0.75 GiB, about llama.cpp's 4096-token fit floor. Pinned.</summary>
    public const long SmallContextBytes = 768L << 20;

    /// <summary>llama.cpp's own fit margin per device with the budget off: 1 GiB.</summary>
    public const long LlamaMarginBytes = 1L << 30;

    /// <summary>
    /// Why no mark can be given under <paramref name="effective"/>, else null: the CPU backend, no GPU read (null, 0 or less, as the
    /// budget treats it), or a layer count with <c>Embedded VRAM only</c> off (on, the launch passes <c>all</c> whatever the count).
    /// </summary>
    public static FitNone? WhyNone(AppSettingsData effective, long? gpuBytes, LlamaBackend backend)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (backend == LlamaBackend.Cpu)
        {
            return FitNone.Cpu;
        }

        if (gpuBytes is not > 0)
        {
            return FitNone.NoRead;
        }

        return Layers(effective) is EmbeddedGpuLayers.Auto or EmbeddedGpuLayers.All ? null : FitNone.LayerCount;
    }

    /// <summary>Whether a mark can be given here: the filter's <c>size: fits</c> stop is offered only then.</summary>
    public static bool CanMark(AppSettingsData effective, long? gpuBytes, LlamaBackend backend) => WhyNone(effective, gpuBytes, backend) is null;

    /// <summary><paramref name="model"/>'s mark under <paramref name="effective"/> on a GPU of <paramref name="gpuBytes"/>; null where <see cref="WhyNone"/> says.</summary>
    public static FitMark? Mark(EmbeddedModel model, AppSettingsData effective, long? gpuBytes, LlamaBackend backend)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (WhyNone(effective, gpuBytes, backend) is not null)
        {
            return null;
        }

        long usable = Usable(gpuBytes!.Value, effective);
        long weights = WeightBytes(model, effective);
        if (weights + WorkingContextBytes <= usable)
        {
            return FitMark.Fits;
        }

        if (weights + SmallContextBytes <= usable)
        {
            return FitMark.SmallContext;
        }

        return Layers(effective) == EmbeddedGpuLayers.All ? FitMark.WontFit : FitMark.PartlyCpu;
    }

    /// <summary>What a start may fill of <paramref name="totalBytes"/>: less the budget's <c>--fit-target</c> when it is on, else less llama.cpp's 1 GiB.</summary>
    public static long Usable(long totalBytes, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        int budget = EmbeddedVramBudget.Effective(effective.EmbeddedVramBudget);
        long margin = budget == EmbeddedVramBudget.Off ? LlamaMarginBytes : (long)EmbeddedVramBudget.FitTargetMiB(totalBytes, budget) << 20;
        return totalBytes - margin;
    }

    /// <summary>
    /// The files a start loads (the parts <see cref="EmbeddedModelCatalog.TotalBytes"/> sums): the weights; the projector with
    /// <c>Embedded vision</c> on where pictures are readable (<see cref="Files.ImageCodecs.Available"/>, as the launch decides); the drafter
    /// with <c>Embedded drafter</c> on.
    /// </summary>
    public static long WeightBytes(EmbeddedModel model, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(effective);
        long bytes = model.Model.Bytes;
        if (effective.EmbeddedVision && Files.ImageCodecs.Available)
        {
            bytes += model.Mmproj.Bytes;
        }

        if (effective.EmbeddedDrafter && model.HasMtp)
        {
            bytes += model.Drafter?.Bytes ?? 0;
        }

        return bytes;
    }

    /// <summary>Whether <paramref name="effective"/> fixes the context length, so the caption adds that the marks are for a fitted one.</summary>
    public static bool FixedContext(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return EmbeddedContextSize.Effective(effective.EmbeddedContextSize) != EmbeddedContextSize.Default;
    }

    /// <summary>The layers the launch passes: <c>all</c> under <c>Embedded VRAM only</c>, else the setting's effective value.</summary>
    private static string Layers(AppSettingsData effective) =>
        effective.EmbeddedVramOnly ? EmbeddedGpuLayers.All : EmbeddedGpuLayers.Effective(effective.EmbeddedGpuLayers);
}
