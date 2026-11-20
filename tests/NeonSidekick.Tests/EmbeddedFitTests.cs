using NeonSidekick.EmbeddedLlm;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Whether an embedded model fits, before the download (2026-11-16, plan lucid-roaming-lantern #43): each mark at its edges, the budget off
/// and on, vision and the drafter counted only when on, no mark on the CPU backend, with no read, a read of 0 or a layer count with
/// <i>Embedded VRAM only</i> off; <c>all</c> marks 🚫 (✗ until 2026-11-20) where <c>auto</c> marks 🐢; the legends, and the note's GPU in whole GB
/// and its fixed-context clause; a 🚫 row's marks in the columns a ✅ row's are.
/// </summary>
public class EmbeddedFitTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;
    private const long Gpu = 24 * GiB;

    private static readonly EmbeddedSampling Sampling = new(1.0, 0.95, 64);

    private static EmbeddedModel Model(long weights, long mmproj = 1_000_000_000, long drafter = 0) =>
        new("m", "M", "Q4", "r/r", "c", new EmbeddedFile("m.gguf", weights, "0"), new EmbeddedFile("mmproj.gguf", mmproj, "0"), Sampling,
            drafter > 0 ? new EmbeddedFile("mtp.gguf", drafter, "0") : null);

    // The plain case: no budget, no projector, no drafter, every layer held on the GPU.
    private static AppSettingsData Plain(bool vramOnly = true, string layers = EmbeddedGpuLayers.Auto) => new()
    {
        EmbeddedVramBudget = EmbeddedVramBudget.Off,
        EmbeddedVision = false,
        EmbeddedDrafter = false,
        EmbeddedVramOnly = vramOnly,
        EmbeddedGpuLayers = layers,
    };

    private static FitMark? Mark(long weights, AppSettingsData effective, long? gpu = Gpu, LlamaBackend backend = LlamaBackend.Cuda) =>
        EmbeddedFit.Mark(Model(weights), effective, gpu, backend);

    [Fact]
    public void EachMark_AtItsEdges_WithTheBudgetOff()
    {
        // Usable: 24 GiB less llama.cpp's 1 GiB.
        Assert.Equal(23 * GiB, EmbeddedFit.Usable(Gpu, Plain()));
        Assert.Equal(FitMark.Fits, Mark(20 * GiB, Plain()));
        Assert.Equal(FitMark.SmallContext, Mark(20 * GiB + 1, Plain()));
        Assert.Equal(FitMark.SmallContext, Mark(23 * GiB - 768 * MiB, Plain()));
        Assert.Equal(FitMark.WontFit, Mark(23 * GiB - 768 * MiB + 1, Plain()));
        Assert.Equal(FitMark.PartlyCpu, Mark(23 * GiB - 768 * MiB + 1, Plain(vramOnly: false)));
    }

    [Fact]
    public void TheBudget_LeavesTheLaunchsFitTarget()
    {
        var budget = Plain();
        budget.EmbeddedVramBudget = 89;
        long usable = (24576 - EmbeddedVramBudget.FitTargetMiB(Gpu, 89)) * MiB;   // 2703 MiB held back
        Assert.Equal(usable, EmbeddedFit.Usable(Gpu, budget));
        Assert.Equal(FitMark.Fits, Mark(usable - 3 * GiB, budget));
        Assert.Equal(FitMark.SmallContext, Mark(usable - 3 * GiB + 1, budget));

        budget.EmbeddedVramBudget = 7;   // a bad value is off, as the launch reads it
        Assert.Equal(23 * GiB, EmbeddedFit.Usable(Gpu, budget));
    }

    [Fact]
    public void VisionAndTheDrafter_CountOnlyWhenOn()
    {
        var model = Model(10 * GiB, mmproj: 1 * GiB, drafter: 2 * GiB);
        Assert.Equal(10 * GiB, EmbeddedFit.WeightBytes(model, Plain()));

        var vision = Plain();
        vision.EmbeddedVision = true;
        Assert.Equal(Files.ImageCodecs.Available ? 11 * GiB : 10 * GiB, EmbeddedFit.WeightBytes(model, vision));

        var drafter = Plain();
        drafter.EmbeddedDrafter = true;
        Assert.Equal(12 * GiB, EmbeddedFit.WeightBytes(model, drafter));
        Assert.Equal(10 * GiB, EmbeddedFit.WeightBytes(Model(10 * GiB, mmproj: 1 * GiB), drafter));   // no drafter to count

        // The drafter tips a model over: 19 GiB of weights and a working context fit 23, 21 with the drafter only a small one.
        Assert.Equal(FitMark.Fits, EmbeddedFit.Mark(Model(19 * GiB, drafter: 2 * GiB), Plain(), Gpu, LlamaBackend.Cuda));
        Assert.Equal(FitMark.SmallContext, EmbeddedFit.Mark(Model(19 * GiB, drafter: 2 * GiB), drafter, Gpu, LlamaBackend.Cuda));
    }

    [Fact]
    public void NoMark_OnTheCpu_WithNoRead_OrALayerCount_WithVramOnlyOff()
    {
        Assert.Null(Mark(1 * GiB, Plain(), backend: LlamaBackend.Cpu));
        Assert.Null(Mark(1 * GiB, Plain(), gpu: null));
        Assert.Null(Mark(1 * GiB, Plain(), gpu: 0));
        Assert.Null(Mark(1 * GiB, Plain(vramOnly: false, layers: "20")));
        Assert.Equal(FitNone.Cpu, EmbeddedFit.WhyNone(Plain(vramOnly: false), Gpu, LlamaBackend.Cpu));
        Assert.Equal(FitNone.VramOnlyOnCpu, EmbeddedFit.WhyNone(Plain(vramOnly: true), Gpu, LlamaBackend.Cpu));   // nothing launches: no "runs every model"
        Assert.Contains(EmbeddedLlmText.VramOnlyOnCpu, EmbeddedLlmText.FitNoneCaption(FitNone.VramOnlyOnCpu), StringComparison.Ordinal);
        Assert.Equal(FitNone.NoRead, EmbeddedFit.WhyNone(Plain(), -1, LlamaBackend.Vulkan));
        Assert.Equal(FitNone.LayerCount, EmbeddedFit.WhyNone(Plain(vramOnly: false, layers: "20"), Gpu, LlamaBackend.Cuda));


        // VRAM only passes all whatever the count, so the count takes no mark away; a bad value launches as auto.
        Assert.Equal(FitMark.WontFit, Mark(30 * GiB, Plain(vramOnly: true, layers: "20")));
        Assert.Equal(FitMark.PartlyCpu, Mark(30 * GiB, Plain(vramOnly: false, layers: "lots")));
        Assert.Null(EmbeddedFit.WhyNone(Plain(), Gpu, LlamaBackend.Metal));
    }

    [Fact]
    public void All_MarksWontFit_WhereAutoMarksPartlyOnTheCpu()
    {
        Assert.Equal(FitMark.WontFit, Mark(30 * GiB, Plain(vramOnly: false, layers: EmbeddedGpuLayers.All)));
        Assert.Equal(FitMark.PartlyCpu, Mark(30 * GiB, Plain(vramOnly: false, layers: EmbeddedGpuLayers.Auto)));
    }

    [Fact]
    public void TheCaption_NamesTheGpuInWholeGb_AndAFixedContext()
    {
        Assert.Equal("24 GB", EmbeddedLlmText.GpuGb(Gpu));
        Assert.Equal("8 GB", EmbeddedLlmText.GpuGb(8 * GiB - 100 * MiB));
        // The legend in the user's two lines and a dim note under them (2026-11-20, the user's ask; one sentence until then).
        Assert.Equal("✅ fits · ⚠️ small context only · 🚫 won't fit · 🐢 partly on the CPU", EmbeddedLlmText.FitMarksLegend);
        Assert.Equal("⚡ drafter · 👁️ vision · 🛠️ tools · ⛓️‍💥 uncensored", EmbeddedLlmText.CapabilityLegend);
        Assert.Equal("An estimate for this GPU's 24 GB, with nothing else on it.", EmbeddedLlmText.FitEstimateNote(Gpu, fixedContext: false));
        Assert.EndsWith(", with nothing else on it, for a fitted context.", EmbeddedLlmText.FitEstimateNote(Gpu, fixedContext: true));

        var fixedContext = Plain();
        Assert.False(EmbeddedFit.FixedContext(fixedContext));
        fixedContext.EmbeddedContextSize = 32768;
        Assert.True(EmbeddedFit.FixedContext(fixedContext));
        fixedContext.EmbeddedContextSize = 7;   // a bad value launches as fit
        Assert.False(EmbeddedFit.FixedContext(fixedContext));

        Assert.All(Enum.GetValues<FitNone>(), why => Assert.StartsWith("No fit marks: ", EmbeddedLlmText.FitNoneCaption(why)));
        Assert.All(Enum.GetValues<FitMark>(), mark => Assert.Equal(2, TextCells.Width(EmbeddedLlmText.FitGlyph(mark))));   // every mark two cells since 🚫
    }

    [Fact]
    public void AWontFitRow_AndAFitsRow_KeepTheirMarksInOneColumn()
    {
        var model = Model(1 * GiB);
        string fits = EmbeddedLlmText.CapabilityColumns(model, "installed · 1 GB", 20, FitMark.Fits);
        string wont = EmbeddedLlmText.CapabilityColumns(model, "installed · 1 GB", 20, FitMark.WontFit);
        Assert.Equal(At(fits), At(wont));
        Assert.StartsWith(new string(' ', 4 + 2) + "✅", fits, StringComparison.Ordinal);
        Assert.Equal(EmbeddedLlmText.CapabilityColumns(model, "installed · 1 GB", 20), EmbeddedLlmText.CapabilityColumns(model, "installed · 1 GB", 20, null));

        static int At(string row) => TextCells.Width(row[..row.IndexOf(EmbeddedLlmText.VisionGlyph, StringComparison.Ordinal)]);
    }
}
