using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

// NeonSidekick — a terminal chat sidekick
// Copyright (C) 2026 Christopher Nelson
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

// NeonSidekick — a NativeAOT terminal chat assistant (Spectre.Console TUI, synthwave).
//
// Usage:
//   NeonSidekick              interactive TUI
//   NeonSidekick --headless   stdin/stdout REPL (stdout IS the interface; no TUI)
//   NeonSidekick --smoke      render the banner, verify native dependencies, exit 0/1
//   NeonSidekick --version | --help
//
// Keys: ESC = cancel/back from anywhere; Ctrl+C = copy / stop the speech / cancel, twice to exit; /exit exits.

// Every culture invariant before anything formats a number or a date (2026-09-23): InvariantGlobalization
// is off since SqlClient refuses it, and this keeps what the flag gave (App/CulturePin.cs).
CulturePin.Apply();

// The Claude CLI server's MCP relay (2026-09-30): this executable started by the claude CLI as its MCP server
// (NeonSidekick --mcp-relay <address> <key>), copying the CLI's stdio to the app's loopback listener. Before anything
// touches the console: stdout is the MCP pipe, not a screen.
if (NeonSidekick.Claude.McpRelay.Asked(args))
{
    return await NeonSidekick.Claude.McpRelay.RunAsync(args[1], args[2]);
}

// Force UTF-8 console output. On Windows the NativeAOT build (InvariantGlobalization) falls back
// to the console's OEM code page (CP437/CP850); the themed UI's box-drawing, bullets and braille
// spinner frames are not in that code page and print as '?'. Wrapped in try/catch because the
// call can fail on detached or unusual consoles, where '?' beats a crash.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // Leave the runtime default in place.
}

// stdout as a FrameWriter (2026-09-29, the user's report: the hint row, the toolbar and the performance bar flickered at
// every turn's end): it flushes every write as the console's own writer did, and the pane holds each synchronized frame
// and lets it go as one write. Installed with SetOut before the Spectre console, which takes Console.Out and asks whether
// its writer is stdout's for its width and terminal detection.
// Terminal.app (and iTerm2 on the alternate screen) draws ⚙️, 🛠️ and the other text-default emoji with U+FE0F one cell wide where other terminals draw two
// (2026-10-06, measured there): the cell arithmetic follows the terminal, or the toolbar's clicks land on the wrong button.
NeonSidekick.UI.TextCells.NarrowSelectorSequences = NeonSidekick.UI.TextCells.ForTerminal(Environment.GetEnvironmentVariable("TERM_PROGRAM"));

var frames = new FrameWriter(Console.OpenStandardOutput());
Console.SetOut(frames);

var console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Ansi = AnsiSupport.Detect,
    ColorSystem = ColorSystemSupport.Detect,
});

var options = SidekickOptions.Parse(args);
if (options.Error is not null)
{
    console.WriteLine(options.Error);
    console.WriteLine(SidekickOptions.Usage);
    return 2;
}

if (options.ShowHelp)
{
    console.WriteLine(SidekickOptions.Usage);
    return 0;
}

if (options.ShowVersion)
{
    console.WriteLine(SidekickApp.VersionLine);
    return 0;
}

// The run's diagnostic lines in memory (2026-10-02): /log's window shows them, --log or not. Attached before the --log sink
// opens, so the window starts at the run's first line; the interactive screen alone has the window.
using var logBuffer = !options.Headless && !options.IsCheck ? DiagnosticBuffer.Attach() : null;

// --log <path>: every diagnostic line, Trace and up, appended to a file. The TUI shows only
// warnings, so this is how "what did the wake recogniser hear" is answered in the field. A path
// that cannot be opened is reported once and the run goes on without it. A {ts} in the path is the
// run's start time (2026-10-03), stamped here once, so everything after names the file written.
options = options.WithLogStamp(DateTime.Now);
DiagnosticFileSink? logSink = null;
if (options.LogPath is { } logPath)
{
    try
    {
        logSink = DiagnosticFileSink.Open(logPath);
    }
    catch (Exception ex)
    {
        console.WriteLine($"--log: cannot open {logPath} ({ex.Message}); continuing without a log file.");
    }
}

using var logSinkScope = logSink;

// The environment is read through one injectable reader so nothing else in the process ever
// calls Environment.GetEnvironmentVariable directly; tests hand the same class a dictionary.
var environment = new EnvironmentOverrides(Environment.GetEnvironmentVariable);
string home = AppSettings.ResolveStorageDirectory(environment.Home);

