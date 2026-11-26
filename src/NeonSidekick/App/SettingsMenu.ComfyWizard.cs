using System.Globalization;
using System.Text.Json.Nodes;
using NeonSidekick.Comfy;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The new-workflow wizard (later on 2026-09-24, the user's ask: "a wizard function to add new comfy workflows similar to how we did it for
/// SQL connections"), shortened on 2026-11-25 (the workflow manager): it asks only Source → Kind → Checkpoint → Name to <b>build</b> a
/// standard workflow from the server's models (<see cref="ComfyGraphs"/>), or Source → File → Name to <b>import</b> one exported from ComfyUI
/// (<see cref="ComfyImport"/>), then shows the summary (<c>SettingsMenu.ComfyEdit.cs</c>) with everything else at its default, where Enter on a
/// row changes it, Test runs it and Save writes <c>&lt;name&gt;.md</c> and <c>&lt;name&gt;.json</c> into a <c>comfy</c> folder
/// (<see cref="ComfyWorkflowFile.Add"/>). Kind <c>both</c> saves a text and an image workflow together.
/// </summary>
internal sealed partial class SettingsMenu
{
    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The asked pages' rows, one per <see cref="ComfyWizardStep"/> before the summary, in its order. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardLabels = ["Source", "File", "Kind", "Checkpoint", "Name", "Run"];

    /// <summary>The wizard's crumb under the list.</summary>
    public const string ComfyNewWorkflowCrumb = "New workflow";

    public const string ComfyWizardSourceQuestion = "Build a standard workflow from the server's models, or import one ComfyUI ran?";
    public const string ComfyWizardFileQuestion = "The full path of a ComfyUI export (Workflow → Export (API)) or of a PNG or WebP picture ComfyUI saved.";
    public const string ComfyWizardRunQuestion = "The run to import: the server's recent runs, newest first.";
    public const string ComfyRunsLabel = "Reading the server's recent runs…";
    public const string ComfyPickRunFirst = "Pick a run first.";

    /// <summary>The run list's row that shows or hides the app's own runs.</summary>
    public static string ComfyOwnRunsRow(int count, bool shown) => shown ? "Hide the app's own runs" : $"Show the app's own runs ({Invariant(count)})";

    /// <summary>A picked run the import refuses.</summary>
    public static string ComfyRunRefused(string problem) => "Error: that run's graph " + problem;
    public const string ComfyWizardKindQuestion = "What it makes.";
    public const string ComfyWizardCheckpointQuestion = "The checkpoint (the model) it loads, from the server's list.";
    public const string ComfyWizardFamilyQuestion = "The model family: it picks the prompt style the model writes in and the defaults below.";
    public const string ComfyWizardScopeQuestion = "Which comfy folder it goes in.";
    public const string ComfyWizardNameQuestion = "Its name: what the model and /imagine call it (" + ComfyWorkflowFile.NameRule + ").";
    public const string ComfyWizardClipSkipQuestion = "CLIP skip: Pony Diffusion XL is trained for 2; most other checkpoints want none.";
    public const string ComfyWizardSamplerQuestion = "The sampler.";
    public const string ComfyWizardSchedulerQuestion = "The scheduler.";
    public const string ComfyWizardVaeQuestion = "The VAE: the checkpoint's own, or one from the server's list.";
    public const string ComfyWizardLoraQuestion = "The LoRA to add, from the server's list.";
    public static readonly string ComfyWizardWidthQuestion = "The default width in pixels, " + Invariant(ComfyStudio.MinSide) + " to " + Invariant(ComfyStudio.MaxSide) + ".";
    public static readonly string ComfyWizardHeightQuestion = "The default height in pixels, " + Invariant(ComfyStudio.MinSide) + " to " + Invariant(ComfyStudio.MaxSide) + ".";
    public static readonly string ComfyWizardStepsQuestion = "The default sampling steps, 1 to " + Invariant(ComfyStudio.MaxSteps) + ".";
    public const string ComfyWizardCfgQuestion = "The default CFG (guidance), 0 to 30.";
    public const string ComfyWizardDenoiseQuestion = "How much of the input picture to repaint by default, 0 to 1 (0.3 keeps it close, 0.8 changes it a lot).";
    public const string ComfyWizardNegativeQuestion = "The default negative prompt (empty for none).";
    public const string ComfyWizardDescriptionQuestion = "What it is for, in your words (optional): the model reads it to pick a workflow.";
    public const string ComfyWizardSummaryCaption = "Check the workflow: Test runs it once without saving; Enter on a row below changes it.";

    /// <summary>The source picks. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardSourceRows =
    [
        "build   a standard workflow from the server's models",
        "run     one of the server's recent runs",
        "file    a ComfyUI export, or a picture ComfyUI saved",
    ];

    /// <summary>The kind picks. Pinned.</summary>
    public static readonly IReadOnlyList<string> ComfyWizardKindRows =
        ["text → image", "image → image (restyle an input picture)", "both (two workflows: <name>" + ComfyTextSuffix + " and <name>" + ComfyImageSuffix + ")"];

    /// <summary>The names <c>both</c> saves under: the base name and these.</summary>
    public const string ComfyTextSuffix = "_txt2img";
    public const string ComfyImageSuffix = "_img2img";

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
        "flux2        FLUX.2 dev: long prose, hex colors, no negative (split model: import)",
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

    /// <summary>The VAE list's first row: the checkpoint's own.</summary>
    public const string ComfyCheckpointVaeRow = "(from the checkpoint)";

    /// <summary>The mark after a graph's value the server doesn't list.</summary>
    public const string ComfyNotOnServerMark = "  (not on the server's list)";

    public const string ComfyWizardSaveOfferedRow = "Save, and offer it to the model";
    public const string ComfyWizardSaveHiddenRow = "Save, hidden from the model until you tick it in ComfyUI workflows";
    public const string ComfyWizardTestRow = "Test the workflow";
    public const string ComfyWizardCancelRow = "Cancel";
    public const string ComfyWizardCancelledNotice = "No workflow added.";
    public const string ComfyWizardNotNeeded = "(not needed)";
    public const string ComfyWizardUnset = "—";

    /// <summary>The prompt a test runs by default, at most <see cref="ComfyWizardTestSide"/> square and <see cref="ComfyWizardTestSteps"/> steps when small.</summary>
    public const string ComfyWizardTestPrompt = "a test picture";
    public const int ComfyWizardTestSide = 512;
    public const int ComfyWizardTestSteps = 8;

    /// <summary>The warning when Build is given a family that usually loads as split files (later still on 2026-09-24). Pinned.</summary>
    public static string ComfyWizardSplitWarning(string family) =>
        $"{family} models usually load as separate files (a diffusion model, a text encoder, a VAE), which Build cannot wire: it works only for an all-in-one checkpoint. For the others, export ComfyUI's own template with Export (API) and Import it.";

    public static string ComfyWizardFileError(string path, string problem) => $"'{path}' {problem}.";

    public static string ComfyWizardNumberError(string typed, string range) => $"'{typed}' is not {range}.";

    public static string ComfyWorkflowAdded(string name, string folder) => $"Added workflow '{name}' to {folder}.";

    public static string ComfyWorkflowAddFailed(string name, string why) => $"Could not add workflow '{name}': {why}";

    /// <summary>A name too long for <c>both</c>'s suffixes.</summary>
    public static readonly string ComfyBothNameTooLong = "The name must leave room for " + ComfyTextSuffix + " (" + ComfyWorkflowFile.NameRule + ").";

    /// <summary>The page a wizard step asks; the summary last. The rows of <see cref="ComfyWizardLabels"/> by index.</summary>
    internal enum ComfyWizardStep
    {
        Source,
        File,
        Kind,
        Checkpoint,
        Name,
        Run,
        Summary,
    }

    /// <summary>What the wizard makes: a text workflow, an image one, or both.</summary>
    internal enum ComfyKind
    {
        Text,
        Image,
        Both,
    }

    /// <summary>A LoRA a build adds, in order.</summary>
    private sealed class ComfyDraftLora(string name, double strength)
    {
        public string Name { get; } = name;

        public double Strength { get; set; } = strength;
    }

    /// <summary>
    /// What the wizard or an Edit has so far. A null number or text is "the default": for a new workflow the import's value or the family's,
    /// written into the sidecar at the save; for an installed one, a key the sidecar leaves to the family.
    /// </summary>
    private sealed class ComfyDraft
    {
        /// <summary>An installed workflow's Edit (no source, kind, name or scope).</summary>
        public bool Editing { get; init; }

        public bool Import { get; set; }

        /// <summary>An import from a recent run (Source's second row), not a file.</summary>
        public bool FromRun { get; set; }

        /// <summary>The picked run's row text, for the asked pages; the runs read once a wizard.</summary>
        public string RunLabel { get; set; } = "";

        public IReadOnlyList<ComfyRun>? Runs { get; set; }

        public string File { get; set; } = "";

        public ComfyImportResult? Imported { get; set; }

        public ComfyKind Kind { get; set; }

        public bool FromImage => Kind == ComfyKind.Image;

        // A build's own parts; the graph is made from them (ComfyBuildGraph).
        public string Checkpoint { get; set; } = "";

        public string? Vae { get; set; }

        public List<ComfyDraftLora> Loras { get; } = [];

        public int? ClipSkip { get; set; }

        public string? Sampler { get; set; }

        public string? Scheduler { get; set; }

        /// <summary>An import's or an Edit's working graph; null for a build, or with <see cref="GraphWhy"/> when it can't be changed here.</summary>
        public JsonObject? Graph { get; set; }

        public string? GraphWhy { get; set; }

        public ComfyFamily Family { get; set; }

        public bool FamilyTouched { get; set; }

        public bool Global { get; set; }

        public string Name { get; set; } = "";

        public bool NameTouched { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public int? Steps { get; set; }

        public double? Cfg { get; set; }

        public double? Denoise { get; set; }

        public string? Negative { get; set; }

        public string Description { get; set; } = "";

        public string Tips { get; set; } = "";

        public bool Reinforce { get; set; } = true;

        public string[] ImageRoles { get; } = ["", "", ""];

        // An Edit's files as read, and the workflow they made.
        public ComfyFileText? JsonFile { get; init; }

        public ComfyFileText? MdFile { get; init; }

        public ComfyWorkflow? Original { get; init; }

        public JsonObject? OriginalGraph { get; init; }

        /// <summary>The server's lists, read once a visit, by <c>class.input</c>.</summary>
        public Dictionary<string, IReadOnlyList<string>> Lists { get; } = new(StringComparer.Ordinal);

        private ComfyDefaults FamilyDefaults => ComfyFamilies.Defaults(Family);

        private ComfyImportResult? Own => Import && !Editing ? Imported : null;

        public bool TakesImage => Import ? Imported?.TakesImage == true : Kind != ComfyKind.Text;

        public int EffClipSkip => ClipSkip ?? ComfyGraphs.DefaultClipSkip(Family);

        public string EffSampler => Sampler ?? ComfyGraphs.DefaultSampler(Family);

        public string EffScheduler => Scheduler ?? ComfyGraphs.DefaultScheduler(Family);

        public int EffWidth => Width ?? Own?.Width ?? FamilyDefaults.Width;

        public int EffHeight => Height ?? Own?.Height ?? FamilyDefaults.Height;

        public int EffSteps => Steps ?? Own?.Steps ?? FamilyDefaults.Steps;

        public double EffCfg => Cfg ?? Own?.Cfg ?? FamilyDefaults.Cfg;

        public double EffDenoise => Denoise ?? Own?.Denoise ?? ComfyWorkflow.DefaultImageDenoise;

        public string EffNegative => Negative ?? (Import && !Editing ? Imported?.Negative ?? "" : FamilyDefaults.Negative);
    }

    private string ComfyWizardTitle => Crumb(FieldName(SettingsField.ComfyWorkflowsOffered) + " › " + ComfyNewWorkflowCrumb);

    private string ComfyFolder(bool global) => global ? _settings.GlobalComfyDirectory : _settings.ProfileComfyDirectory;

    private IReadOnlyList<string> ComfyRoots => [_settings.ProfileComfyDirectory, _settings.GlobalComfyDirectory];

    /// <summary>Where a new workflow starts: global when the profile's folder holds none and the global one some, else the profile's.</summary>
    private bool ComfyStartsGlobal()
    {
        static bool Any(string folder)
        {
            try
            {
                return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.json").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return !Any(_settings.ProfileComfyDirectory) && Any(_settings.GlobalComfyDirectory);
    }

    /// <summary>The pages a draft asks, in order, the summary last.</summary>
    private static List<ComfyWizardStep> ComfyWizardSteps(ComfyDraft draft) =>
        draft.FromRun ? [ComfyWizardStep.Source, ComfyWizardStep.Run, ComfyWizardStep.Name, ComfyWizardStep.Summary]
        : draft.Import ? [ComfyWizardStep.Source, ComfyWizardStep.File, ComfyWizardStep.Name, ComfyWizardStep.Summary]
        : [ComfyWizardStep.Source, ComfyWizardStep.Kind, ComfyWizardStep.Checkpoint, ComfyWizardStep.Name, ComfyWizardStep.Summary];

    /// <summary>The asked pages' rows with their values, for the typed pages (the file, the name).</summary>
    private List<string> ComfyWizardRows(ComfyDraft draft)
    {
        var steps = ComfyWizardSteps(draft).Where(s => s != ComfyWizardStep.Summary).ToList();
        int width = steps.Max(s => ComfyWizardLabels[(int)s].Length) + 2;
        return steps.Select(s => Markup.Escape(ComfyWizardLabels[(int)s].PadRight(width) + ComfyWizardStepValue(s, draft))).ToList();
    }

    private static string ComfyWizardStepValue(ComfyWizardStep step, ComfyDraft draft)
    {
        static string OrUnset(string? text) => string.IsNullOrWhiteSpace(text) ? ComfyWizardUnset : text.Trim();
        return step switch
        {
            ComfyWizardStep.Source => draft.FromRun ? "run" : draft.Import ? "file" : "build",
            ComfyWizardStep.File => OrUnset(draft.File),
            ComfyWizardStep.Run => OrUnset(draft.RunLabel),
            ComfyWizardStep.Kind => ComfyWizardKindRows[(int)draft.Kind],
            ComfyWizardStep.Checkpoint => OrUnset(draft.Checkpoint),
            _ => OrUnset(draft.Name),
        };
    }

    /// <summary>
    /// The wizard: the asked pages, then the summary; Esc one page back (the draft kept), off the first page cancelled. (Changed, the name
    /// added for the list's cursor.)
    /// </summary>
    private async Task<(bool Changed, string? Added)> AddComfyWorkflowAsync(CancellationToken cancellationToken)
    {
        var draft = new ComfyDraft { Global = ComfyStartsGlobal() };
        int at = 0;
        while (true)
        {
            var steps = ComfyWizardSteps(draft);
            at = Math.Clamp(at, 0, steps.Count - 1);
            var step = steps[at];
            if (step == ComfyWizardStep.Summary)
            {
                var (done, changed, added) = await ComfySummaryAsync(draft, cancellationToken).ConfigureAwait(false);
                if (done)
                {
                    return (changed, added);
                }

                at--;   // Esc on the summary: back to the name, the draft kept
                continue;
            }

            if (await ComfyWizardStepAsync(step, draft, cancellationToken).ConfigureAwait(false))
            {
                at++;
                continue;
            }

            if (--at < 0)
            {
                Sink.Notice(ComfyWizardCancelledNotice);
                return (false, null);
            }
        }
    }

    /// <summary>One asked page; true when answered, false for Esc (or a server list that could not be read, said on the status line).</summary>
    private async Task<bool> ComfyWizardStepAsync(ComfyWizardStep step, ComfyDraft draft, CancellationToken cancellationToken)
    {
        switch (step)
        {
            case ComfyWizardStep.Source:
            {
                if (await ComfyWizardPickAsync(ComfyWizardSourceQuestion, ComfyWizardSourceRows, draft.FromRun ? 1 : draft.Import ? 2 : 0, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                bool import = picked > 0, fromRun = picked == 1;
                if (import != draft.Import || fromRun != draft.FromRun)
                {
                    ComfyDropImport(draft);
                }

                draft.Import = import;
                draft.FromRun = fromRun;
                return true;
            }

            case ComfyWizardStep.Run:
                return await ComfyPickRunAsync(draft, cancellationToken).ConfigureAwait(false);

            case ComfyWizardStep.File:
            {
                var page = new MenuPage(ComfyWizardTitle, ComfyWizardRows(draft), EditKeys);
                int row = ComfyWizardSteps(draft).IndexOf(ComfyWizardStep.File);
                string initial = draft.File;
                while (true)
                {
                    if (await ComfyAskTextAsync(page, row, ComfyWizardFileQuestion, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false) is not { } typed)
                    {
                        return false;
                    }

                    if (ComfyImportFile(draft, typed) is not { } error)
                    {
                        return true;
                    }

                    Sink.Error(error);
                    initial = typed;
                }
            }

            case ComfyWizardStep.Kind:
            {
                if (await ComfyWizardPickAsync(ComfyWizardKindQuestion, ComfyWizardKindRows, (int)draft.Kind, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                draft.Kind = (ComfyKind)picked;
                return true;
            }

            case ComfyWizardStep.Checkpoint:
            {
                if (await ComfyChoicesAsync(draft, "CheckpointLoaderSimple", "ckpt_name", cancellationToken).ConfigureAwait(false) is not { } list)
                {
                    return false;
                }

                if (await ComfyPickValueAsync(ComfyWizardTitle, ComfyWizardCheckpointQuestion, list, draft.Checkpoint, null, cancellationToken).ConfigureAwait(false) is not { } picked)
                {
                    return false;
                }

                ComfySetCheckpoint(draft, picked.Value!);
                return true;
            }

            default:   // Name
            {
                if (!draft.NameTouched && draft.Name.Length == 0)
                {
                    draft.Name = ComfySuggestName(draft, draft.Import ? ComfyImportNameFrom(draft.Imported?.Checkpoint ?? "", draft.File) : draft.Checkpoint);
                }

                var page = new MenuPage(ComfyWizardTitle, ComfyWizardRows(draft), EditKeys);
                int row = ComfyWizardSteps(draft).IndexOf(ComfyWizardStep.Name);
                return await ComfyAskNameAsync(page, row, ComfyWizardNameQuestion, draft.Name, typed => ComfySetName(draft, typed), cancellationToken).ConfigureAwait(false) is not null;
            }
        }
    }

    /// <summary>The draft's import read from <paramref name="typed"/> (a path), or why not. Another file drops the old import's values and edits.</summary>
    private string? ComfyImportFile(ComfyDraft draft, string typed)
    {
        string path = UnquotePath(typed.Trim());
        if (path.Length == 0)
        {
            return ComfyWizardFileError(path, ComfyText.PathNotAFile);
        }

        var (read, why) = ComfyReadTypedFile(path, ComfyImportMaxBytes);
        if (read is not { } bytes)
        {
            return ComfyWizardFileError(path, why!);
        }

        // By the bytes, never the extension (2026-11-25): a PNG or WebP ComfyUI saved carries the graph it ran.
        string json;
        if (ComfyEmbeddedGraph.IsPicture(bytes))
        {
            var (embedded, missing) = ComfyEmbeddedGraph.Read(bytes);
            if (embedded is null)
            {
                return ComfyWizardFileError(path, missing!);
            }

            json = embedded;
        }
        else
        {
            json = Files.TextFile.Decode(bytes, out _);
            if (!json.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{'))
            {
                return ComfyWizardFileError(path, ComfyText.NotWorkflowOrPicture);
            }
        }

        var (result, problem) = ComfyImport.Placehold(json);
        if (result is null)
        {
            return ComfyWizardFileError(path, problem!);
        }

        ComfyDropImport(draft);
        draft.File = path;
        ComfyTakeImport(draft, result, ComfyImportNameFrom(result.Checkpoint, path));
        DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogImported(Path.GetFileName(path), "taken"));
        return null;
    }

    /// <summary>What an import's name is suggested from: its model's file name, else an export's file name; a picture's (<c>ComfyUI_00012_</c>) never.</summary>
    private static string ComfyImportNameFrom(string checkpoint, string path) =>
        checkpoint.Length > 0 ? checkpoint
        : path.Length > 0 && !Files.ImageFile.IsImagePath(path) ? path
        : ComfyRunNameFallback;

    /// <summary>The suggestion when an import names no model and no export file.</summary>
    public const string ComfyRunNameFallback = "workflow";

    /// <summary>
    /// The Run page (2026-11-25): the server's recent runs (<see cref="ComfyHistory"/>, read once a wizard under the busy page), newest first, the
    /// app's own hidden behind a row that shows them; a row's band gives the prompt's start and the output file. A run the import refuses says why
    /// and the page stays. True once a run is taken.
    /// </summary>
    private async Task<bool> ComfyPickRunAsync(ComfyDraft draft, CancellationToken cancellationToken)
    {
        if (draft.Runs is null)
        {
            if (_comfyClient() is not { } client)
            {
                Sink.Error(ComfyText.NoServer);
                return false;
            }

            var (done, read) = await ComfyBusyAsync(ComfyRunsLabel, token => client.HistoryAsync(token), cancellationToken).ConfigureAwait(false);
            if (!done)
            {
                return false;
            }

            if (read.Error is { } error)
            {
                Sink.Error(error);
                return false;
            }

            draft.Runs = ComfyHistory.Parse(read.Body ?? "");
        }

        if (draft.Runs.Count == 0)
        {
            Sink.Notice(ComfyText.NoRuns);
            return false;
        }

        bool showOwn = draft.Runs.All(r => r.Own);
        int cursor = 0;
        while (true)
        {
            var shown = draft.Runs.Where(r => showOwn || !r.Own).ToList();
            int own = draft.Runs.Count(r => r.Own);
            var rows = shown.Select(r => Markup.Escape(ComfyRunRow(r))).ToList();
            if (own > 0 && own < draft.Runs.Count)
            {
                rows.Add(Markup.Escape(ComfyOwnRunsRow(own, showOwn)));
            }

            var page = new MenuPage(ComfyWizardTitle, rows, PickKeys)
            {
                Caption = ComfyWizardRunQuestion,
                Footer = (_, row) => row >= 0 && row < shown.Count ? ComfyRunBand(shown[row]) : null,
            };
            if (!_pane.Enabled)
            {
                Flow.Notice(ComfyWizardRunQuestion);
            }

            if (await PickAsync(page, Math.Clamp(cursor, 0, rows.Count - 1), cancellationToken).ConfigureAwait(false) is not { } picked)
            {
                return false;
            }

            cursor = picked;
            if (picked >= shown.Count)
            {
                showOwn = !showOwn;
                continue;
            }

            var run = shown[picked];
            if (run.Import is not { } import)
            {
                Sink.Error(ComfyRunRefused(run.Problem ?? ComfyWorkflow.NotApiFormatProblem));
                continue;
            }

            ComfyDropImport(draft);
            draft.RunLabel = ComfyRunRow(run);
            ComfyTakeImport(draft, import, ComfyImportNameFrom(import.Checkpoint, ""));
            DiagnosticLog.Info(ComfyWorkflowFile.Category, ComfyText.LogImported("a recent run", "taken"));
            return true;
        }
    }

    /// <summary>A run's row: its age, model, size and steps, and how many runs of it there were.</summary>
    private string ComfyRunRow(ComfyRun run)
    {
        string age = run.StartedMs is { } ms ? Docker.DockerText.Age(ms / 1000, ComfyClock.GetUtcNow()) : "—";
        var import = run.Import;
        // Only the model is the server's text (2026-11-26, the review's #100): scrubbed alone, so the age's padding stays.
        string model = run.Model.Length > 0 ? UntrustedText.Name(Path.GetFileNameWithoutExtension(run.Model.Replace('\\', '/').Split('/')[^1])) : import is null ? "(not importable)" : "(no model)";
        string size = import is { Width: { } w, Height: { } h } ? Invariant(w) + "×" + Invariant(h) : "";
        string steps = import?.Steps is { } st ? Invariant(st) + " steps" : "";
        string repeats = run.Repeats > 1 ? "×" + Invariant(run.Repeats) : "";
        return string.Join("  ", new[] { age.PadRight(8), TextCells.Cut(model, 40), size, steps, repeats }.Where(p => p.Length > 0));
    }

    /// <summary>
    /// A run's band: the start of its prompt (shown here only, never saved) and its first output file. Plain text: the pane's footer escapes it once
    /// (2026-11-26, the review's #73; escaped here too, a [ read as [[).
    /// </summary>
    private static MenuFooter ComfyRunBand(ComfyRun run)
    {
        var lines = new List<string>();
        if (run.Import?.Prompt is { Length: > 0 } prompt)
        {
            lines.Add(TextCells.Cut(UntrustedText.OneLine(prompt), 200));
        }

        if (run.OutputFile is { } output)
        {
            lines.Add(UntrustedText.OneLine(output));
        }

        if (run.Import is null && run.Problem is { } problem)
        {
            lines.Add(problem);
        }

        return new MenuFooter(string.Join("\n", lines)) { KeepLines = true };
    }

    /// <summary>The clock the run list's ages are read against (a test sets its own).</summary>
    public TimeProvider ComfyClock { get; set; } = TimeProvider.System;

    /// <summary>The largest file the import reads (a ComfyUI picture carries its graph; an export is far smaller).</summary>
    public const long ComfyImportMaxBytes = 64L * 1024 * 1024;

    /// <summary>An import's result taken into the draft: its working graph, its checkpoint and the family guessed from it, a suggested name.</summary>
    private void ComfyTakeImport(ComfyDraft draft, ComfyImportResult result, string from)
    {
        draft.Imported = result;
        (draft.Graph, draft.GraphWhy) = ComfyGraphEdit.Load(result.GraphJson);
        draft.Checkpoint = result.Checkpoint;
        if (!draft.FamilyTouched)
        {
            draft.Family = ComfyFamilies.Guess(result.Checkpoint.Length > 0 ? result.Checkpoint : from);
        }

        if (!draft.NameTouched)
        {
            draft.Name = ComfySuggestName(draft, result.Checkpoint.Length > 0 ? result.Checkpoint : from);
        }
    }

    /// <summary>Another source, file or run: the import's values and the graph edits made on it go; a build's own LoRAs and VAE stay.</summary>
    private static void ComfyDropImport(ComfyDraft draft)
    {
        draft.Imported = null;
        draft.Graph = null;
        draft.GraphWhy = null;
        draft.File = "";
        if (!draft.NameTouched)
        {
            draft.Name = "";
        }
    }

    /// <summary>A build's checkpoint: the family guessed again while untouched, and the suggested name follows while untouched.</summary>
    private void ComfySetCheckpoint(ComfyDraft draft, string checkpoint)
    {
        if (string.Equals(draft.Checkpoint, checkpoint, StringComparison.Ordinal))
        {
            return;
        }

        draft.Checkpoint = checkpoint;
        if (!draft.FamilyTouched)
        {
            draft.Family = ComfyFamilies.Guess(checkpoint);
        }

        if (!draft.NameTouched)
        {
            draft.Name = ComfySuggestName(draft, checkpoint);
        }
    }

    /// <summary>A free name suggested from <paramref name="from"/> (a model's file name, a path): never one taken in either folder.</summary>
    private string ComfySuggestName(ComfyDraft draft, string from)
    {
        string suggested = ComfyWorkflowFile.Suggest(Path.GetFileNameWithoutExtension(from.Replace('\\', '/').Split('/')[^1]));
        return ComfyWorkflowFile.FreeName(suggested, ComfyRoots, ComfyFolder(draft.Global), draft.Kind == ComfyKind.Both ? [ComfyTextSuffix, ComfyImageSuffix] : null);
    }

    /// <summary>The names a draft saves under: its name, or for <c>both</c> the text and image names.</summary>
    private static List<string> ComfyWizardNames(ComfyDraft draft) =>
        draft.Kind == ComfyKind.Both && !draft.Import ? [draft.Name + ComfyTextSuffix, draft.Name + ComfyImageSuffix] : [draft.Name];

    /// <summary>A typed name taken (null), or why not: each name it saves under must be free in both folders.</summary>
    private string? ComfySetName(ComfyDraft draft, string typed)
    {
        string name = typed.Trim();
        if (draft.Kind == ComfyKind.Both && !draft.Import && ComfyWorkflowFile.IsValidName(name) && !ComfyWorkflowFile.IsValidName(name + ComfyTextSuffix))
        {
            return ComfyBothNameTooLong;
        }

        string folder = ComfyFolder(draft.Global);
        var candidate = new ComfyDraft { Kind = draft.Kind, Import = draft.Import, Name = name };
        foreach (string each in ComfyWizardNames(candidate))
        {
            if (ComfyWorkflowFile.NameProblem(each, ComfyRoots, folder, null) is { } problem)
            {
                return problem;
            }
        }

        draft.NameTouched |= !string.Equals(draft.Name, name, StringComparison.Ordinal);
        draft.Name = name;
        return null;
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

    /// <summary>
    /// One of the server's combo lists (read once a visit into the draft), or null with the reason on the status line (no server, unreachable,
    /// an empty list: <paramref name="empty"/> when given). The read runs as a task beside the pane, so Esc cancels a slow server.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ComfyChoicesAsync(ComfyDraft draft, string nodeClass, string input, CancellationToken cancellationToken, string? empty = null)
    {
        string key = nodeClass + "." + input;
        if (draft.Lists.TryGetValue(key, out var known))
        {
            return known;
        }

        if (_comfyClient() is not { } client)
        {
            Sink.Error(ComfyText.NoServer);
            return null;
        }

        var (done, read) = await ComfyBusyAsync(ComfyReadingLabel, token => client.ChoicesAsync(nodeClass, input, token), cancellationToken).ConfigureAwait(false);
        if (!done)
        {
            return null;
        }

        var (choices, error) = read;
        if (error is not null || choices.Count == 0)
        {
            Sink.Error(error ?? empty ?? ComfyText.BadAnswer("/object_info/" + nodeClass));
            return null;
        }

        draft.Lists[key] = choices;
        return choices;
    }

    /// <summary>The busy line while a list is read.</summary>
    public const string ComfyReadingLabel = "Reading the server's list…";

    private Task<int?> ComfyWizardPickAsync(string question, IReadOnlyList<string> choices, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(ComfyWizardTitle, choices.Select(Markup.Escape).ToList(), PickKeys) { Caption = question };
        if (!_pane.Enabled)
        {
            Flow.Notice(question);
        }

        return PickAsync(page, cursor, cancellationToken);
    }

    /// <summary>
    /// A value picked from a server list that filters as it is typed: <paramref name="noneRow"/> first when given (it picks null), the
    /// current value marked when the server doesn't list it (so Enter never swaps it for the first row), then the list. Null on Esc.
    /// </summary>
    private async Task<(string? Value, bool Ok)?> ComfyPickValueAsync(string title, string question, IReadOnlyList<string> list, string? current, string? noneRow, CancellationToken cancellationToken)
    {
        var values = new List<string?>();
        var rows = new List<string>();
        if (noneRow is not null)
        {
            values.Add(null);
            rows.Add(noneRow);
        }

        if (current is { Length: > 0 } && IndexOf(list, current) < 0)
        {
            values.Add(current);
            rows.Add(UntrustedText.Name(current) + ComfyNotOnServerMark);
        }

        foreach (string value in list)
        {
            values.Add(value);
            rows.Add(UntrustedText.Name(value));
        }

        int cursor = current is { Length: > 0 } ? values.FindIndex(v => v is not null && string.Equals(v, current, StringComparison.OrdinalIgnoreCase)) : 0;
        if (await PickFilteredAsync(rows, Math.Max(0, cursor), title, PickKeys, question, null, close: false, cancellationToken).ConfigureAwait(false) is not { } picked)
        {
            return null;
        }

        return (values[picked], true);
    }

    /// <summary>Writes the draft (<see cref="ComfyWorkflowFile.Add"/>; both of <c>both</c>, rolled back together), then, offered, its names into the list. (Changed, the first name); null when nothing was written.</summary>
    private (bool Changed, string Added)? ComfyWizardSave(ComfyDraft draft, bool offer)
    {
        if (ComfyWizardMissing(draft) is { } missing)
        {
            Sink.Error(missing);
            return null;
        }

        string folder = ComfyFolder(draft.Global);
        var names = ComfyWizardNames(draft);
        var written = new List<string>();
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
        {
            string json = ComfyDraftGraphJson(draft, name, draft.Kind == ComfyKind.Both ? i == 1 : draft.FromImage);
            if (ComfyWorkflowFile.Add(folder, ComfyRoots, name, json, ComfyDraftSidecar(draft, json)) is { } error)
            {
                foreach (string made in written)
                {
                    ComfyWorkflowFile.Delete(folder, made, bin: null);
                }

                Sink.Error(ComfyWorkflowAddFailed(name, error));
                return null;
            }

            written.Add(name);
        }

        foreach (string name in written)
        {
            Sink.Notice(ComfyWorkflowAdded(name, folder));
        }

        if (!offer)
        {
            return (true, written[0]);
        }

        // Each new name folded in once (2026-11-26, the review's #28), read inside the update so a change made meanwhile stands; null offers none (2026-10-01).
        Apply(SettingsField.ComfyWorkflowsOffered, d => d.ComfyWorkflowsOffered = written.Aggregate(
            d.ComfyWorkflowsOffered ?? [], (list, name) => ConnectionOfferedAfterSave(list, null, name, offer: true)));
        return (true, written[0]);
    }

    /// <summary>
    /// A file typed in the pane, read (2026-11-26, the review's #70/#99, one check for the import and the test pictures): refused when not a full path
    /// (never read against the app's own folder), on an unknown computer, not a file, failing <paramref name="judge"/>, or over <paramref name="cap"/>
    /// (the caller's: the two differ); the bytes, or why not as the words after the quoted path.
    /// </summary>
    internal static (byte[]? Bytes, string? Why) ComfyReadTypedFile(string path, long cap, Func<string, string?>? judge = null)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return (null, ComfyText.PathNotFull);
        }

        if (Unc.KnownUncHosts.IsUnknown(path))
        {
            return (null, ComfyText.PathUnknownComputer);
        }

        try
        {
            if (!File.Exists(path))
            {
                return (null, ComfyText.PathNotAFile);
            }

            if (judge?.Invoke(path) is { } refused)
            {
                return (null, refused);
            }

            return Files.RegularFiles.ReadAtMost(path, cap) is { } bytes ? (bytes, null) : (null, ComfyText.PathTooLarge(cap));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>What the draft still lacks before a save or a test, said as a sentence, or null.</summary>
    private static string? ComfyWizardMissing(ComfyDraft draft) =>
        draft.Editing ? null
        : draft.FromRun && draft.Imported is null ? ComfyPickRunFirst
        : draft.Import && draft.Imported is null ? ComfyText.SetFileFirst
        : !draft.Import && draft.Checkpoint.Length == 0 ? ComfyText.SetCheckpointFirst
        : draft.Name.Length == 0 ? ComfyText.SetNameFirst
        : null;

    // ── The list's rows (the manager's and the Botchat checklist's) ──────────

    /// <summary>The workflows the two comfy folders hold, for the checklist and its value (the profile's then the home's).</summary>
    private static IReadOnlyList<ComfyWorkflow> InstalledComfyWorkflows(string profileDirectory) => ScanComfyWorkflows(profileDirectory).Workflows;

    /// <summary><see cref="InstalledComfyWorkflows"/> with the files skipped, for the checklists' prune: a workflow that failed to load is still there (2026-10-04).</summary>
    private static (IReadOnlyList<ComfyWorkflow> Workflows, IReadOnlyList<ComfyWorkflowProblem> Problems) ScanComfyWorkflows(string profileDirectory) =>
        new ComfyWorkflowCatalog(() => [AppSettings.ProfileComfyDirectoryOf(profileDirectory), AppSettings.GlobalComfyDirectoryOf(Profiles.HomeOf(profileDirectory))]).Scan();

    /// <summary>The offered count: <c>K of N</c>, or <c>none of N</c> — before any is ticked too (2026-10-01: null offers none). Pinned.</summary>
    public static string ComfyOfferedValue(IReadOnlyList<string>? offered, IReadOnlyList<ComfyWorkflow> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        int kept = ComfyWorkflowCatalog.Offered(installed, offered).Count;
        return (kept == 0 ? "none" : Invariant(kept)) + " of " + Invariant(installed.Count);
    }

    /// <summary>
    /// The widths a ComfyUI list lines its columns up to (2026-10-04, the user's ask: the family · shape · size run wandered row to row): the
    /// name's (with its two-space gap, capped at <see cref="ComfyNameCap"/> since 2026-11-25), the family's, the shape's, and the default size's
    /// width and height digits, so the <c>×</c> of every size falls in one column.
    /// </summary>
    public sealed record ComfyOfferedColumns(int Name, int Family, int Shape, int SizeWidth, int SizeHeight = 0)
    {
        /// <summary>The columns over every workflow listed. Pure.</summary>
        public static ComfyOfferedColumns Of(IReadOnlyList<ComfyWorkflow> workflows)
        {
            ArgumentNullException.ThrowIfNull(workflows);
            return workflows.Count == 0
                ? new(2, 0, 0, 0)
                : new(
                    Math.Min(workflows.Max(w => TextCells.Width(w.Name)), ComfyNameCap) + 2,
                    workflows.Max(w => ComfyFamilies.Name(w.Family).Length),
                    workflows.Max(w => ComfyText.InputShape(w).Length),
                    workflows.Max(w => Invariant(w.Defaults.Width).Length),
                    workflows.Max(w => Invariant(w.Defaults.Height).Length));
        }
    }

    /// <summary>
    /// One list row: the mark, then <paramref name="flag"/> (the check's <c>!</c> or a blank) when given, the name (cut to the column), then dim
    /// in columns (<see cref="ComfyOfferedColumns"/>, two spaces apart) the family, the shape, the default size (blank where the graph takes
    /// none, left out with <paramref name="size"/> false) and <paramref name="scope"/> when given. Pinned.
    /// </summary>
    public static string ComfyOfferedRow(ComfyWorkflow workflow, bool offered, ComfyOfferedColumns columns, string? scope = null, bool size = true, char? flag = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(columns);
        var d = workflow.Defaults;
        string note = ComfyFamilies.Name(workflow.Family).PadRight(columns.Family) + "  " + ComfyText.InputShape(workflow).PadRight(columns.Shape);
        if (size)
        {
            int cells = columns.SizeWidth + 1 + Math.Max(columns.SizeHeight, 1);
            string drawn = workflow.Placeholders.Contains(ComfyWorkflow.WidthKey) ? Invariant(d.Width).PadLeft(columns.SizeWidth) + "×" + Invariant(d.Height) : "";
            note += "  " + (scope is null ? drawn : drawn.PadRight(cells));
        }

        if (scope is not null)
        {
            note += "  " + scope;
        }

        string head = (offered ? "[x] " : "[ ] ") + (flag is { } f ? f + " " : "") + TextCells.Cell(workflow.Name, columns.Name - 2).PadRight(columns.Name);
        return Markup.Escape(head) + Theme.DimMarkup(note.TrimEnd());
    }

    /// <summary>
    /// A checklist of every installed workflow saved to a list of names (<c>Botchat limited workflows</c>; <c>ComfyUI workflows offered</c>'s too
    /// until 2026-11-25, when the workflow manager took that one): each row with its kind, family and size (<see cref="ComfyOfferedRow"/>), the
    /// names <paramref name="read"/> gives ticked. Enter or Space flips one, A every one installed now, N none, each saved at once through
    /// <paramref name="write"/>; a button that changes nothing saves nothing. A ticked name no longer installed is dropped as the list opens
    /// (2026-10-04), unless a file of that name failed to load.
    /// </summary>
    private async Task<bool> EditWorkflowChecklistAsync(SettingsField field, Func<AppSettingsData, List<string>?> read, Action<AppSettingsData, List<string>> write, CancellationToken cancellationToken)
    {
        bool changed = false;
        int cursor = 0;
        while (true)
        {
            var (installed, problems) = ScanComfyWorkflows(_settings.ProfileDirectory);
            if (installed.Count == 0)
            {
                Sink.Error(ComfyText.NoWorkflows(ComfyRoots));
                return changed;
            }

            var broken = problems.Select(p => Path.GetFileNameWithoutExtension(p.FilePath)).ToList();
            changed |= PruneStale(field, read(_settings.Current), OfferedNames.Stale(read(_settings.Current), installed.Select(w => w.Name), broken, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase, write);
            var current = _settings.Current;
            var chosen = read(current) ?? [];
            var on = ComfyWorkflowCatalog.Offered(installed, chosen).Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = ComfyOfferedColumns.Of(installed);
            var page = new MenuPage(Crumb(FieldName(field)), installed.Select(w => ComfyOfferedRow(w, on.Contains(w.Name), columns)).ToList(), ToggleKeys)
            {
                SpaceToggles = true,
            };
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
            var next = pick.Button == SelectAllIndex ? installed.Select(w => w.Name).ToList()
                : pick.Button == SelectNoneIndex ? []
                : installed.Select(w => w.Name).Where(n => on.Contains(n) != string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count == on.Count && next.All(on.Contains))
            {
                continue;   // a button that changes nothing saves nothing (null and empty both offer none, 2026-10-01)
            }

            // Saved but not installed now survived the prune above: a file of that name failed to load. It stays until mended (2026-10-04).
            next.AddRange(chosen.Where(n => !installed.Any(w => string.Equals(w.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))));
            Apply(field, d => write(d, next));
            changed = true;
        }
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);
}
