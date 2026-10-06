# Building from source

How to build, test and publish Neon Sidekick yourself. Back to the [README](../README.md).

## What you need

* **Windows x64.** Releases target `win-x64`. A macOS build for Apple Silicon is in preview; see [Building on a Mac](#building-on-a-mac).
* **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).**
* **For the native build (NativeAOT):** Visual Studio 2022 or later, or its Build Tools, with the **Desktop development with C++** workload. A plain `dotnet build` doesn't need it.
* **PowerShell** to run `build.ps1`, not Git Bash: the smoke step hangs under MSYS.

## Quick start

```powershell
git clone https://github.com/M0j0Risin/NeonSidekick.git
cd NeonSidekick
dotnet build NeonSidekick.slnx -c Release
dotnet run --project src/NeonSidekick -c Release
```

`dotnet run` starts the app on the .NET runtime, which is fine for trying a change. The native, self-contained `NeonSidekick.exe` that releases ship comes from `build.ps1`.

## build.ps1

`build.ps1` is the whole pipeline in one script; CI runs the same script, so a local run matches it.

| Command | What it does |
|---|---|
| `.\build.ps1` | Restore, build, run the tests with coverage, publish the native exe and smoke-test it. |
| `.\build.ps1 -TestOnly` | Restore, build and test; no publish. The quickest full check. |
| `.\build.ps1 -Publish` | Restore, build, publish the native exe and smoke-test it; no tests. |
| `.\build.ps1 -Package` | The full run, then the release zip and its `.sha256` in `publish\package\`. |
| `.\build.ps1 -Package -Tag v1.2.3` | The same, refusing a tag that isn't the project's `<Version>`. |
| `.\build.ps1 -CoverageFloor 0` | Reports coverage without failing under the floor (80% by default). |
| `.\build.ps1 -Clean` | Deletes `bin\`, `obj\` and `publish\`. |

The native exe lands in `publish\output\NeonSidekick.exe`.

The build is strict on purpose:
* **Zero warnings.** Any compiler warning fails the build, and so does any NativeAOT warning about the app's own code.
* **Coverage.** The tests must cover at least 80% of the lines.
* **Smoke test.** After publishing, the script runs the native exe with `--smoke`, which checks that every native library loads and works. `dotnet build` and `dotnet test` can't catch a missing native DLL; this can.

## Tests

```powershell
dotnet test tests/NeonSidekick.Tests -c Release
dotnet test tests/NeonSidekick.Tests -c Release --filter "FullyQualifiedName~AssistantTests"
```

* The tests run one at a time, by design.
* **Live tests** (a real LLM server, database, camera, microphone, speech model…) are skipped unless their resource is there. [ENVIRONMENT.md](ENVIRONMENT.md#test-suite) lists the variables that turn each one on.
* Two tests load an embedded model onto the GPU. `build.ps1` leaves them out; run them with `dotnet test tests/NeonSidekick.Tests -c Release --filter Category=LoadsEmbeddedModel`.

## Checking the native exe

Besides `--smoke`, the published exe has checks for real hardware and servers: `--audio-check`, `--voice-check`, `--camera-check`, and one per database family (`--sql-check`, `--postgres-check`…). [COMMANDLINE.md](COMMANDLINE.md) lists them all.

## Building on a Mac

A preview build for Apple Silicon Macs (`osx-arm64`). It leaves out the features that have no macOS backend yet: voice, pictures, the camera, screen capture, the viewer windows, YouTube playback, printing, the embedded LLM, Docker and UNC shares. The chat, the tools, MCP, git, the database tools, sessions and headless mode are all there.

NativeAOT can't build a Mac binary from Windows, so build on the Mac itself. You need:

* **Microsoft's [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for macOS Arm64, not Homebrew's (see below).
* **The Xcode command line tools** (`xcode-select --install`) for the native link.
* **PowerShell 7** to run `build.ps1`, installed as a .NET tool (see below).

### Setting up the Mac

Homebrew's `dotnet` formula is built from source. Its NativeAOT pack is marked non-portable, so the publish links against OpenSSL and fails at the very end with `ld: library 'ssl' not found`. Homebrew's `powershell` formula depends on that same `dotnet`, so both have to go:

```sh
brew uninstall powershell dotnet          # if they are installed
brew install --cask dotnet-sdk            # Microsoft's SDK, in /usr/local/share/dotnet (asks for your password)
dotnet tool install --global PowerShell   # pwsh, in ~/.dotnet/tools
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
```

Homebrew no longer has a stable `powershell` cask, only `powershell@preview`. The SDK's installer also adds `~/.dotnet/tools` through `/etc/paths.d`, but as a literal `~`, which zsh doesn't expand; hence the `~/.zshrc` line. In a new shell, check:

```sh
which pwsh dotnet      # ~/.dotnet/tools/pwsh, and /usr/local/share/dotnet/dotnet (or a link to it)
dotnet --list-sdks     # 10.0.x [/usr/local/share/dotnet/sdk]
```

Run `pwsh ./build.ps1 -Clean` once after switching SDKs, so nothing built by the old one is reused.

### Building

```sh
pwsh ./build.ps1 -TestOnly     # build and test (many tests still fail on a Mac; see the status below)
pwsh ./build.ps1 -Publish      # publish/output/NeonSidekick, smoke-tested
pwsh ./build.ps1 -Package      # publish/package/NeonSidekick-v<version>-osx-arm64.tar.gz
```

The publish prints trim and AOT warnings from Oracle, SqlClient, NumSharp and Whisper.net, and `ILC: Method … will always throw` lines for Oracle's optional cloud assemblies. The Windows publish prints them too; they don't fail the build.

`-Runtime` defaults to `osx-arm64` on a Mac. The script marks the bundled `espeak-ng` executable as runnable and gives it and the app an ad-hoc signature. The package is a `.tar.gz` so the executable bits survive.

A downloaded build isn't notarized, so macOS quarantines it. Clear that once after unpacking: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.

Good to know on a Mac:
* **Secrets go in the Keychain.** API keys and database passwords are encrypted with a key the app keeps in your login Keychain (the *NeonSidekick* item), and a `passwordStore: credman` password is a Keychain item you make with `security add-generic-password -s <target> -a <user> -w`. After each new build macOS asks once whether it may use that key; choose **Always Allow**.
* **The shell is `zsh`.** `run_command` runs in zsh unless you pick `bash` or `powershell` (pwsh) under *Shell default*. Scripts run with `python3`, `node` or `pwsh`.
* **Copy and paste.** Cmd+V pastes through the terminal. `/copy` and the input line's copy use `pbcopy`.
* **Editors.** `/persona` and friends open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; a terminal editor like vim can't share the terminal with the app.
* **Terminals.** Terminal.app and iTerm2 both work. Turn on *Use Option as Meta key* for Alt shortcuts, and hold Fn (Terminal.app) or Option (iTerm2) to select text with the mouse while the app has it.

### Status of the Mac port

The first build and run on a Mac was on 2026-10-06 (Apple Silicon, Terminal.app). What came up, and where each item stands:

**Fixed** (on the `macos-preview` branch):
* **The publish failed to link** (`library 'ssl' not found`): Homebrew's SDK. Use Microsoft's; see [Setting up the Mac](#setting-up-the-mac).
* **The smoke test crashed at exit** (exit code 134, `mutex lock failed: Invalid argument`, with a ggml backtrace). The ggml name is misleading: ggml installs the process-wide crash handler, but the crash is ONNX Runtime 1.22, which aborts at exit on macOS while its environment is still alive. `Program.cs` now releases that environment as its last step on macOS and Linux, when one was made.
* **`git:roundtrip` failed with `AboveSandbox`.** On macOS `/var` and `/tmp` are links into `/private`, and libgit2 reports the resolved path (`/private/var/…`), so a repository in the working directory looked like one above it. Any project under `/tmp` hit the same thing. `GitAccess` now resolves the root's real path (`Files/RealPath.cs`, libc's `realpath`) and maps libgit2's paths back to the root's spelling.
* **`pdf:browser` timed out after 60 s.** Chrome on macOS writes the PDF, or prints the page for `--dump-dom`, and then never exits; every browser-based web fetch would have waited out its timeout too. `HeadlessBrowser` now stops Chrome once the output is whole (the PDF ends with `%%EOF`, the page with `</html>`) and has stayed unchanged for 1.5 s. On Windows Chrome exits by itself within that time.
* **The screen blanked while typing.** Each full redraw (opening the `/` menu, a draft wrapping) erases the pane, asks the terminal where the cursor is, and draws the pane again. The question went through the held frame writer (`FrameWriter`) and stayed in the buffer until the 500 ms timeout, so Terminal.app, which shows output as it arrives, showed a blank pane for half a second. The late answer could also be taken for the next question's, putting the screen out of step. `UnixConsoleInput.QueryCursor` now flushes the held frame with the question; a redraw takes milliseconds.

**Open:**
* **⚙️ and 🛠️ overlap the next character in Terminal.app.** Terminal.app draws these text-default symbols one cell wide even with the emoji selector; the app counts two, as Windows Terminal and iTerm2 draw them. Nothing else on screen shifts. The likely fix is substitute symbols when `TERM_PROGRAM` is `Apple_Terminal`; 🖥️ and ✂️ should be checked the same way.
* **About 680 of the 10,050 tests fail on a Mac.** Nearly all were written for Windows: expected strings with `\` paths or drive letters, or Windows-only APIs (WinMM, the clipboard, WebView2, `cmd.exe`). They need marking as Windows-only or making path-neutral. `-Publish` skips the tests, so this doesn't block a build.
* **Quitting after Kokoro has spoken** hasn't been checked yet against the exit-crash fix.

**Checking a change in Terminal.app.** A small pseudo-terminal relay can run the app in a Terminal.app window, type into it from a script, and log every write and the terminal's answers with timestamps; `screencapture -l <window id>` then shows what Terminal.app actually drew. That is how the blank-screen bug was found: the timestamps showed the cursor question leaving 500 ms late. The capture needs the screen-recording permission for the terminal running the script.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
* **On a Mac, `ld: library 'ssl' not found` at the end of the publish:** the SDK is Homebrew's. See [Setting up the Mac](#setting-up-the-mac).
* **On a Mac, `pwsh: command not found` after installing it as a .NET tool:** `~/.dotnet/tools` isn't on `PATH`. Add it in `~/.zshrc`.
