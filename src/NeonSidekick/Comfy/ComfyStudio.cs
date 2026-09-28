using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Comfy;

/// <summary>
/// One generation asked for, by the model (<c>generate_image</c>) or the user (<c>/imagine</c>). Null numbers and a
/// null <see cref="Negative"/> take the workflow's fallbacks; <see cref="Verbatim"/> (2026-09-24, the user's ask: their
/// own <c>score_9, …</c> prompts sent as typed) drops the family's default negative, so nothing is added to what was
/// given — a negative the workflow's sidecar names is the user's own setting and still applies. <see cref="AnyWorkflow"/>
/// (later on 2026-09-24, <c>/imagine</c>'s) lets a named workflow be any installed one, not only those
/// <c>ComfyUI workflows offered</c> gives the model; with no name the offered ones are still the choice.
/// <see cref="Images"/> (later still on 2026-09-24, the face swap; one <c>Image</c> before) are the input pictures in
/// slot order — <c>image</c>, <c>image2</c>, <c>image3</c> — each a sandbox path or a pasted picture's label; null or
/// empty is none.
/// </summary>
public sealed record ComfyRequest(
    string Prompt,
    string? Workflow = null,
    string? Negative = null,
    bool Verbatim = false,
    long? Seed = null,
    int? Width = null,
    int? Height = null,
    int? Steps = null,
    double? Cfg = null,
    double? Denoise = null,
    IReadOnlyList<string>? Images = null,
    int Count = 1,
    bool AnyWorkflow = false,
    string? NegativeExtra = null)
{
    /// <summary>How many input pictures were given.</summary>
    public int ImageCount => Images?.Count ?? 0;
}

/// <summary>What a generation came to: the result text (an <c>Error:</c> sentence on failure) and the saved pictures as the model sees them.</summary>
public sealed record ComfyGeneration(string Text, IReadOnlyList<ImageAttachment> Images)
{
    public bool Ok => Images.Count > 0;
}

/// <summary>
/// The image tools' engine (2026-09-24): picks the workflow, uploads the input picture, fills the template, runs it on
/// the ComfyUI server named by <c>ComfyUI URL</c> (<see cref="ComfyClient"/>) and saves what comes back under the
/// working directory's <c>ComfyUI output folder</c> — <c>comfy_images\pony-txt2img-1234.png</c>, a number added when the
/// name is taken, never an overwrite. One engine for the tool and <c>/imagine</c>, so a picture is made, named and
/// reported the same way whoever asked. The client is kept while the URL stays the same.
/// </summary>
public sealed class ComfyStudio : IDisposable
{
    private const string Category = "Comfy";

