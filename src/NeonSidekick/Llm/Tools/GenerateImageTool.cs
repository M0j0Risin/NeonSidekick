using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Comfy;
using NeonSidekick.Files;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>generate_image(prompt?, workflow?, negative?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?)</c>
/// (2026-09-24, the user's ask: "what can we do with comfyui?"): pictures from one of the user's ComfyUI workflows
/// (<see cref="ComfyWorkflowCatalog"/>) on their server, saved under the working directory, and shown to the model in
/// the next message the <c>view_image</c> way (<see cref="ToolImageResult"/>). The description is built from the
/// catalog at each request — the workflows one line each, and the prompt style of each family among them
/// (<see cref="ComfyText.Describe"/>) — so the model writes a Pony Diffusion XL prompt for a Pony workflow and prose
/// for a Flux one without a round trip to ask. A prompt the user wrote is passed through as typed with
/// <c>verbatim</c> (the user's ask: "we will be able to send standard prompts too, as in score_9, etc."), which also
/// keeps the family's default negative off. The work is <see cref="ComfyStudio"/>'s, shared with <c>/imagine</c>.
/// Later still on 2026-09-24 (the user's ask: a face swap, "replace the face in the first image with the face from the
/// second image"): <c>image2</c> and <c>image3</c> for a workflow taking more than one picture, and <c>prompt</c> no
/// longer required, since such a workflow may have none.
/// </summary>
public sealed class GenerateImageTool : AIFunction
{
    public const string ToolName = "generate_image";

    public const string PromptArgument = "prompt";
    public const string WorkflowArgument = "workflow";
    public const string NegativeArgument = "negative";
    public const string NegativeExtraArgument = "negative_extra";
    public const string VerbatimArgument = "verbatim";
    public const string WidthArgument = "width";
    public const string HeightArgument = "height";
    public const string SeedArgument = "seed";
    public const string StepsArgument = "steps";
    public const string CfgArgument = "cfg";
    public const string DenoiseArgument = "denoise";
    public const string ImageArgument = "image";
    public const string Image2Argument = "image2";
    public const string Image3Argument = "image3";

    /// <summary>The input-picture arguments in slot order, the placeholders' names (<see cref="ComfyWorkflow.ImageKeys"/>).</summary>
    public static readonly IReadOnlyList<string> ImageArguments = [ImageArgument, Image2Argument, Image3Argument];
    public const string CountArgument = "count";

    /// <summary>
    /// The tool's name and parameter names, read from its schema (2026-09-30, code review): what the <c>/botchat</c> picture writer,
    /// never offered the tool, catches a <c>generate_image</c> call written as text by — its <c>prompt</c> is the one wanted.
    /// </summary>
    public static readonly (string Name, IReadOnlyList<string> Parameters) WrittenForm = (ToolName, Assistant.ParameterNames(SchemaFor(1)));

    // The schema's text; its count line quotes the cap in force (later on 2026-09-24), put in for {MAX}.
    private const string SchemaText =
        """
        {
          "type": "object",
          "properties": {
            "prompt": { "type": "string", "description": "The positive prompt, in the dialect of the workflow's family — or the user's own prompt, exactly as they gave it, with verbatim true. Leave it out only for a workflow marked 'no prompt'." },
            "workflow": { "type": "string", "description": "Which workflow to run, by name from the list; a ^name in the user's message is that workflow (pass it without the ^). May be left out when only one fits (one taking an image when image is given, one not taking an image otherwise)." },
            "negative": { "type": "string", "description": "The negative prompt. Left out, the workflow's default is used (none with verbatim true unless the workflow names one)." },
            "negative_extra": { "type": "string", "description": "A few tags appended to the workflow's own negative to reinforce your prompt: the opposites of what it asks, where the model tends to drift. Only for your own prompts, and only for workflows marked 'negative reinforced'." },
            "verbatim": { "type": "boolean", "description": "True when prompt (and negative) are the user's own text passed through unchanged; nothing is added to them." },
            "width": { "type": "integer", "description": "Width in pixels (64 to 4096); left out, the workflow's size. Only where the workflow has a width placeholder." },
            "height": { "type": "integer", "description": "Height in pixels (64 to 4096); left out, the workflow's size." },
            "seed": { "type": "integer", "description": "The noise seed; left out, a random one (the result reports it, to reproduce or vary a picture)." },
            "steps": { "type": "integer", "description": "Sampling steps (1 to 200); left out, the workflow's." },
            "cfg": { "type": "number", "description": "Classifier-free guidance scale (0 to 30); left out, the workflow's." },
            "denoise": { "type": "number", "description": "How much of an input image to repaint, 0 to 1 (image workflows): about 0.3 keeps it close, 0.8 changes it a lot." },
            "image": { "type": "string", "description": "An input picture, for a workflow that takes one (img2img, upscale, inpaint): a path under the working directory, or a picture the user pasted by its label ([Image #1]) — sent at full size and saved beside the output." },
            "image2": { "type": "string", "description": "The second input picture, for a workflow taking two or more (a face swap's face, a second picture to compose), in the role its line names; the same forms as image." },
            "image3": { "type": "string", "description": "The third input picture, for a workflow taking three." },
            "count": { "type": "integer", "description": "How many pictures, 1 to {MAX}, each with the next seed. Default 1." }
          }
        }
        """;

    private readonly ComfyStudio _studio;
    private readonly Func<IReadOnlyList<ComfyWorkflow>, IReadOnlyList<ComfyWorkflow>>? _narrow;

