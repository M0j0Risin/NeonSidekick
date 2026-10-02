using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Camera;

namespace NeonSidekick.Llm.Tools;

/// <summary>What became of a photo the model asked for (<see cref="CameraCaptureTool"/>).</summary>
public abstract record CameraAnswer
{
    private CameraAnswer()
    {
    }

    /// <summary>Taken and sent.</summary>
    public sealed record Shot(CameraShot Photo) : CameraAnswer;

    /// <summary>The user closed the shutter pane without sending one.</summary>
    public sealed record Declined : CameraAnswer;

    /// <summary>The user did not allow the app to take one (the <c>model</c> shutter).</summary>
    public sealed record Denied : CameraAnswer;

    /// <summary>Declined or denied earlier in this turn: not asked again.</summary>
    public sealed record AlreadyDeclined : CameraAnswer;

    /// <summary>The camera failed; the sentence says how.</summary>
    public sealed record Failed(string Message) : CameraAnswer;

    /// <summary>Nothing could ask: no screen (headless) or no pane.</summary>
    public sealed record NoScreen : CameraAnswer;
}

/// <summary>
/// <c>camera_capture(prompt)</c> (2026-10-02, the user's ask: the model asks for a photo when it needs to see something): the
/// prompt is shown to the user, who takes the photo on the shutter pane (or allows the app to take it, by the <c>Camera
/// shutter</c> setting); the photo is saved in the working directory's <c>camera</c> folder and comes back the
/// <c>view_image</c> way — a result line, then the picture in the carrier message. A decline is a sentence that tells the model
/// to carry on, and the rest of the turn's calls answer it without asking again (the screen keeps the turn's stamp). Read-only
/// in the plan-mode sense, like <c>ask_user</c>: it asks the user, it changes nothing of theirs.
/// </summary>
public sealed class CameraCaptureTool : AIFunction
{
    public const string ToolName = "camera_capture";

    public const string PromptArgument = "prompt";

    /// <summary>The other names a model sends the prompt under (lenient, the <c>ask_user</c> lesson).</summary>
    public static readonly string[] PromptAliases = ["reason", "message", "text", "question"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"prompt\": { \"type\": \"string\", \"description\": \"What you want to see and why, one short sentence shown to the user (e.g. \\\"Hold the label up to the camera.\\\").\" } " +
        "}, \"required\": [\"prompt\"] }");

    private readonly Func<string, CancellationToken, Task<CameraAnswer>>? _shoot;

    /// <param name="shoot">Asks for the photo and waits; null where nothing can ask (headless), which every call then answers <see cref="CameraText.NoScreen"/>.</param>
    public CameraCaptureTool(Func<string, CancellationToken, Task<CameraAnswer>>? shoot)
    {
        _shoot = shoot;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Asks the user for a photo from their camera and shows it to you. Use it only when seeing something would really help " +
        "(an object, a label, a screen, a gesture, the user themselves when they ask). The prompt is shown to the user, who takes the " +
        "photo; it is saved in the working directory's camera folder and attached to the message after the result. If the user " +
        "declines, carry on without it and do not ask again unless they ask.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The prompt out of the arguments (or an alias); <see cref="CameraText.DefaultPrompt"/> for none, line breaks flattened.</summary>
    public static string PromptOf(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (string name in (string[])[PromptArgument, .. PromptAliases])
        {
            string value = ToolArguments.ReadString(arguments, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return CameraText.DefaultPrompt;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (_shoot is null)
        {
            return CameraText.NoScreen;
        }

        var answer = await _shoot(PromptOf(arguments), cancellationToken).ConfigureAwait(false);
        return answer switch
        {
            CameraAnswer.Shot shot => new ToolImageResult(CameraText.Taken(shot.Photo), [shot.Photo.Image]),
            CameraAnswer.Declined => CameraText.Declined,
            CameraAnswer.Denied => CameraText.Denied,
            CameraAnswer.AlreadyDeclined => CameraText.AlreadyDeclined,
            CameraAnswer.Failed failed => CameraText.ToolFailed(failed.Message),
            _ => CameraText.NoScreen,
        };
    }
}