// The profile for this launch (2026-09-26): --profile over NEONSIDEKICK_PROFILE over, headless, "default", over the
// pointer (SidekickOptions.LaunchProfile). A name that is not a profile ends the launch like a bad argument does,
// before anything is created under the home.
string? profile = options.LaunchProfile(environment.Profile);
if (profile is not null && !Profiles.Exists(home, profile))
{
    console.WriteLine(AppSettings.UnknownProfileMessage(profile, Profiles.List(home)));
    return 2;
}

using var settings = new AppSettings(home, profile);

// A crash lands in crash.log beside the profiles, stack and all: the terminal window closes with
// the process, so the runtime's stderr print is never read, and --log carries no stack. The
// domain handler sees a throw on any thread (an audio pump, a pool continuation); the catch
// around the run below sees the main path and lets the console mode and the settings' flush
// happen on the way out. Neither handler may throw.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        DiagnosticLog.Error(CrashReport.Category, CrashReport.LogLine(CrashReport.Write(home, ex, DateTime.UtcNow)), ex);
    }
};
TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Error(CrashReport.Category, "Unobserved task exception", e.Exception);

// The app token: Ctrl+Break, and Ctrl+C wherever the console input is not the app's (headless,
// the check modes, a redirected console). It cancels the turn in flight and ends the loop, so
// settings are still flushed on the way out. On the interactive screen Ctrl+C is a key record
// (WindowsConsoleInput drops ENABLE_PROCESSED_INPUT, 2026-09-17) that the screen decides about.
// In headless mode a Console.ReadLine that is already waiting cannot be interrupted on
// Windows; the process leaves at the next line instead.
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

// The bottom pane needs to know where the cursor is; a console that cannot say (redirected
// stdout) gets the plain transcript. With a pane, the chat screen also reads the console's input
// buffer itself (keys and mouse clicks) and takes the mouse; the diagnostic modes and headless
// never do, and the original console mode is restored when the input is disposed, after the run.
var geometry = ScreenGeometry.ForConsole();
bool interactive = !options.Headless && !options.IsCheck;
using var windowsInput = interactive && geometry is not null ? WindowsConsoleInput.TryCreate() : null;
// macOS (2026-10-06, the macOS build): the termios reader. It owns stdin, so the cursor queries the geometry makes go through it
// from here on (Console.CursorTop on Unix reads its answer from stdin itself and resets the terminal's mode around the read);
// the one probe above ran before raw mode, when .NET's own query was still safe.
UnixConsoleInput? unixInput = null;
if (OperatingSystem.IsMacOS() && interactive && geometry is not null && windowsInput is null)
{
    unixInput = UnixConsoleInput.TryCreate();
    if (unixInput is { } reader)
    {
        reader.Frames = frames;
        geometry = reader.Geometry();
    }
}

using var unixInputScope = unixInput;

// The clipboard: Win32's on Windows, pbcopy/pbpaste on macOS (2026-10-06, the user's call), text only there.
Func<string?> readClipboard = WindowsClipboard.TryReadText;
Func<string, bool> copyToClipboard = WindowsClipboard.TrySetText;
Func<byte[]?> readClipboardImage = WindowsClipboard.TryReadImage;
if (OperatingSystem.IsMacOS())
{
    readClipboard = MacClipboard.TryReadText;
    copyToClipboard = MacClipboard.TrySetText;
    readClipboardImage = MacClipboard.TryReadImage;
}
IAnsiConsoleInput? consoleInput = (IAnsiConsoleInput?)windowsInput ?? unixInput;
var app = new SidekickApp(console, settings, environment, geometry: geometry, input: consoleInput, clipboard: readClipboard, copyToClipboard: copyToClipboard, clipboardImage: readClipboardImage, setTitle: title => ConsoleTitle.TrySet(title), openViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.Open : null, viewPicture: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.OpenAt : null, followViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.Follow : null, printSpooler: OperatingSystem.IsWindows() ? new NeonSidekick.Printing.WindowsPrintSpooler() : null, embeddedLlm: NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.Offered ? () => NeonSidekick.EmbeddedLlm.EmbeddedLlmService.Create(settings.EmbeddedModelsDirectory, settings.LlamaDirectory) : null, perfSource: NeonSidekick.Perf.PerfSources.CreateDefault, frames: frames, camera: OperatingSystem.IsWindows() ? new NeonSidekick.Camera.MediaFoundationCameraSystem() : null, liveView: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.ShowLive : null, showShot: NeonSidekick.Viewer.PictureWindow.IsAvailable ? picture => NeonSidekick.Viewer.PictureWindow.OpenAt(picture, activate: false) : null, openLogWindow: NeonSidekick.Viewer.LogWindow.IsAvailable && logBuffer is not null ? () => NeonSidekick.Viewer.LogWindow.Show(logBuffer) : null, openProcessWindow: NeonSidekick.Viewer.ProcessWindow.IsAvailable ? NeonSidekick.Viewer.ProcessWindow.Show : null, closeLogWindow: NeonSidekick.Viewer.LogWindow.IsAvailable && logBuffer is not null ? NeonSidekick.Viewer.LogWindow.Close : null, closeViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.CloseViewer : null, screenSystem: OperatingSystem.IsWindows() ? new NeonSidekick.Screen.WindowsScreenSystem() : null, hotkeyProbe: OperatingSystem.IsWindows() ? new NeonSidekick.Hotkeys.WindowsHotkeyProbe() : null, openThumbs: NeonSidekick.Viewer.ThumbsWindow.IsAvailable ? NeonSidekick.Viewer.ThumbsWindow.Open : null, followThumbs: NeonSidekick.Viewer.ThumbsWindow.IsAvailable ? NeonSidekick.Viewer.ThumbsWindow.Follow : null, closeThumbs: NeonSidekick.Viewer.ThumbsWindow.IsAvailable ? NeonSidekick.Viewer.ThumbsWindow.Close : null, showInViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.ShowQuietly : null, videoPlayer: OperatingSystem.IsWindows() ? NeonSidekick.Viewer.VideoWindow.Player : null);