    /// <summary>
    /// The most pictures one call makes (<c>count</c>) as the settings stand: <c>ComfyUI max pictures per call</c> clamped
    /// into its range (later on 2026-09-24; a fixed 4 before). Pure.
    /// </summary>
    public static int MaxCountOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.ComfyMaxPicturesPerCall, AppSettingsData.MinComfyMaxPicturesPerCall, AppSettingsData.MaxComfyMaxPicturesPerCall);
    }

    /// <summary>The ranges the numbers are held to — wide, since a workflow may be anything, but no typo reaches the GPU.</summary>
    public const int MinSide = 64;
    public const int MaxSide = 4096;
    public const int MaxSteps = 200;
    public const double MaxCfg = 30;

    private readonly ComfyWorkflowCatalog _catalog;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<Uri, ComfyClient> _clientFactory;
    private readonly Random _random;
    private readonly object _gate = new();
    private readonly Func<int, PastedPicture?>? _pasted;
    private readonly TimeProvider _time;
    // A pasted picture already written for an input (later still on 2026-09-24): its number → the saved relative path,
    // so the same paste used twice is one file. Under _gate.
    private readonly Dictionary<int, string> _pastedSaved = new();
    private ComfyClient? _client;
    private string? _clientUrl;

    /// <param name="clientFactory">Makes the client for a URL; null = <see cref="ComfyClient"/> over its own transport. Tests pass one over a stub handler.</param>
    /// <param name="pasted">The session's pasted pictures at full size by number (<c>UI.PasteBlocks.Original</c> in the app, later still on 2026-09-24): what an <c>image</c> of <c>[Image #N]</c> reads; null = no paste is ever an input (headless, tests that pass none).</param>
    /// <param name="time">The clock a clipboard paste's name is stamped from (<see cref="PastedStem"/>); null = the system's.</param>
    public ComfyStudio(ComfyWorkflowCatalog catalog, WorkingDirectory files, Func<AppSettingsData> effective, Func<Uri, ComfyClient>? clientFactory = null, Random? random = null, Func<int, PastedPicture?>? pasted = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _clientFactory = clientFactory ?? (url => new ComfyClient(url));
        _random = random ?? Random.Shared;
        _pasted = pasted;
    }

    public ComfyWorkflowCatalog Catalog => _catalog;

    /// <summary>Whether <c>ComfyUI reinforce negatives</c> is on, as the settings stand now.</summary>
    public bool ReinforceNegatives => _effective().ComfyReinforceNegatives;

    /// <summary>
    /// Whether <paramref name="request"/>'s <c>negative_extra</c> is appended: the setting on, some given, the model's own prompt
    /// (not verbatim), no negative set for the call (that one is the whole negative), and a workflow that takes it
    /// (<see cref="ComfyWorkflow.TakesReinforcement"/>). Pure.
    /// </summary>
    public static bool Reinforces(ComfyRequest request, ComfyWorkflow workflow, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(effective);
        return effective.ComfyReinforceNegatives && !string.IsNullOrWhiteSpace(request.NegativeExtra) && !request.Verbatim && request.Negative is null && workflow.TakesReinforcement;
    }

    /// <summary><paramref name="extra"/> after <paramref name="negative"/>, one comma between them, either side's stray commas trimmed. Pure.</summary>
    public static string JoinNegative(string negative, string extra)
    {
        ArgumentNullException.ThrowIfNull(negative);
        ArgumentNullException.ThrowIfNull(extra);
        string head = negative.Trim().TrimEnd(',').TrimEnd();
        string tail = extra.Trim().Trim(',').Trim();
        return head.Length == 0 ? tail : tail.Length == 0 ? head : head + ", " + tail;
    }

    /// <summary>The pictures-per-call cap as the settings stand now (<see cref="MaxCountOf"/>).</summary>
    public int MaxCount => MaxCountOf(_effective());

    /// <summary>The installed workflows the model is offered: <c>ComfyUI workflows offered</c> as the settings stand (later on 2026-09-24).</summary>
    public IReadOnlyList<ComfyWorkflow> OfferedWorkflows() => ComfyWorkflowCatalog.Offered(_catalog.Workflows, _effective().ComfyWorkflowsOffered);

    /// <summary>The server as the settings stand, or null when <c>ComfyUI URL</c> is empty or no http(s) URL.</summary>
    public static Uri? ServerOf(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Uri.TryCreate(effective.ComfyUrl?.Trim(), UriKind.Absolute, out var url) && Web.WebFetcher.IsHttp(url) ? url : null;
    }

    /// <summary>The client for the URL in force, made again when the URL changed; null without one.</summary>
    public ComfyClient? Client()
    {
        if (ServerOf(_effective()) is not { } url)
        {
            return null;
        }

        lock (_gate)
        {
            if (_client is null || !string.Equals(_clientUrl, url.AbsoluteUri, StringComparison.Ordinal))
            {
                _client?.Dispose();
                _client = _clientFactory(url);
                _clientUrl = url.AbsoluteUri;
            }

            return _client;
        }
    }

    /// <summary>
    /// The workflow for <paramref name="request"/>: the one named (any case), else the only one of the right kind —
    /// taking as many input pictures as <paramref name="images"/> (later still on 2026-09-24; image or no image before)
    /// — else an error naming the candidates.
    /// </summary>
    public static (ComfyWorkflow? Workflow, string? Error) Pick(IReadOnlyList<ComfyWorkflow> workflows, string? name, int images)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var named = workflows.FirstOrDefault(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            return named is null ? (null, ComfyText.UnknownWorkflow(name.Trim(), workflows)) : (named, null);
        }

        var fit = workflows.Where(w => w.ImageCount == images).ToList();
        return fit.Count == 1 ? (fit[0], null)
            : workflows.Count == 1 ? (workflows[0], null)
            : (null, ComfyText.WhichWorkflow(fit.Count > 0 ? fit : workflows));
    }

    /// <summary>
    /// Runs <paramref name="request"/>: every refusal (no server, no workflow, a bad number, a bad input picture,
    /// ComfyUI's own) is an <c>Error:</c> text with no picture; the caller's cancellation is rethrown.
    /// <paramref name="narrow"/> (2026-09-27, <c>/botchat</c>'s bots: their two workflows alone) chooses the workflows from every
    /// installed one — <c>ComfyUI workflows offered</c> has no say (later that day, the user's call) — before the pick, so a name
    /// outside them is unknown as any other; never applied to <c>/imagine</c>'s any-by-name.
    /// </summary>
    public async Task<ComfyGeneration> GenerateAsync(ComfyRequest request, CancellationToken cancellationToken, Func<IReadOnlyList<ComfyWorkflow>, IReadOnlyList<ComfyWorkflow>>? narrow = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effective = _effective();
        if (Client() is not { } client)
        {
            return Fail(ComfyText.NoServer);
        }

        var installed = _catalog.Workflows;
        if (installed.Count == 0)
        {
            return Fail(ComfyText.NoWorkflows(_catalog.Roots));
        }

        // The model sees the offered ones only (later on 2026-09-24); /imagine may name any installed one.
        bool anyByName = request.AnyWorkflow && !string.IsNullOrWhiteSpace(request.Workflow);
        var workflows = anyByName ? installed
            : narrow is not null ? narrow(installed)
            : ComfyWorkflowCatalog.Offered(installed, effective.ComfyWorkflowsOffered);
        if (workflows.Count == 0)
        {
            return Fail(ComfyText.NoneOffered);
        }

        var inputs = request.Images ?? [];
        int given = inputs.Count;
        var (workflow, pickError) = Pick(workflows, request.Workflow, given);
        if (workflow is null)
        {
            return Fail(pickError!);
        }

        // Only a workflow that has a {{prompt}} needs one (later still on 2026-09-24: a face swap has nothing to say).
        if (workflow.TakesPrompt && string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Fail(ComfyText.NoPrompt);
        }

        if (given != workflow.ImageCount)
        {
            return Fail(given > 0 && !workflow.TakesImage ? ComfyText.TakesNoImage(workflow.Name)
                : given == 0 && workflow.ImageCount == 1 ? ComfyText.NeedsImage(workflow.Name)
                : ComfyText.WrongImageCount(workflow, given));
        }

        int maxCount = MaxCountOf(effective);
        if (Check(request, maxCount) is { } rangeError)
        {
            return Fail(rangeError);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(effective.ComfyTimeoutSeconds, AppSettingsData.MinComfyTimeoutSeconds, AppSettingsData.MaxComfyTimeoutSeconds));
        // Each input picture read and uploaded in slot order (later still on 2026-09-24: a face swap's target, then its face).
        var uploaded = new List<string>(given);
        var inputNotes = new List<string>();
        foreach (string image in inputs)
        {
            var input = ReadInput(image, effective.ComfyOutputFolder);
            if (input.Error is not null)
            {
                return Fail(input.Error);
            }

            if (input.Saved is not null && ComfyText.TryPastedLabel(image, out int pastedNumber))
            {
                // Where the paste was written, so the model can name the file from now on.
                string note = ComfyText.PastedInput(input.Saved, pastedNumber);
                if (!inputNotes.Contains(note))
                {
                    inputNotes.Add(note);
                }
            }

            var (name, uploadError) = await client.UploadAsync(input.Bytes!, input.FileName!, timeout, cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                return Fail(uploadError!);
            }

            uploaded.Add(name);
        }

        string? inputNote = inputNotes.Count == 0 ? null : string.Join("\n", inputNotes);

        var defaults = workflow.Defaults;
        string negative = request.Negative ?? (request.Verbatim ? workflow.Negative ?? "" : defaults.Negative);
        if (Reinforces(request, workflow, effective))
        {
            // The model's reinforcing tags after the workflow's own negative (later still on 2026-09-24).
            negative = JoinNegative(negative, request.NegativeExtra!);
        }
        long seed = request.Seed ?? NextSeed();
        int count = Math.Clamp(request.Count, 1, maxCount);
        var saved = new List<string>();
        var images = new List<ImageAttachment>();
        ComfyParameters? parameters = null;
        for (int i = 0; i < count; i++)
        {
            long thisSeed = seed + i;
            var values = new ComfyValues(request.Prompt, negative, thisSeed, request.Width, request.Height, request.Steps, request.Cfg, request.Denoise, uploaded.Count == 0 ? null : uploaded);
            var graph = workflow.Fill(values);
            // The first run's parameters as sent (2026-09-25): the report's seed is the first one too.
            parameters ??= ComfyWorkflow.ReadParameters(graph);
            var run = await client.RunAsync(graph, timeout, cancellationToken).ConfigureAwait(false);
            if (!run.Ok)
            {
                // What was saved before the failure stays saved, and is said.
                return saved.Count == 0 ? Fail(run.Error!) : new ComfyGeneration(Report(workflow, seed, request, saved, negative, images.Count > 0, inputNote, parameters) + "\n" + run.Error, images);
            }

            for (int j = 0; j < run.Images.Count; j++)
            {
                var picture = run.Images[j];
                string extension = Path.GetExtension(picture.FileName) is { Length: > 0 } e ? e.ToLowerInvariant() : ".png";
                string stem = SafeName(workflow.Name) + "-" + thisSeed.ToString(CultureInfo.InvariantCulture) + (run.Images.Count > 1 ? "-" + (j + 1).ToString(CultureInfo.InvariantCulture) : "");
                var (relative, saveError) = Save(effective.ComfyOutputFolder, stem, extension, picture.Bytes);
                if (relative is null)
                {
                    return saved.Count == 0 ? Fail(saveError!) : new ComfyGeneration(Report(workflow, seed, request, saved, negative, images.Count > 0, inputNote, parameters) + "\n" + saveError, images);
                }

                saved.Add(relative);
                if (ImageFile.TryLoad(picture.Bytes, relative, out var attachment, out _) && attachment is not null)
                {
                    images.Add(attachment with { Path = relative });
                }
            }
        }

        DiagnosticLog.Info(Category, $"Generated {saved.Count.ToString(CultureInfo.InvariantCulture)} picture(s) with {workflow.Name}, seed {seed.ToString(CultureInfo.InvariantCulture)}.");
        return new ComfyGeneration(Report(workflow, seed, request, saved, negative, images.Count > 0, inputNote, parameters), images);
    }

    private static string Report(ComfyWorkflow workflow, long seed, ComfyRequest request, IReadOnlyList<string> saved, string negative, bool attached, string? input, ComfyParameters? parameters)
    {
        var d = workflow.Defaults;
        int width = request.Width ?? d.Width;
        int height = request.Height ?? d.Height;
        // What the graph carries wins (a template may hardcode its steps, 2026-09-25); what was asked for fills the rest.
        // Denoise, the sampler and the scheduler are the graph's alone: a graph with none has none to report.
        var sent = parameters is null ? null : parameters with
        {
            Width = parameters.Width ?? width,
            Height = parameters.Height ?? height,
            Steps = parameters.Steps ?? request.Steps ?? d.Steps,
            Cfg = parameters.Cfg ?? request.Cfg ?? d.Cfg,
            Seed = parameters.Seed ?? seed,
        };
        // A prompt the workflow has no {{prompt}} for never reached the picture, so it is not reported as sent.
        return ComfyText.Generated(workflow.Name, seed, width, height, saved, workflow.TakesPrompt ? request.Prompt : "", negative, attached, input, sent);
    }

    private static ComfyGeneration Fail(string text) => new(text, []);

    private long NextSeed()
    {
        lock (_gate)
        {
            // ComfyUI's seeds are unsigned 64-bit; a positive 48-bit one is plenty and survives every JSON reader.
            return _random.NextInt64(1, 1L << 48);
        }
    }

    /// <summary>The first number out of its range, as the error; null when all fit. <paramref name="maxCount"/> is <see cref="MaxCountOf"/>'s.</summary>
    public static string? Check(ComfyRequest request, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        string side = $"{MinSide.ToString(CultureInfo.InvariantCulture)} to {MaxSide.ToString(CultureInfo.InvariantCulture)}";
        if (request.Width is { } w && (w < MinSide || w > MaxSide)) return ComfyText.OutOfRange("width", N(w), side);
        if (request.Height is { } h && (h < MinSide || h > MaxSide)) return ComfyText.OutOfRange("height", N(h), side);
        if (request.Steps is { } s && (s < 1 || s > MaxSteps)) return ComfyText.OutOfRange("steps", N(s), "1 to " + N(MaxSteps));
        if (request.Cfg is { } c && (!double.IsFinite(c) || c < 0 || c > MaxCfg)) return ComfyText.OutOfRange("cfg", c.ToString(CultureInfo.InvariantCulture), "0 to " + MaxCfg.ToString(CultureInfo.InvariantCulture));
        if (request.Denoise is { } d && (!double.IsFinite(d) || d < 0 || d > 1)) return ComfyText.OutOfRange("denoise", d.ToString(CultureInfo.InvariantCulture), "0 to 1");
        if (request.Seed is { } seed && seed < 0) return ComfyText.OutOfRange("seed", seed.ToString(CultureInfo.InvariantCulture), "0 or more");
        if (request.Count < 1 || request.Count > maxCount) return ComfyText.OutOfRange("count", N(request.Count), "1 to " + N(maxCount));
        return null;

        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The input picture's own bytes — never the model's downscale — and its file name, or the reason it cannot be used.
    /// A pasted picture's label (<c>[Image #N]</c>, later still on 2026-09-24) is <see cref="ReadPasted"/>'s, with where it was saved.
    /// </summary>
    private (byte[]? Bytes, string? FileName, string? Saved, string? Error) ReadInput(string path, string? outputFolder)
    {
        if (ComfyText.TryPastedLabel(path, out int number))
        {
            return ReadPasted(number, outputFolder);
        }

        var outcome = _files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return (null, null, null, FileText.Error(outcome, path, "read"));
        }

        try
        {
            if (!File.Exists(full))
            {
                return (null, null, null, ComfyText.BadInput(path, "no such file"));
            }

            if (!ImageFile.IsImagePath(full))
            {
                return (null, null, null, ComfyText.BadInput(path, "not a png, jpg, gif, webp or bmp file"));
            }

            if (new FileInfo(full).Length > ImageFile.MaxFileBytes)
            {
                return (null, null, null, ComfyText.BadInput(path, "over 20 MB"));
            }

            return (File.ReadAllBytes(full), Path.GetFileName(full), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, null, ComfyText.BadInput(path, ex.Message));
        }
    }

    /// <summary>
    /// The output folder's subfolder the pasted inputs are saved in (later still on 2026-09-24, the user's call): inputs apart
    /// from outputs, a dot-folder (<c>pasted</c> until later that day) so the listings that leave dot-entries out leave it too. Pinned.
    /// </summary>
    public const string PastedFolderName = ".pasted";

    /// <summary>
    /// A clipboard paste's file stem, stamped with the local time it was first used (later still on 2026-09-24, the
    /// user's call over <c>pasted-N</c>, whose N began again with every run): <c>pasted-20260924-153012</c>. Pinned.
    /// </summary>
    public static string PastedStem(DateTimeOffset local) => "pasted-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// A pasted picture as the input (later still on 2026-09-24, the user's call: saved when used): the paste's own bytes
    /// — a clipboard picture before the 2048 downscale the model saw, a dropped file read again — written into the
    /// output folder's <see cref="PastedFolderName"/> subfolder (<see cref="Save"/>: a free name, the sandbox's say) the
    /// first time it is used, a clipboard one as <see cref="PastedStem"/> with the extension its bytes say, a dropped
    /// one under its own name; then uploaded from there. A second use of the same paste reuses that file while it is
    /// still there. The saved path comes back so the result can name it.
    /// </summary>
    private (byte[]? Bytes, string? FileName, string? Saved, string? Error) ReadPasted(int number, string? outputFolder)
    {
        if (_pasted?.Invoke(number) is not { } picture)
        {
            return (null, null, null, ComfyText.NoPastedPicture(number));
        }

        if (picture.Bytes.Length > ImageFile.MaxFileBytes)
        {
            return (null, null, null, ComfyText.BadInput(NeonSidekick.UI.PasteBlocks.ImageLabel(number), "over 20 MB"));
        }

        string? saved;
        lock (_gate)
        {
            _pastedSaved.TryGetValue(number, out saved);
        }

        if (saved is null || _files.Resolve(saved, forWrite: false, out string full) != FileOutcome.Ok || !File.Exists(full))
        {
            string stem = picture.FileName is { Length: > 0 } own ? SafeName(Path.GetFileNameWithoutExtension(own)) : PastedStem(_time.GetLocalNow());
            string extension = Path.GetExtension(picture.FileName) is { Length: > 0 } e ? e.ToLowerInvariant() : ImageFile.ExtensionOf(picture.Bytes);
            var (relative, error) = Save(Path.Combine(OutputFolder(outputFolder), PastedFolderName), stem, extension, picture.Bytes);
            if (relative is null)
            {
                return (null, null, null, error);
            }

            lock (_gate)
            {
                _pastedSaved[number] = relative;
            }

            saved = relative;
        }

        return (picture.Bytes, Path.GetFileName(saved), saved, null);
    }

    /// <summary>The output folder as a sandbox path: the setting trimmed, <c>.</c> for empty.</summary>
    public static string OutputFolder(string? setting) => string.IsNullOrWhiteSpace(setting) ? "." : setting.Trim();

    /// <summary>Writes the picture under a free name — <c>stem.png</c>, then <c>stem-2.png</c>, … — and returns its relative path, or the sandbox's refusal.</summary>
    private (string? Relative, string? Error) Save(string? folder, string stem, string extension, byte[] bytes)
    {
        string dir = OutputFolder(folder);
        for (int n = 1; n < 1000; n++)
        {
            string name = stem + (n == 1 ? "" : "-" + n.ToString(CultureInfo.InvariantCulture)) + extension;
            var result = _files.WriteBytes(dir == "." ? name : Path.Combine(dir, name), bytes, overwrite: false);
            if (result.Outcome == FileOutcome.Ok)
            {
                return (result.Relative, null);
            }

            if (result.Outcome != FileOutcome.Exists)
            {
                return (null, FileText.Error(result.Outcome, result.Relative, "write", result.Detail));
            }
        }

        return (null, FileText.Error(FileOutcome.Exists, Path.Combine(dir, stem + extension), "write"));
    }

    /// <summary>A workflow name as a file name's stem: characters a file name cannot hold become <c>-</c>.</summary>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '-' : c).ToArray());
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
