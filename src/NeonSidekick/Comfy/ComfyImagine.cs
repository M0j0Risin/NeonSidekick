using System.Globalization;
using System.Text.RegularExpressions;

namespace NeonSidekick.Comfy;

/// <summary>
/// <c>/imagine [workflow] &lt;prompt&gt; [-- &lt;negative&gt;] [--seed N] [--size WxH] [--steps N] [--cfg X] [--denoise X]
/// [--image &lt;path&gt;] [--image2 &lt;path&gt;] [--image3 &lt;path&gt;] [--count N]</c> read into a request (2026-09-24, the user's ask: "in addition to the model
/// generating per-model prompts, we will be able to send standard prompts too, as in score_9, etc."). The prompt and
/// the negative are the user's text exactly as typed — commas, parentheses and weights kept, nothing added — so the
/// request is always <see cref="ComfyRequest.Verbatim"/>. The first word names the workflow when it is one's name;
/// otherwise the only workflow of the right kind is used. A flag's value is one word, or a double-quoted run (a path
/// with a space). A bare <c>--</c> starts the negative; <c>--no-negative</c> (later on 2026-09-24, the user's ask) sends
/// none at all — not the sidecar's, not the family's. The first word may name any installed workflow, offered to the
/// model or not (<see cref="ComfyRequest.AnyWorkflow"/>); without one the offered ones are the choice. Pure.
/// </summary>
public static partial class ComfyImagine
{
    [GeneratedRegex("""(?:^|\s)--(seed|size|steps|cfg|denoise|image[23]?|count)(?:\s+("[^"]*"|\S+))?(?=\s|$)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FlagPattern();

    [GeneratedRegex(@"(?:^|\s)--(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NegativeSplit();

    [GeneratedRegex(@"(?:^|\s)--no-negative(?=\s|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NoNegativeFlag();

    /// <summary>The bare switch that sends no negative prompt at all.</summary>
    public const string NoNegativeSwitch = "--no-negative";

    /// <summary>The request, or the sentence for the first thing that does not read (<see cref="ComfyText.ImagineUsage"/> for nothing at all).</summary>
    public static (ComfyRequest? Request, string? Error) Parse(string args, IReadOnlyList<ComfyWorkflow> workflows, int maxCount = Settings.AppSettingsData.DefaultComfyMaxPicturesPerCall)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(workflows);
        if (string.IsNullOrWhiteSpace(args))
        {
            return (null, ComfyText.ImagineUsage);
        }

        long? seed = null;
        int? width = null, height = null, steps = null, count = null;
        double? cfg = null, denoise = null;
        // Slot order (later still on 2026-09-24, the face swap): --image, --image2, --image3.
        var images = new string?[ComfyWorkflow.MaxInputImages];
        string? error = null;
        bool noNegative = NoNegativeFlag().IsMatch(args);
        string rest = FlagPattern().Replace(NoNegativeFlag().Replace(args, " "), match =>
        {
            string flag = "--" + match.Groups[1].Value.ToLowerInvariant();
            string value = match.Groups[2].Success ? match.Groups[2].Value.Trim('"') : "";
            if (error is not null)
            {
                return " ";
            }

            bool ok = flag switch
            {
                "--seed" => TryLong(value, out seed),
                "--steps" => TryInt(value, out steps),
                "--count" => TryInt(value, out count),
                "--cfg" => TryDouble(value, out cfg),
                "--denoise" => TryDouble(value, out denoise),
                "--image" => (images[0] = value).Length > 0,
                "--image2" => (images[1] = value).Length > 0,
                "--image3" => (images[2] = value).Length > 0,
                _ => TrySize(value, out width, out height),
            };
            if (!ok)
            {
                error = ComfyText.BadFlag(flag, value);
            }

            return " ";
        });

        if (error is not null)
        {
            return (null, error);
        }

        var parts = NegativeSplit().Split(rest, 2);
        string positive = parts[0].Trim();
        string? negative = parts.Length > 1 ? parts[1].Trim() : null;
        string? workflow = null;
        int space = positive.IndexOfAny([' ', '\t']);
        string first = space < 0 ? positive : positive[..space];
        if (workflows.Any(w => string.Equals(w.Name, first, StringComparison.OrdinalIgnoreCase)))
        {
            workflow = first;
            positive = space < 0 ? "" : positive[(space + 1)..].Trim();
        }

        // A named workflow with no {{prompt}} (a face swap) runs on its pictures alone.
        bool promptless = workflow is not null && workflows.First(w => string.Equals(w.Name, workflow, StringComparison.OrdinalIgnoreCase)) is { TakesPrompt: false };
        if (positive.Length == 0 && !promptless)
        {
            return (null, ComfyText.ImagineUsage);
        }

        for (int i = 1; i < images.Length; i++)
        {
            if (images[i] is not null && images[i - 1] is null)
            {
                return (null, ComfyText.ImageGap("--" + ComfyWorkflow.ImageKeys[i], "--" + ComfyWorkflow.ImageKeys[i - 1]));
            }
        }

        if (noNegative && !string.IsNullOrEmpty(negative))
        {
            return (null, ComfyText.NegativeAndNoNegative);
        }

        // "" is "no negative at all"; null leaves the workflow's own.
        string? sent = noNegative ? "" : string.IsNullOrEmpty(negative) ? null : negative;
        var request = new ComfyRequest(positive, workflow, sent, Verbatim: true, seed, width, height, steps, cfg, denoise, images.OfType<string>().ToList(), count ?? 1, AnyWorkflow: true);
        return ComfyStudio.Check(request, maxCount) is { } range ? (null, range) : (request, null);
    }

    private static bool TryInt(string text, out int? value)
    {
        value = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
        return value is not null;
    }

    private static bool TryLong(string text, out long? value)
    {
        value = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;
        return value is not null;
    }

    private static bool TryDouble(string text, out double? value)
    {
        value = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && double.IsFinite(n) ? n : null;
        return value is not null;
    }

    private static bool TrySize(string text, out int? width, out int? height)
    {
        width = height = null;
        int x = text.IndexOfAny(['x', 'X', '*']);
        if (x <= 0 || !TryInt(text[..x], out var w) || !TryInt(text[(x + 1)..], out var h))
        {
            return false;
        }

        (width, height) = (w, h);
        return true;
    }
}
