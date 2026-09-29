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

// --log <path>: every diagnostic line, Trace and up, appended to a file. The TUI shows only
// warnings, so this is how "what did the wake recogniser hear" is answered in the field. A path
// that cannot be opened is reported once and the run goes on without it.
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
bool interactive = !options.Headless && !options.Smoke && !options.AudioCheck && !options.VoiceCheck;
using var consoleInput = interactive && geometry is not null ? WindowsConsoleInput.TryCreate() : null;
var app = new SidekickApp(console, settings, environment, geometry: geometry, input: consoleInput, clipboard: WindowsClipboard.TryReadText, copyToClipboard: WindowsClipboard.TrySetText, clipboardImage: WindowsClipboard.TryReadImage, setTitle: title => ConsoleTitle.TrySet(title), openViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.Open : null, viewPicture: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.OpenAt : null, followViewer: NeonSidekick.Viewer.PictureWindow.IsAvailable ? NeonSidekick.Viewer.PictureWindow.Follow : null, printSpooler: OperatingSystem.IsWindows() ? new NeonSidekick.Printing.WindowsPrintSpooler() : null, embeddedLlm: NeonSidekick.EmbeddedLlm.EmbeddedEndpoint.Offered ? () => NeonSidekick.EmbeddedLlm.EmbeddedLlmService.Create(settings.EmbeddedModelsDirectory, settings.LlamaDirectory) : null);

// The viewer's Themed image viewer switch (later on 2026-09-27), read from the effective settings on the viewer's thread.
NeonSidekick.Viewer.PictureWindow.Themed = () => app.EffectiveSettings.ThemedViewer;
// The viewer's keys highlight the same picture in the strip (2026-09-28).
NeonSidekick.Viewer.PictureWindow.Browsed = app.ViewerBrowsed;
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

// The picture viewer (2026-09-27) closes with the app; its thread is a background one, this is the tidy way.
NeonSidekick.Viewer.PictureWindow.CloseAll();

// A change made in the last quarter-second before quitting must not be lost to the debounce.
await settings.FlushAsync().ConfigureAwait(false);
return exitCode;
