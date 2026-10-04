using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Screen;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>screen_list()</c> (2026-10-04): the monitors and the windows <c>screen_capture</c> can aim at, front to back, each with the
/// target that names it (<c>monitor:2</c>, <c>window:1234</c>), so the model can pick one. Titles and sizes only, never a pixel, so
/// it asks nothing. Offered with <c>screen_capture</c>, under the same switch.
/// </summary>
public sealed class ScreenListTool : AIFunction
{
    public const string ToolName = "screen_list";

    private static readonly JsonElement Schema = ToolSchema.Parse("{ \"type\": \"object\", \"properties\": {} }");

    private readonly IScreenSystem? _screen;

    public ScreenListTool(IScreenSystem? screen)
    {
        _screen = screen;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Lists the user's monitors and the windows on the screen (front to back) with the target that names each, for screen_capture. " +
        "Titles and sizes only, no picture.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_screen is not { } screen)
        {
            return ScreenText.ToolFailed(ScreenText.Unsupported);
        }

        try
        {
            var lines = await Task.Run(() => ScreenText.List(screen.Monitors(), screen.OwnMonitor(), screen.Windows(), screen.OwnWindow()), cancellationToken)
                .WaitAsync(ScreenCapture.Timeout, cancellationToken).ConfigureAwait(false);
            return string.Join('\n', lines);
        }
        catch (ScreenException e)
        {
            return ScreenText.ToolFailed(e.Message);
        }
        catch (TimeoutException)
        {
            return ScreenText.ToolFailed(ScreenText.TimedOut);
        }
    }
}
