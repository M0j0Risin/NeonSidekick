using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Screen;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The screen (2026-10-04) ───────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The screen (GDI in the app on Windows, a fake in tests); null where there is none, and the screen tools are then not offered.</summary>
    private readonly IScreenSystem? _screenSystem;

    /// <summary>Screenshots taken and saved into the Screen capture output folder; null without a screen.</summary>
    private readonly ScreenCapture? _screenCapture;

    /// <summary><c>screen_capture</c> and <c>screen_list</c>, built once; offered while <see cref="ScreenOffered(AppSettingsData)"/> says so.</summary>
    private readonly IReadOnlyList<AIFunction> _screenTools;

    /// <summary>The screenshots by full path, so one put on the input line is still known as a screenshot when it is sent.</summary>
    private readonly ConcurrentDictionary<string, byte> _screenPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Allow for this session" on the allow pane (<c>Screen capture ask</c> <c>ask</c>); forgotten with the session.</summary>
    private bool _screenAllowed;

    /// <summary>A screenshot denied in the running turn: the turn's later calls are answered without asking.</summary>
    private bool _screenDeclined;

    /// <summary>The screen tools (2026-10-04): <c>screen_capture</c> over <paramref name="capture"/> (null where nothing can ask) and <c>screen_list</c> over <paramref name="screen"/>.</summary>
    public static IReadOnlyList<AIFunction> ScreenTools(Func<string, string, CancellationToken, Task<ScreenAnswer>>? capture, IScreenSystem? screen) =>
        [new ScreenCaptureTool(capture), new ScreenListTool(screen)];

    /// <summary>
    /// Whether the screen tools are offered (2026-10-04, <see cref="CameraOffered(AppSettingsData, bool, bool, bool)"/>'s shape): the
    /// setting <c>Screen capture tool</c> on, a screen layer here, the pane to ask on, and a model that reads pictures. Pure.
    /// </summary>
    public static bool ScreenOffered(AppSettingsData effective, bool available, bool pane, bool blind) =>
        effective.ScreenTools && available && pane && !blind;

    private bool ScreenOffered(AppSettingsData effective) =>
        ScreenOffered(effective, _screenSystem is not null, _pane.Enabled, _session.EmbeddedServer is { Vision: false });

    /// <summary>
    /// The model's <c>screen_capture</c>, on the turn task: the target resolved first (a bad one is its sentence, nothing asked),
    /// a denial earlier in the turn answered without asking; under <c>ask</c> the allow pane naming what would be captured (unless
    /// allowed for the session), under <c>allow</c> straight to the capture. A sent screenshot's path is remembered, and the
    /// viewer shows it when <c>Screen capture preview</c> is on, so the user sees exactly what the model got.
    /// </summary>
    private async Task<ScreenAnswer> CaptureForModelAsync(string target, string prompt, CancellationToken turnToken)
    {
        if (_screenDeclined)
        {
            return new ScreenAnswer.AlreadyDeclined();
        }

        if (!_pane.Enabled || _screenCapture is not { } capture)
        {
            return new ScreenAnswer.NoScreen();
        }

        if (ScreenTarget.Parse(target, out string? error) is not { } parsed)
        {
            return new ScreenAnswer.Failed(error ?? ScreenText.BadTarget(target));
        }

        ScreenAnswer answer;
        try
        {
            var aim = await capture.AimAsync(parsed, turnToken).ConfigureAwait(false);
            answer = await AllowScreenAsync(aim, prompt, turnToken).ConfigureAwait(false) is { } refused
                ? refused
                : new ScreenAnswer.Shot(await capture.TakeAsync(aim, turnToken).ConfigureAwait(false));
        }
        catch (ScreenException e)
        {
            answer = new ScreenAnswer.Failed(e.Message);
        }

        if (answer is ScreenAnswer.Denied)
        {
            _screenDeclined = true;
        }

        if (answer is ScreenAnswer.Shot shot)
        {
            _screenPaths[shot.Picture.FullPath] = 0;
            if (_effective().ScreenPreview)
            {
                ShowShotQuietly(shot.Picture.FullPath);
            }
        }

        DiagnosticLog.Info(ScreenText.Category, "screen_capture: " + answer.GetType().Name);
        return answer;
    }

    /// <summary>The allow pane when <c>Screen capture ask</c> says so and the session has not allowed it: null to go ahead, else the answer that stops it.</summary>
    private async Task<ScreenAnswer?> AllowScreenAsync(ScreenAim aim, string prompt, CancellationToken turnToken)
    {
        if (_screenAllowed || ScreenAskMode.Resolve(_effective()) == ScreenAsk.Allow)
        {
            return null;
        }

        var allow = await RunCameraPaneAsync(token => _cameraMenu.AllowAsync(ScreenText.AllowCaption(aim.Described, prompt), token, ScreenText.AllowTitle), null, turnToken).ConfigureAwait(false);
        if (allow is not { } choice)
        {
            return new ScreenAnswer.NoScreen();
        }

        if (choice == CameraAllow.Deny)
        {
            return new ScreenAnswer.Denied();
        }

        _screenAllowed = choice == CameraAllow.Session;
        return null;
    }

    /// <summary>
    /// <c>/screen</c>'s argument list (2026-10-04, the user's report: it had none): <see cref="ScreenTarget.Complete"/> over the
    /// monitors, read only after <c>monitor:</c>, and the windows, only after <c>window:</c> — fresh at each keystroke, both cheap
    /// synchronous calls (each sets and restores the thread's DPI awareness itself). No screen system, or a read that fails,
    /// gives the words alone.
    /// </summary>
    private IReadOnlyList<CompletionItem> ScreenChoices(string argText)
    {
        IReadOnlyList<ScreenMonitor> monitors = [];
        IReadOnlyList<ScreenWindow> windows = [];
        int? ownMonitor = null;
        long? ownWindow = null;
        if (_screenSystem is { } system && !argText.Contains(' ', StringComparison.Ordinal))
        {
            try
            {
                if (argText.StartsWith(ScreenText.MonitorPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    monitors = system.Monitors();
                    ownMonitor = system.OwnMonitor();
                }
                else if (argText.StartsWith(ScreenText.WindowPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    windows = system.Windows();
                    ownWindow = system.OwnWindow();
                }
            }
            catch (ScreenException e)
            {
                DiagnosticLog.Debug(ScreenText.Category, "/screen's argument list could not read the targets: " + e.Message);
            }
        }

        return ScreenTarget.Complete(argText, monitors, ownMonitor, windows, ownWindow);
    }

    /// <summary>The message's pictures with the screenshots marked as such (one attached from the line was loaded from its file).</summary>
    private IReadOnlyList<ImageAttachment> MarkScreenImages(IReadOnlyList<ImageAttachment> images) =>
        _screenPaths.IsEmpty || !images.Any(i => _screenPaths.ContainsKey(i.Path)) ? images
            : images.Select(i => _screenPaths.ContainsKey(i.Path) ? i with { Screen = true } : i).ToList();

    /// <summary>
    /// <c>/screen [target | list]</c> (2026-10-04): the user's own hand, so <c>Screen capture tool</c> and the ask never judge it —
    /// <c>list</c> prints the targets; anything else is a target (the monitor the app is on when bare), captured under a spinner,
    /// shown in the viewer under <c>Screen capture preview</c>, and put on the input line as the paste of its path, as
    /// <c>/camera</c> does. The app's own window is in front while it runs, so <c>/screen behind</c> is the way to catch the
    /// window the user was just in.
    /// </summary>
    private async Task HandleScreenAsync(string args, CancellationToken cancellationToken)
    {
        if (_screenCapture is not { } capture || _screenSystem is not { } system)
        {
            _transcript.Error(ScreenText.Unsupported);
            return;
        }

        if (string.Equals(args.Trim(), "list", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                foreach (string line in await Task.Run(() => ScreenText.List(system.Monitors(), system.OwnMonitor(), system.Windows(), system.OwnWindow()), cancellationToken).ConfigureAwait(false))
                {
                    _transcript.Notice(line);
                }
            }
            catch (ScreenException e)
            {
                _transcript.Error(e.Message);
            }

            return;
        }

        if (ScreenTarget.Parse(args, out string? error) is not { } target)
        {
            _transcript.Error(error ?? ScreenText.Usage);
            return;
        }

        try
        {
            var shot = await _transcript.WithSpinnerAsync(ScreenText.Capturing, async () =>
            {
                var aim = await capture.AimAsync(target, cancellationToken).ConfigureAwait(false);
                return await capture.TakeAsync(aim, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            if (_effective().ScreenPreview)
            {
                ShowShotQuietly(shot.FullPath);
            }

            _screenPaths[shot.FullPath] = 0;
            _attachReplay = [new InputEvent.Paste("\"" + shot.FullPath + "\""), new InputEvent.Key(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false))];
            _transcript.Notice(ScreenText.Attached(shot.RelativePath));
        }
        catch (ScreenException e)
        {
            _transcript.Error(e.Message);
        }
    }
}