// The console window closed by its X button (2026-10-02, the user's report: Docker server stop on exit never ran then).
// SIGHUP is CTRL_CLOSE_EVENT on Windows (a hangup elsewhere): no finally of the run's runs after it, and Windows ends the
// process about 5 s on, so the handler does the exit's work itself, on the console's control thread, the process alive
// until it returns. Cancel stays unset: the window still closes. Ctrl+C, Ctrl+Break and /exit take the usual way out.
using var closing = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, _ =>
{
    app.ConsoleClosing();
    // The debounced save the normal exit flushes below, as the quarter-second rule asks. A failure must not throw here.
    try
    {
        settings.FlushAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        DiagnosticLog.Warn(AppSettings.Category, "Settings flush on window close: " + ex.Message);
    }
});

// The Themed external windows switch (later on 2026-09-27 for the viewer; every window of ours since 2026-10-03), read from the effective settings on the window's thread.
NeonSidekick.Viewer.PictureWindow.Themed = () => app.EffectiveSettings.ThemedExternalWindows;
// The viewer's keys highlight the same picture in the strip (2026-09-28), and in the thumbnail browser (2026-10-04).
NeonSidekick.Viewer.PictureWindow.Browsed = app.ViewerBrowsed;
// The thumbnail browser's picks move the viewer and the strip; the picture menu's edits read the effective settings, and its
// attach, print and lines go to the chat (2026-10-04).
NeonSidekick.Viewer.ThumbsWindow.Picked = app.ThumbsPicked;
NeonSidekick.Viewer.PictureMenu.Settings = () => NeonSidekick.Images.PictureEditSettings.From(app.EffectiveSettings);
NeonSidekick.Viewer.PictureMenu.Attach = app.AttachPicture;
NeonSidekick.Viewer.PictureMenu.Print = app.PrintPicture;
NeonSidekick.Viewer.PictureMenu.Reported = app.PictureReported;
// The app's own windows hand back what they have no use for (2026-10-03): TAB brings the terminal forward, found now, while
// it is still the window in front, and a Ctrl or Alt chord is queued on the console input as though typed there.
NeonSidekick.Viewer.TerminalHandoff.Remember();
NeonSidekick.Viewer.TerminalHandoff.Passed = windowsInput is null ? null : windowsInput.Inject;
// The viewer opens where it last closed (2026-09-28): the profile keeps the corner, written only when it moved, so a close
// in place logs no change. On the viewer's thread; Update is locked and nothing listens to Changed.
NeonSidekick.Viewer.PictureWindow.Position = () => settings.Current is { ViewerLeft: int x, ViewerTop: int y } ? (x, y) : null;
NeonSidekick.Viewer.PictureWindow.Placed = (x, y) =>
{
    if (settings.Current is not { ViewerLeft: int left, ViewerTop: int top } || left != x || top != y)
    {
        settings.Update(d =>
        {
            d.ViewerLeft = x;
            d.ViewerTop = y;
        });
    }
};
// The camera's live window keeps a place of its own (2026-10-02), the viewer's pair's twin.
NeonSidekick.Viewer.PictureWindow.LivePosition = () => settings.Current is { CameraWindowLeft: int x, CameraWindowTop: int y } ? (x, y) : null;
NeonSidekick.Viewer.PictureWindow.LivePlaced = (x, y) =>
{
    if (settings.Current is not { CameraWindowLeft: int left, CameraWindowTop: int top } || left != x || top != y)
    {
        settings.Update(d =>
        {
            d.CameraWindowLeft = x;
            d.CameraWindowTop = y;
        });
    }
};
// The log window keeps a place of its own too (2026-10-02).
NeonSidekick.Viewer.LogWindow.Position = () => settings.Current is { LogWindowLeft: int x, LogWindowTop: int y } ? (x, y) : null;
NeonSidekick.Viewer.LogWindow.Placed = (x, y) =>
{
    if (settings.Current is not { LogWindowLeft: int left, LogWindowTop: int top } || left != x || top != y)
    {
        settings.Update(d =>
        {
            d.LogWindowLeft = x;
            d.LogWindowTop = y;
        });
    }
};
// The process window keeps a place of its own too (2026-10-05), whichever process it shows.
NeonSidekick.Viewer.ProcessWindow.Position = () => settings.Current is { ProcessWindowLeft: int x, ProcessWindowTop: int y } ? (x, y) : null;
NeonSidekick.Viewer.ProcessWindow.Placed = (x, y) =>
{
    if (settings.Current is not { ProcessWindowLeft: int left, ProcessWindowTop: int top } || left != x || top != y)
    {
        settings.Update(d =>
        {
            d.ProcessWindowLeft = x;
            d.ProcessWindowTop = y;
        });
    }
};
// The thumbnail browser keeps a place of its own too (2026-10-04).
NeonSidekick.Viewer.ThumbsWindow.Position = () => settings.Current is { ThumbsWindowLeft: int x, ThumbsWindowTop: int y } ? (x, y) : null;
NeonSidekick.Viewer.ThumbsWindow.Placed = (x, y) =>
{
    if (settings.Current is not { ThumbsWindowLeft: int left, ThumbsWindowTop: int top } || left != x || top != y)
    {
        settings.Update(d =>
        {
            d.ThumbsWindowLeft = x;
            d.ThumbsWindowTop = y;
        });
    }
};
// The video window (2026-10-05) keeps a place of its own too, and its WebView2 profile and page under the home.
if (OperatingSystem.IsWindows())
{
    NeonSidekick.Viewer.VideoWindow.Home = home;
    NeonSidekick.Viewer.VideoWindow.Position = () => settings.Current is { VideoWindowLeft: int x, VideoWindowTop: int y } ? (x, y) : null;
    NeonSidekick.Viewer.VideoWindow.Placed = (x, y) =>
    {
        if (settings.Current is not { VideoWindowLeft: int left, VideoWindowTop: int top } || left != x || top != y)
        {
            settings.Update(d =>
            {
                d.VideoWindowLeft = x;
                d.VideoWindowTop = y;
            });
        }
    };
}