    // The schema for the cap last asked for: rebuilt only when the row changes (ViewImageTool's shape).
    private int _schemaLimit;
    private JsonElement _schema;

    /// <param name="studio">The screen's ComfyUI studio.</param>
    /// <param name="narrow">
    /// The workflows this tool may use out of every installed one (2026-09-27, the user's ask: <c>/botchat</c>'s bots get
    /// their own workflows alone — the two pickers' then, the Botchat ComfyUI rows' set since 2026-10-04): read at every description
    /// and every call; null is every offered workflow, the main chat's tool.
    /// </param>
    public GenerateImageTool(ComfyStudio studio, Func<IReadOnlyList<ComfyWorkflow>, IReadOnlyList<ComfyWorkflow>>? narrow = null)
    {
        _studio = studio ?? throw new ArgumentNullException(nameof(studio));
        _narrow = narrow;
    }

    public override string Name => ToolName;

    /// <summary>The catalog's workflows and their families' prompt guides, as the catalog stands now.</summary>
    public override string Description
    {
        get
        {
            var workflows = _narrow is null ? _studio.OfferedWorkflows() : _narrow(_studio.Catalog.Workflows);
            return workflows.Count == 0 ? ComfyText.DescribeEmpty : ComfyText.Describe(workflows, _studio.ReinforceNegatives);
        }
    }

    /// <summary>The cap in force: <c>ComfyUI max pictures per call</c>, clamped.</summary>
    public int MaxCount => _studio.MaxCount;

    public override JsonElement JsonSchema
    {
        get
        {
            int limit = MaxCount;
            if (_schema.ValueKind == JsonValueKind.Undefined || limit != _schemaLimit)
            {
                _schema = SchemaFor(limit);
                _schemaLimit = limit;
            }

            return _schema;
        }
    }

    /// <summary>The schema under the given cap: the <c>count</c> description quotes it. Pinned.</summary>
    public static JsonElement SchemaFor(int maxCount) =>
        ToolSchema.Parse(SchemaText.Replace("{MAX}", maxCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

    /// <summary>The call's arguments as a request, or the sentence for the first one that does not read. Pure.</summary>
    public static (ComfyRequest? Request, string? Error) Read(AIFunctionArguments arguments, int maxCount = AppSettingsData.DefaultComfyMaxPicturesPerCall)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadBoolean(arguments, VerbatimArgument, out bool? verbatim, out string raw))
        {
            return (null, FileText.BadBoolean(VerbatimArgument, raw));
        }

        int? width = null, height = null, steps = null, count = null;
        foreach (var (name, set) in new (string, Action<int?>)[]
        {
            (WidthArgument, v => width = v),
            (HeightArgument, v => height = v),
            (StepsArgument, v => steps = v),
            (CountArgument, v => count = v),
        })
        {
            if (!ToolArguments.TryReadInt32(arguments, name, out int? value, out raw))
            {
                return (null, ClockText.BadInteger(name, raw));
            }

            set(value);
        }

        if (!ToolArguments.TryReadInt64(arguments, SeedArgument, out long? seed, out raw))
        {
            return (null, ClockText.BadInteger(SeedArgument, raw));
        }

        if (!ToolArguments.TryReadDouble(arguments, CfgArgument, out double? cfg, out raw))
        {
            return (null, BadNumber(CfgArgument, raw));
        }

        if (!ToolArguments.TryReadDouble(arguments, DenoiseArgument, out double? denoise, out raw))
        {
            return (null, BadNumber(DenoiseArgument, raw));
        }

        string negative = ToolArguments.ReadString(arguments, NegativeArgument);
        string negativeExtra = ToolArguments.ReadString(arguments, NegativeExtraArgument);
        string workflow = ToolArguments.ReadString(arguments, WorkflowArgument);
        // The pictures in slot order; a later one without the one before is a slip, not a silent shift.
        var images = new List<string>();
        for (int i = 0; i < ImageArguments.Count; i++)
        {
            string image = ToolArguments.ReadString(arguments, ImageArguments[i]).Trim();
            if (image.Length == 0)
            {
                continue;
            }

            if (images.Count != i)
            {
                return (null, ComfyText.ImageGap(ImageArguments[i], ImageArguments[images.Count]));
            }

            images.Add(image);
        }

        var request = new ComfyRequest(
            ToolArguments.ReadString(arguments, PromptArgument),
            workflow.Length == 0 ? null : workflow,
            // An empty string is how some models leave an optional argument out: the default then, not "no negative".
            string.IsNullOrWhiteSpace(negative) ? null : negative,
            verbatim ?? false,
            seed,
            width,
            height,
            steps,
            cfg,
            denoise,
            images.Count == 0 ? null : images,
            count ?? 1,
            NegativeExtra: string.IsNullOrWhiteSpace(negativeExtra) ? null : negativeExtra);
        return (request, ComfyStudio.Check(request, maxCount));
    }

    /// <summary>A number argument that is no number. Pinned.</summary>
    public static string BadNumber(string argument, string raw) => $"Error: '{raw.Trim()}' is not a number for '{argument}'";

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var (request, error) = Read(arguments, MaxCount);
        if (request is null || error is not null)
        {
            return error;
        }

        ComfyGeneration generation;
        try
        {
            generation = await _studio.GenerateAsync(request, cancellationToken, _narrow).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The hint row's double-click (2026-09-28, ComfyStudio.Drain): the pictures stop, the turn goes on, and the model is told.
            return ComfyText.CancelledByUser;
        }

        return generation.Images.Count > 0 ? new ToolImageResult(generation.Text, generation.Images) : generation.Text;
    }
}
