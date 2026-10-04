using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Screen;

namespace NeonSidekick.Llm.Tools;

/// <summary>What became of a screenshot the model asked for (<see cref="ScreenCaptureTool"/>).</summary>
public abstract record ScreenAnswer
{
    private ScreenAnswer()
    {
    }

    /// <summary>Taken and sent.</summary>
    public sealed record Shot(ScreenShot Picture) : ScreenAnswer;

    /// <summary>The user did not allow it.</summary>
    public sealed record Denied : ScreenAnswer;

    /// <summary>Denied earlier in this turn: not asked again.</summary>
    public sealed record AlreadyDeclined : ScreenAnswer;

    /// <summary>The target named nothing, or the capture failed; the sentence says how.</summary>
    public sealed record Failed(string Message) : ScreenAnswer;

    /// <summary>Nothing could ask: no screen (headless) or no pane.</summary>
    public sealed record NoScreen : ScreenAnswer;
}

/// <summary>
/// <c>screen_capture(target?, prompt)</c> (2026-10-04, the user's ask: "what's this error on my screen?"): a monitor, every monitor,
/// a window (by id or title words) or the window behind the app, captured and shown to the model the <c>view_image</c> way — a
/// result line, then the picture in the carrier message. Under <c>Screen capture ask</c> <c>ask</c> the user allows it on a pane
/// first (once, or for the session); under <c>allow</c> it is taken at once. A denial is a sentence that tells the model to carry
/// on, and the rest of the turn's calls answer it without asking again. Read-only in the plan-mode sense, as
/// <c>camera_capture</c>: it looks, it changes nothing. <see cref="CameraCaptureTool"/>'s twin.
/// </summary>
public sealed class ScreenCaptureTool : AIFunction
{
    public const string ToolName = "screen_capture";

    public const string TargetArgument = "target";

    public const string PromptArgument = "prompt";

    /// <summary>The other names a model sends the target under.</summary>
    public static readonly string[] TargetAliases = ["window", "monitor", "what"];

    private static readonly JsonElement Schema = ToolSchema.Parse(
        "{ \"type\": \"object\", \"properties\": { " +
        "\"target\": { \"type\": \"string\", \"description\": \"What to capture: " + ScreenText.Targets + ". screen_list gives the ids.\" }, " +
        "\"prompt\": { \"type\": \"string\", \"description\": \"Why you want to see it, one short sentence shown to the user (e.g. \\\"To read the error dialog.\\\").\" } " +
        "}, \"required\": [\"prompt\"] }");

    private readonly Func<string, string, CancellationToken, Task<ScreenAnswer>>? _capture;

    /// <param name="capture">Takes the screenshot of (target, prompt), asking first where the setting says so; null where nothing can ask (headless), which every call then answers <see cref="ScreenText.NoScreen"/>.</param>
    public ScreenCaptureTool(Func<string, string, CancellationToken, Task<ScreenAnswer>>? capture)
    {
        _capture = capture;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Takes a screenshot of the user's screen (a monitor, every monitor, or one window) and shows it to you. Use it when seeing " +
        "the screen would really help: an error dialog, what an app shows, a layout the user asks about. The user may have to allow it. " +
        "It is saved in the working directory and attached to the message after the result. If the user denies it, carry on without " +
        "it and do not ask again unless they ask.";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The target out of the arguments (or an alias), trimmed; empty for none (the default monitor).</summary>
    public static string TargetOf(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (string name in (string[])[TargetArgument, .. TargetAliases])
        {
            string value = ToolArguments.ReadString(arguments, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    /// <summary>The prompt out of the arguments (or an alias); <see cref="ScreenText.DefaultPrompt"/> for none, line breaks flattened.</summary>
    public static string PromptOf(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        foreach (string name in (string[])[PromptArgument, .. CameraCaptureTool.PromptAliases])
        {
            string value = ToolArguments.ReadString(arguments, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return ScreenText.DefaultPrompt;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (_capture is null)
        {
            return ScreenText.NoScreen;
        }

        var answer = await _capture(TargetOf(arguments), PromptOf(arguments), cancellationToken).ConfigureAwait(false);
        return answer switch
        {
            ScreenAnswer.Shot shot => new ToolImageResult(ScreenText.Taken(shot.Picture), [shot.Picture.Image]),
            ScreenAnswer.Denied => ScreenText.Denied,
            ScreenAnswer.AlreadyDeclined => ScreenText.AlreadyDeclined,
            ScreenAnswer.Failed failed => ScreenText.ToolFailed(failed.Message),
            _ => ScreenText.NoScreen,
        };
    }
}