int exitCode;
try
{
    exitCode = await app.RunAsync(options, shutdown.Token).ConfigureAwait(false);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    string? crashPath = CrashReport.Write(home, ex, DateTime.UtcNow);
    // The --log line without its console echo: the notice below is the one line the user sees.
    bool echo = DiagnosticLog.EchoToConsole;
    DiagnosticLog.EchoToConsole = false;
    DiagnosticLog.Error(CrashReport.Category, CrashReport.LogLine(crashPath), ex);
    DiagnosticLog.EchoToConsole = echo;
    console.WriteLine(CrashReport.Notice(crashPath, ex));
    exitCode = 1;
}

// The picture viewer (2026-09-27) and the log window (2026-10-02) close with the app; their threads are background ones,
// this is the tidy way (and the log window remembers its place).
NeonSidekick.Viewer.PictureWindow.CloseAll();
NeonSidekick.Viewer.LogWindow.Close();
NeonSidekick.Viewer.ProcessWindow.Close();
NeonSidekick.Viewer.ThumbsWindow.Close();
if (OperatingSystem.IsWindows())
{
    NeonSidekick.Viewer.VideoWindow.Close();
}

// A change made in the last quarter-second before quitting must not be lost to the debounce.
await settings.FlushAsync().ConfigureAwait(false);

// ONNX Runtime 1.22 aborts the process at exit on macOS while its environment lives ("mutex lock failed: Invalid argument"
// from its static destructors, exit code 134; the first Mac smoke, 2026-10-06, after kokoro:ort made one): released here, last,
// only when something made one.
if (!OperatingSystem.IsWindows() && Microsoft.ML.OnnxRuntime.OrtEnv.IsCreated)
{
    Microsoft.ML.OnnxRuntime.OrtEnv.Instance().Dispose();
}

return exitCode;
