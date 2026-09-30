using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Comfy;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>ComfyUI add workflow</c> and <c>ComfyUI workflows offered</c> (later on 2026-09-24, the user's ask: "a multiselect
/// setting to select which comfyui workflows to offer the model … and a wizard function to add new comfy workflows
/// similar to how we did it for SQL connections"). The wizard is <c>SQL add connection</c>'s shape — one page per
/// choice, every row of the draft on each page, ESC one back, a summary that tests the unsaved draft and saves it — over
/// two sources (the user's pick, "Both"): <b>build</b> a standard workflow from what the server has (its checkpoints,
/// samplers and schedulers, <see cref="ComfyGraphs"/>), or <b>import</b> one exported from ComfyUI with Export (API), the
/// placeholders put in for you (<see cref="ComfyImport"/>). The save writes <c>&lt;name&gt;.md</c> and
/// <c>&lt;name&gt;.json</c> into the chosen <c>comfy</c> folder (<see cref="ComfyWorkflowFile.Add"/>).
/// </summary>
internal sealed partial class SettingsMenu
{
    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The value column of the <c>ComfyUI add workflow</c> action row. Pinned.</summary>
    public const string ComfyAddWorkflowLabel = "Enter to start workflow wizard";

    /// <summary>The <c>ComfyUI workflows offered</c> value before the profile narrows it. Pinned.</summary>
    public const string ComfyNotNarrowedLabel = "all (not narrowed)";

    /// <summary>The wizard's rows, one per <see cref="ComfyWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardLabels =
        ["Source", "File", "Kind", "Checkpoint", "Family", "Scope", "Name", "CLIP skip", "Sampler", "Scheduler", "Width", "Height", "Steps", "CFG", "Denoise", "Negative", "Description"];

    public const string ComfyWizardSourceQuestion = "Build a standard workflow from the server's models, or import one you exported from ComfyUI?";
    public const string ComfyWizardFileQuestion = "The exported file's full path (in ComfyUI: Workflow → Export (API)).";
    public const string ComfyWizardKindQuestion = "What it makes.";
    public const string ComfyWizardCheckpointQuestion = "The checkpoint (the model) it loads, from the server's list.";
    public const string ComfyWizardFamilyQuestion = "The model family: it picks the prompt style the model writes in and the defaults below.";
    public const string ComfyWizardScopeQuestion = "Which comfy folder it goes in.";
    public const string ComfyWizardNameQuestion = "Its name: what the model and /imagine call it (" + ComfyWorkflowFile.NameRule + ").";
    public const string ComfyWizardClipSkipQuestion = "CLIP skip: Pony Diffusion XL is trained for 2; most other checkpoints want none.";
    public const string ComfyWizardSamplerQuestion = "The sampler.";
    public const string ComfyWizardSchedulerQuestion = "The scheduler.";
    public static readonly string ComfyWizardWidthQuestion = "The default width in pixels, " + Invariant(ComfyStudio.MinSide) + " to " + Invariant(ComfyStudio.MaxSide) + ".";
    public static readonly string ComfyWizardHeightQuestion = "The default height in pixels, " + Invariant(ComfyStudio.MinSide) + " to " + Invariant(ComfyStudio.MaxSide) + ".";
    public static readonly string ComfyWizardStepsQuestion = "The default sampling steps, 1 to " + Invariant(ComfyStudio.MaxSteps) + ".";
    public const string ComfyWizardCfgQuestion = "The default CFG (guidance), 0 to 30.";
    public const string ComfyWizardDenoiseQuestion = "How much of the input picture to repaint by default, 0 to 1 (0.3 keeps it close, 0.8 changes it a lot).";
    public const string ComfyWizardNegativeQuestion = "The default negative prompt (empty for none).";
    public const string ComfyWizardDescriptionQuestion = "What it is for, in your words (optional): the model reads it to pick a workflow.";
    public const string ComfyWizardSummaryCaption = "Check the workflow: Test runs it once, small, without saving; Enter on a row below changes it.";

    /// <summary>The source picks. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardSourceRows =
        ["build   a standard workflow from the server's checkpoints, samplers and schedulers", "import  a workflow exported from ComfyUI (Export (API)), the placeholders put in for you"];

    /// <summary>The kind picks. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardKindRows = ["text → image", "image → image (restyle an input picture)"];

    /// <summary>The family picks, in <see cref="ComfyFamily"/> order. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardFamilyRows =
    [
        "other        anything else: a plain description",
        "pony         Pony Diffusion XL: score_9 … prefix, source and rating tags, booru tags",
        "sdxl         SDXL (RealVis, famegrid …): phrases and quality tags",
        "flux         Flux: natural-language sentences, no negative",
        "sd15         Stable Diffusion 1.5: tags and weights",
        "illustrious  Illustrious XL / NoobAI: quality tags, then Danbooru tags",
        "juggernaut   Juggernaut XL: photographic sentences, low CFG",
        "flux2        FLUX.2 dev: long prose, hex colours, no negative (split model: import)",
        "flux2klein   FLUX.2 Klein: prose, 4 steps at CFG 1 (split model: import)",
        "krea2        Krea 2 Turbo: one rich paragraph, 8 steps at CFG 1 (split model: import)",
        "zimage       Z-Image Turbo: concise prose, 8 steps at CFG 1 (split model: import)",
        "qwenimage    Qwen Image: long prose, great at text in quotes (split model: import)",
        "sd35         Stable Diffusion 3.5: sentences plus style phrases, a negative",
        "ernie        Ernie Image Turbo: rich prose, text in quotes, 8 steps at CFG 1 (split model: import)",
        "boogu        Boogu Image Turbo: descriptive prose, 4 steps at CFG 1 (split model: import)",
        "longcat      LongCat Image: detailed prose, text in quotes, a negative (split model: import)",
        "hidream      HiDream I1: long detailed prose, a negative on Full (split model: import)",
        "ideogram4    Ideogram 4: a JSON prompt, strong typography (split model: import)",
    ];

    /// <summary>The CLIP skip picks: none, then 2. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardClipSkipRows = ["none", "2"];

    public const string ComfyWizardSaveRow = "Save";
    public const string ComfyWizardSaveOfferedRow = "Save, and offer it to the model";
    public const string ComfyWizardSaveHiddenRow = "Save, hidden from the model until ticked in ComfyUI workflows offered";
    public const string ComfyWizardTestRow = "Test the workflow";
    public const string ComfyWizardCancelRow = "Cancel";
    public const string ComfyWizardCancelledNotice = "No workflow added.";
    public const string ComfyWizardNotNeeded = "(not needed)";
    public const string ComfyWizardUnset = "—";

    /// <summary>The prompt the Test runs, at most <see cref="ComfyWizardTestSide"/> square and <see cref="ComfyWizardTestSteps"/> steps.</summary>
    public const string ComfyWizardTestPrompt = "a test picture";
    public const int ComfyWizardTestSide = 512;
    public const int ComfyWizardTestSteps = 8;

    /// <summary>The warning when Build is given a family that usually loads as split files (later still on 2026-09-24). Pinned.</summary>
    public static string ComfyWizardSplitWarning(string family) =>
        $"{family} models usually load as separate files (a diffusion model, a text encoder, a VAE), which Build cannot wire: it works only for an all-in-one checkpoint. For the others, export ComfyUI's own template with Export (API) and Import it.";

    public static string ComfyWizardFileError(string path, string problem) => $"'{path}' {problem}.";

    public static string ComfyWizardNumberError(string typed, string range) => $"'{typed}' is not {range}.";

    public static string ComfyWizardFoundNotice(string found) => "Found " + found;

    public static string ComfyWizardTestOkNotice(string name, double seconds, IReadOnlyList<string> files) =>
        $"Tested '{name}' in {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s: ComfyUI saved " + string.Join(", ", files) + ".";

    public static string ComfyWorkflowAdded(string name, string folder) => $"Added workflow '{name}' to {folder}.";

    public static string ComfyWorkflowAddFailed(string name, string why) => $"Could not add workflow '{name}': {why}";

    /// <summary>The step a wizard page asks; the summary last. The rows of <see cref="ComfyWizardLabels"/> by index.</summary>
    internal enum ComfyWizardStep
    {
        Source,
        File,
        Kind,
        Checkpoint,
        Family,
        Scope,
        Name,
        ClipSkip,
        Sampler,
        Scheduler,
        Width,
        Height,
        Steps,
        Cfg,
        Denoise,
        Negative,
        Description,
        Summary,
    }

    /// <summary>What the wizard has so far. A null number or text is "the default": the import's value, or the family's.</summary>
    private sealed class ComfyDraft
    {
        public bool Import { get; set; }

        public string File { get; set; } = "";

        public ComfyImportResult? Imported { get; set; }

        public bool FromImage { get; set; }

        public string Checkpoint { get; set; } = "";

        public ComfyFamily Family { get; set; }

        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public int? ClipSkip { get; set; }

        public string? Sampler { get; set; }

        public string? Scheduler { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? Steps { get; set; }

        public double? Cfg { get; set; }

        public double? Denoise { get; set; }

        public string? Negative { get; set; }

        public string Description { get; set; } = "";

        // The server's lists, read once a visit.
        public IReadOnlyList<string>? Checkpoints { get; set; }

        public IReadOnlyList<string>? Samplers { get; set; }

        public IReadOnlyList<string>? Schedulers { get; set; }

        public bool TakesImage => Import ? Imported?.TakesImage == true : FromImage;

        public int EffClipSkip => ClipSkip ?? ComfyGraphs.DefaultClipSkip(Family);

        public string EffSampler => Sampler ?? ComfyGraphs.DefaultSampler(Family);

        public string EffScheduler => Scheduler ?? ComfyGraphs.DefaultScheduler(Family);

        public int EffWidth => Width ?? Imported?.Width ?? ComfyFamilies.Defaults(Family).Width;

        public int EffHeight => Height ?? Imported?.Height ?? ComfyFamilies.Defaults(Family).Height;

        public int EffSteps => Steps ?? Imported?.Steps ?? ComfyFamilies.Defaults(Family).Steps;

        public double EffCfg => Cfg ?? Imported?.Cfg ?? ComfyFamilies.Defaults(Family).Cfg;

        public double EffDenoise => Denoise ?? Imported?.Denoise ?? ComfyWorkflow.DefaultImageDenoise;

        public string EffNegative => Negative ?? (Import ? Imported?.Negative ?? "" : ComfyFamilies.Defaults(Family).Negative);
    }

    private string ComfyWizardTitle => Crumb(FieldName(SettingsField.ComfyAddWorkflow));

    private string ComfyFolder(bool global) => global ? _settings.GlobalComfyDirectory : _settings.ProfileComfyDirectory;

    private IReadOnlyList<string> ComfyRoots => [_settings.ProfileComfyDirectory, _settings.GlobalComfyDirectory];

    /// <summary>Whether <paramref name="step"/> is asked: the file for an import only; the kind, checkpoint and sampler rows for a build only; the size for a text workflow; the denoise for an image one.</summary>
    private static bool ComfyWizardAsks(ComfyWizardStep step, ComfyDraft draft) => step switch
    {
        ComfyWizardStep.File => draft.Import,
        ComfyWizardStep.Kind or ComfyWizardStep.Checkpoint or ComfyWizardStep.ClipSkip or ComfyWizardStep.Sampler or ComfyWizardStep.Scheduler => !draft.Import,
        ComfyWizardStep.Width or ComfyWizardStep.Height => draft.Import ? draft.Imported?.Width is not null : !draft.FromImage,
        ComfyWizardStep.Steps => !draft.Import || draft.Imported?.Steps is not null,
        ComfyWizardStep.Cfg => !draft.Import || draft.Imported?.Cfg is not null,
        // An import asks it only where it found a denoise (later still on 2026-09-24: a face swap has no sampler).
        ComfyWizardStep.Denoise => draft.Import ? draft.Imported?.Denoise is not null : draft.FromImage,
        _ => true,
    };

    /// <summary>The first step the draft still lacks (the file, the checkpoint, the name), or null.</summary>
    private static ComfyWizardStep? ComfyWizardMissing(ComfyDraft draft) =>
        draft.Import && draft.Imported is null ? ComfyWizardStep.File
        : !draft.Import && draft.Checkpoint.Length == 0 ? ComfyWizardStep.Checkpoint
        : draft.Name.Length == 0 ? ComfyWizardStep.Name
        : null;

    /// <summary>The value column of the draft's row for <paramref name="step"/>.</summary>
    private string ComfyWizardValue(ComfyWizardStep step, ComfyDraft draft)
    {
        if (!ComfyWizardAsks(step, draft))
        {
            return ComfyWizardNotNeeded;
        }

        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? ComfyWizardUnset : text.Trim();
        return step switch
        {
            ComfyWizardStep.Source => draft.Import ? "import" : "build",
            ComfyWizardStep.File => OrUnset(draft.File),
            ComfyWizardStep.Kind => ComfyWizardKindRows[draft.FromImage ? 1 : 0],
            ComfyWizardStep.Checkpoint => OrUnset(draft.Checkpoint),
            ComfyWizardStep.Family => ComfyFamilies.Name(draft.Family),
            ComfyWizardStep.Scope => (draft.Global ? "global  " : "profile ") + ComfyFolder(draft.Global),
            ComfyWizardStep.Name => OrUnset(draft.Name),
            ComfyWizardStep.ClipSkip => draft.EffClipSkip >= 2 ? Invariant(draft.EffClipSkip) : "none",
            ComfyWizardStep.Sampler => draft.EffSampler,
            ComfyWizardStep.Scheduler => draft.EffScheduler,
            ComfyWizardStep.Width => Invariant(draft.EffWidth),
            ComfyWizardStep.Height => Invariant(draft.EffHeight),
            ComfyWizardStep.Steps => Invariant(draft.EffSteps),
            ComfyWizardStep.Cfg => draft.EffCfg.ToString(CultureInfo.InvariantCulture),
            ComfyWizardStep.Denoise => draft.EffDenoise.ToString(CultureInfo.InvariantCulture),
            ComfyWizardStep.Negative => draft.EffNegative.Length == 0 ? "(none)" : draft.EffNegative,
            _ => OrUnset(draft.Description),
        };
    }

    private List<string> ComfyWizardRows(ComfyDraft draft)
    {
        int width = ComfyWizardLabels.Max(l => l.Length) + 2;
        return ComfyWizardLabels.Select((label, i) => Markup.Escape(label.PadRight(width) + ComfyWizardValue((ComfyWizardStep)i, draft))).ToList();
    }

    /// <summary>The wizard: <c>SQL add connection</c>'s loop over <see cref="ComfyWizardStep"/>. True when a setting changed (the offered list).</summary>
    private async Task<bool> AddComfyWorkflowAsync(CancellationToken cancellationToken)
    {
        var draft = new ComfyDraft();
        var step = ComfyWizardStep.Source;
        bool fromSummary = false;
        while (true)
        {
            if (step == ComfyWizardStep.Summary)
            {
                var (done, changed, edit) = await ComfyWizardSummaryAsync(draft, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    return changed;
                }

                if (edit is { } target)
                {
                    step = target;
                    fromSummary = true;
                    continue;
                }

                step = ComfyWizardStep.Description;
                continue;
            }

            if (!ComfyWizardAsks(step, draft))
            {
                step++;
                continue;
            }

            if (await ComfyWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                step = fromSummary ? ComfyWizardMissing(draft) ?? ComfyWizardStep.Summary : step + 1;
                fromSummary = fromSummary && step != ComfyWizardStep.Summary;
                continue;
            }

            if (fromSummary)
            {
                step = ComfyWizardStep.Summary;
                fromSummary = false;
                continue;
            }

            do
            {
                step--;
            }
            while (step >= ComfyWizardStep.Source && !ComfyWizardAsks(step, draft));

            if (step < ComfyWizardStep.Source)
            {
                Sink.Notice(ComfyWizardCancelledNotice);
                return false;
            }
        }
    }

    /// <summary>One step's page; true when answered, false for ESC (or a server list that could not be read, said on the status line).</summary>
    private async Task<bool> ComfyWizardStepAsync(ComfyWizardStep step, ComfyDraft draft, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ComfyWizardStep.Source:
            {
                if (await ComfyWizardPickAsync(ComfyWizardSourceQuestion, ComfyWizardSourceRows, draft.Import ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Import = picked == 1;
                return true;
            }

            case ComfyWizardStep.File:
                return await ComfyWizardTypeAsync(step, draft, ComfyWizardFileQuestion, draft.File, allowEmpty: false, text =>
                {
                    string path = text.Trim().Trim('"');
                    if (path.Length == 0 || !File.Exists(path))
                    {
                        return ComfyWizardFileError(path, "is no file");
                    }

                    string json;
                    try
                    {
                        json = File.ReadAllText(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return ComfyWizardFileError(path, ex.Message);
                    }

                    var (result, problem) = ComfyImport.Placehold(json);
                    if (result is null)
                    {
                        return ComfyWizardFileError(path, problem!);
                    }

                    draft.File = path;
                    draft.Imported = result;
                    draft.Checkpoint = result.Checkpoint;
                    draft.Family = ComfyFamilies.Guess(result.Checkpoint.Length > 0 ? result.Checkpoint : path);
                    draft.Name = draft.Name.Length > 0 ? draft.Name : ComfyWorkflowFile.Suggest(path);
                    foreach (string found in result.Found)
                    {
                        Sink.Notice(ComfyWizardFoundNotice(found));
                    }

                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Kind:
            {
                if (await ComfyWizardPickAsync(ComfyWizardKindQuestion, ComfyWizardKindRows, draft.FromImage ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.FromImage = picked == 1;
                return true;
            }

            case ComfyWizardStep.Checkpoint:
            {
                if (await ComfyChoicesAsync("CheckpointLoaderSimple", "ckpt_name", cancellationToken).ConfigureAwait(false) is not { } list)
                {
                    return false;
                }

                draft.Checkpoints = list;
                if (await ComfyWizardPickAsync(ComfyWizardCheckpointQuestion, list, Math.Max(0, IndexOf(list, draft.Checkpoint)), cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                if (!string.Equals(draft.Checkpoint, list[picked], StringComparison.Ordinal))
                {
                    draft.Checkpoint = list[picked];
                    draft.Family = ComfyFamilies.Guess(draft.Checkpoint);
                    draft.Name = ComfyWorkflowFile.Suggest(draft.Checkpoint);
                }

                return true;
            }

            case ComfyWizardStep.Family:
            {
                if (await ComfyWizardPickAsync(ComfyWizardFamilyQuestion, ComfyWizardFamilyRows, (int)draft.Family, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Family = (ComfyFamily)picked;
                if (!draft.Import && ComfyGraphs.UsuallySplit(draft.Family))
                {
                    // Build wires one checkpoint; these families mostly ship as a diffusion model, a text encoder and a VAE (later still on 2026-09-24).
                    Sink.Warning(ComfyWizardSplitWarning(ComfyFamilies.Name(draft.Family)));
                }
                return true;
            }

            case ComfyWizardStep.Scope:
            {
                string[] rows = ["profile " + ComfyFolder(false), "global  " + ComfyFolder(true)];
                if (await ComfyWizardPickAsync(ComfyWizardScopeQuestion, rows, draft.Global ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Global = picked == 1;
                return true;
            }

            case ComfyWizardStep.Name:
                return await ComfyWizardTypeAsync(step, draft, ComfyWizardNameQuestion, draft.Name, allowEmpty: false, text =>
                {
                    string name = text.Trim();
                    if (!ComfyWorkflowFile.IsValidName(name))
                    {
                        return "The name must be " + ComfyWorkflowFile.NameRule + ".";
                    }

                    if (ComfyWorkflowFile.Taken(ComfyRoots, name) is { } where)
                    {
                        return ComfyText.NameTaken(name, where) + "; pick another name.";
                    }

                    draft.Name = name;
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.ClipSkip:
            {
                if (await ComfyWizardPickAsync(ComfyWizardClipSkipQuestion, ComfyWizardClipSkipRows, draft.EffClipSkip >= 2 ? 1 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.ClipSkip = picked == 1 ? 2 : 0;
                return true;
            }

            case ComfyWizardStep.Sampler:
            {
                if ((draft.Samplers ??= await ComfyChoicesAsync("KSampler", "sampler_name", cancellationToken).ConfigureAwait(false)) is not { } list)
                {
                    return false;
                }

                if (await ComfyWizardPickAsync(ComfyWizardSamplerQuestion, list, Math.Max(0, IndexOf(list, draft.EffSampler)), cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Sampler = list[picked];
                return true;
            }

            case ComfyWizardStep.Scheduler:
            {
                if ((draft.Schedulers ??= await ComfyChoicesAsync("KSampler", "scheduler", cancellationToken).ConfigureAwait(false)) is not { } list)
                {
                    return false;
                }

                if (await ComfyWizardPickAsync(ComfyWizardSchedulerQuestion, list, Math.Max(0, IndexOf(list, draft.EffScheduler)), cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Scheduler = list[picked];
                return true;
            }

            case ComfyWizardStep.Width:
                return await ComfyWizardIntAsync(step, draft, ComfyWizardWidthQuestion, draft.EffWidth, ComfyStudio.MinSide, ComfyStudio.MaxSide, v => draft.Width = v, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Height:
                return await ComfyWizardIntAsync(step, draft, ComfyWizardHeightQuestion, draft.EffHeight, ComfyStudio.MinSide, ComfyStudio.MaxSide, v => draft.Height = v, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Steps:
                return await ComfyWizardIntAsync(step, draft, ComfyWizardStepsQuestion, draft.EffSteps, 1, ComfyStudio.MaxSteps, v => draft.Steps = v, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Cfg:
                return await ComfyWizardNumberAsync(step, draft, ComfyWizardCfgQuestion, draft.EffCfg, ComfyStudio.MaxCfg, v => draft.Cfg = v, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Denoise:
                return await ComfyWizardNumberAsync(step, draft, ComfyWizardDenoiseQuestion, draft.EffDenoise, 1, v => draft.Denoise = v, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.Negative:
                return await ComfyWizardTypeAsync(step, draft, ComfyWizardNegativeQuestion, draft.EffNegative, allowEmpty: true, text =>
                {
                    draft.Negative = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);

            default:
                return await ComfyWizardTypeAsync(step, draft, ComfyWizardDescriptionQuestion, draft.Description, allowEmpty: true, text =>
                {
                    draft.Description = text.Trim();
                    return null;
                }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One of the server's combo lists, or null with the reason on the status line (no server, unreachable, an empty list).</summary>
    private async Task<IReadOnlyList<string>?> ComfyChoicesAsync(string nodeClass, string input, CancellationToken cancellationToken)
    {
        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return null;
        }

        var (choices, error) = await client.ChoicesAsync(nodeClass, input, cancellationToken).ConfigureAwait(false);
        if (error is not null || choices.Count == 0)
        {
            Sink.Error(error ?? ComfyText.BadAnswer("/object_info/" + nodeClass));
            return null;
        }

        return choices;
    }

    private Task<int?> ComfyWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(ComfyWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>A typed step, <c>SqlWizardTypeAsync</c>'s shape: the rows with the step marked, the question as the caption, a wrong answer asked again.</summary>
    private async Task<bool> ComfyWizardTypeAsync(ComfyWizardStep step, ComfyDraft draft, string question, string initial, bool allowEmpty, Func<string, string?> accept, CancellationToken cancellationToken)
    {
        while (true)
        {
            InputResult result;
            if (_pane.Enabled)
            {
                var page = new MenuPage(ComfyWizardTitle, ComfyWizardRows(draft), EditKeys) { Caption = question };
                result = await _pane.EditAsync(page, (int)step, _input, initial, allowEmpty, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Flow.Notice(PromptTitle(ComfyWizardLabels[(int)step] + " · " + question, EditKeys));
                result = await _input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
            }

            if (result is not InputResult.Submitted submitted)
            {
                return false;
            }

            if (accept(submitted.Text) is not { } error)
            {
                return true;
            }

            Sink.Error(error);
            initial = submitted.Text;
        }
    }

    private Task<bool> ComfyWizardIntAsync(ComfyWizardStep step, ComfyDraft draft, string question, int current, int min, int max, Action<int> set, CancellationToken cancellationToken) =>
        ComfyWizardTypeAsync(step, draft, question, Invariant(current), allowEmpty: false, text =>
        {
            string typed = text.Trim();
            if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            {
                return ComfyWizardNumberError(typed, Invariant(min) + " to " + Invariant(max));
            }

            set(value);
            return null;
        }, cancellationToken);

    private Task<bool> ComfyWizardNumberAsync(ComfyWizardStep step, ComfyDraft draft, string question, double current, double max, Action<double> set, CancellationToken cancellationToken) =>
        ComfyWizardTypeAsync(step, draft, question, current.ToString(CultureInfo.InvariantCulture), allowEmpty: false, text =>
        {
            string typed = text.Trim();
            if (!double.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value) || value < 0 || value > max)
            {
                return ComfyWizardNumberError(typed, "0 to " + max.ToString(CultureInfo.InvariantCulture));
            }

            set(value);
            return null;
        }, cancellationToken);

    /// <summary>The summary: save (offered or hidden on a narrowed profile), test, cancel, then every row; <c>SqlWizardSummaryAsync</c>'s contract.</summary>
    private async Task<(bool Done, bool Changed, ComfyWizardStep? Edit)> ComfyWizardSummaryAsync(ComfyDraft draft, CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (true)
        {
            bool narrowed = _settings.Current.ComfyWorkflowsOffered is not null;
            var actions = narrowed ? new List<string> { ComfyWizardSaveOfferedRow, ComfyWizardSaveHiddenRow } : [ComfyWizardSaveRow];
            int test = actions.Count;
            actions.Add(ComfyWizardTestRow);
            actions.Add(ComfyWizardCancelRow);
            var rows = actions.Select(Markup.Escape).Concat(ComfyWizardRows(draft)).ToList();
            var page = new MenuPage(ComfyWizardTitle, rows, PickKeys) { Caption = ComfyWizardSummaryCaption };
            if (!_pane.Enabled)
            {
                Flow.Notice(ComfyWizardSummaryCaption);
            }

            if (await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return (false, false, null);
            }

            cursor = picked;
            if (picked >= actions.Count)
            {
                var edit = (ComfyWizardStep)(picked - actions.Count);
                if (ComfyWizardAsks(edit, draft))
                {
                    return (false, false, edit);
                }

                continue;
            }

            if (picked == test)
            {
                await ComfyWizardTestAsync(draft, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (picked == test + 1)
            {
                Sink.Notice(ComfyWizardCancelledNotice);
                return (true, false, null);
            }

            if (ComfyWizardSave(draft, offer: picked == 0) is { } changed)
            {
                return (true, changed, null);
            }
        }
    }

    /// <summary>The draft's graph as it will be saved.</summary>
    private static string ComfyWizardGraph(ComfyDraft draft) =>
        draft.Import ? draft.Imported!.GraphJson : ComfyGraphs.Build(draft.Name, draft.Checkpoint, draft.EffSampler, draft.EffScheduler, draft.EffClipSkip, draft.FromImage);

    /// <summary>The draft's sidecar as it will be saved: every default the workflow uses.</summary>
    private static string ComfyWizardSidecar(ComfyDraft draft) => ComfyWorkflowFile.Sidecar(new ComfySidecar(
        draft.Description,
        draft.Family,
        ComfyWizardAsks(ComfyWizardStep.Width, draft) ? draft.EffWidth : null,
        ComfyWizardAsks(ComfyWizardStep.Height, draft) ? draft.EffHeight : null,
        ComfyWizardAsks(ComfyWizardStep.Steps, draft) ? draft.EffSteps : null,
        ComfyWizardAsks(ComfyWizardStep.Cfg, draft) ? draft.EffCfg : null,
        ComfyWizardAsks(ComfyWizardStep.Denoise, draft) ? draft.EffDenoise : null,
        draft.EffNegative));

    /// <summary>
    /// The draft run once on the server, small (at most <see cref="ComfyWizardTestSide"/> square and <see cref="ComfyWizardTestSteps"/>
    /// steps) and nothing written here: an image workflow gets a grey input picture uploaded. What ComfyUI saved, or why it refused, on the status line.
    /// </summary>
    private async Task ComfyWizardTestAsync(ComfyDraft draft, CancellationToken cancellationToken)
    {
        if (ComfyWizardMissing(draft) is { } missing)
        {
            Sink.Error("Set " + ComfyWizardLabels[(int)missing] + " first.");
            return;
        }

        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return;
        }

        if (!ComfyWorkflow.TryParse(draft.Name, "", ComfyWizardGraph(draft), ComfyWizardSidecar(draft), out var workflow, out string? problem))
        {
            Sink.Error(ComfyWorkflowAddFailed(draft.Name, problem!));
            return;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.ComfyTimeoutSeconds, AppSettingsData.MinComfyTimeoutSeconds, AppSettingsData.MaxComfyTimeoutSeconds));
        IReadOnlyList<string>? images = null;
        if (workflow!.TakesImage)
        {
            var (name, error) = await client.UploadAsync(SmokeChecks.SolidBmp(ComfyWizardTestSide, ComfyWizardTestSide), "neon-test.bmp", timeout, cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                Sink.Error(error!);
                return;
            }

            // The one test picture in every input slot (later still on 2026-09-24: a face swap takes two).
            images = Enumerable.Repeat(name, workflow.ImageCount).ToList();
        }

        var d = workflow.Defaults;
        var values = new ComfyValues(ComfyWizardTestPrompt, d.Negative, 1, Math.Min(d.Width, ComfyWizardTestSide), Math.Min(d.Height, ComfyWizardTestSide), Math.Min(d.Steps, ComfyWizardTestSteps), null, null, images);
        var clock = Stopwatch.StartNew();
        var run = await client.RunAsync(workflow.Fill(values), timeout, cancellationToken).ConfigureAwait(false);
        if (run.Ok)
        {
            Sink.Notice(ComfyWizardTestOkNotice(draft.Name, clock.Elapsed.TotalSeconds, run.Images.Select(i => i.FileName).ToList()));
        }
        else
        {
            Sink.Error(run.Error!);
        }
    }

    /// <summary>Writes the draft (<see cref="ComfyWorkflowFile.Add"/>), then — offered on a narrowed profile — its name into the list. Whether a setting changed; null when nothing was written.</summary>
    private bool? ComfyWizardSave(ComfyDraft draft, bool offer)
    {
        if (ComfyWizardMissing(draft) is { } missing)
        {
            Sink.Error("Set " + ComfyWizardLabels[(int)missing] + " first.");
            return null;
        }

        string folder = ComfyFolder(draft.Global);
        if (ComfyWorkflowFile.Add(folder, ComfyRoots, draft.Name, ComfyWizardGraph(draft), ComfyWizardSidecar(draft)) is { } error)
        {
            Sink.Error(ComfyWorkflowAddFailed(draft.Name, error));
            return null;
        }

        Sink.Notice(ComfyWorkflowAdded(draft.Name, folder));
        if (!offer || _settings.Current.ComfyWorkflowsOffered is not { } offered)
        {
            return false;
        }

        Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = [.. offered, draft.Name]);
        return true;
    }

    // ── ComfyUI workflows offered ───────────────────────────────────────────

    /// <summary>The workflows the two comfy folders hold, for the checklist and its value (the profile's then the home's).</summary>
    private static IReadOnlyList<ComfyWorkflow> InstalledComfyWorkflows(string profileDirectory) =>
        new ComfyWorkflowCatalog(() => [Path.Combine(profileDirectory, ComfyWorkflowCatalog.DirectoryName), Path.Combine(Profiles.HomeOf(profileDirectory), ComfyWorkflowCatalog.DirectoryName)]).Workflows;

    /// <summary>The <c>ComfyUI workflows offered</c> value: <see cref="ComfyNotNarrowedLabel"/>, <c>K of N</c> or <c>none of N</c>. Pinned.</summary>
    public static string ComfyOfferedValue(IReadOnlyList<string>? offered, IReadOnlyList<ComfyWorkflow> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (offered is null)
        {
            return ComfyNotNarrowedLabel;
        }

        int kept = ComfyWorkflowCatalog.Offered(installed, offered).Count;
        return (kept == 0 ? "none" : Invariant(kept)) + " of " + Invariant(installed.Count);
    }

    /// <summary>One checklist row: the mark, the name, what it is. Pinned.</summary>
    public static string ComfyOfferedRow(ComfyWorkflow workflow, bool offered, int width)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var d = workflow.Defaults;
        string note = ComfyFamilies.Name(workflow.Family) + " · " + ComfyText.InputShape(workflow) + " · " + Invariant(d.Width) + "×" + Invariant(d.Height);
        return Markup.Escape((offered ? "[x] " : "[ ] ") + workflow.Name.PadRight(width)) + Theme.DimMarkup(note);
    }

    /// <summary><c>EditSqlOfferedAsync</c>'s loop over the installed workflows: Enter or Space flips one, saved at once; the first flip from "not narrowed" keeps all but that one.</summary>
    private async Task<bool> EditComfyOfferedAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var installed = InstalledComfyWorkflows(_settings.ProfileDirectory);
            if (installed.Count == 0)
            {
                Sink.Error(ComfyText.NoWorkflows(ComfyRoots));
                return changed;
            }

            var offered = _settings.Current.ComfyWorkflowsOffered;
            var on = ComfyWorkflowCatalog.Offered(installed, offered).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int width = installed.Max(w => w.Name.Length) + 2;
            var page = new MenuPage(Crumb(FieldName(SettingsField.ComfyWorkflowsOffered)), installed.Select(w => ComfyOfferedRow(w, on.Contains(w.Name), width)).ToList(), ToggleKeys) { SpaceToggles = true };
            var picked = await PickChecklistAsync(page, Math.Min(cursor, installed.Count - 1), cancellationToken).ConfigureAwait(false);
            if (picked is not { } pick)
            {
                if (!changed)
                {
                    Sink.Notice(UnchangedNotice);
                }

                return changed;
            }

            cursor = pick.Row;
            string name = installed[pick.Row].Name;
            // Select all ticks the workflows installed now, one added later still starting hidden (2026-09-29, the user's call).
            var next = pick.Button == SelectAllIndex ? installed.Select(w => w.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : installed.Select(w => w.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (offered is not null && next.Count == on.Count && next.All(on.Contains))
            {
                continue;   // a button that changes nothing saves nothing; from "not narrowed", select all narrows to today's list
            }

            // A name ticked before but no longer installed stays in the list: it counts again if the workflow comes back.
            if (offered is not null)
            {
                next.AddRange(offered.Where(n => !installed.Any(w => string.Equals(w.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            }

            Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = next);
            changed = true;
        }
    }
}
