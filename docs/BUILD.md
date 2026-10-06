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

* **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for macOS Arm64.
* **The Xcode command line tools** (`xcode-select --install`) for the native link.
* **PowerShell 7** (`brew install powershell`) to run `build.ps1`.

```sh
pwsh ./build.ps1 -TestOnly     # build and test; tests of Windows-only features are skipped
pwsh ./build.ps1 -Publish      # publish/output/NeonSidekick, smoke-tested
pwsh ./build.ps1 -Package      # publish/package/NeonSidekick-v<version>-osx-arm64.tar.gz
```

`-Runtime` defaults to `osx-arm64` on a Mac. The script marks the bundled `espeak-ng` executable as runnable and gives it and the app an ad-hoc signature. The package is a `.tar.gz` so the executable bits survive.

A downloaded build isn't notarized, so macOS quarantines it. Clear that once after unpacking: `xattr -dr com.apple.quarantine NeonSidekick-v<version>-osx-arm64`.

Good to know on a Mac:
* **Secrets go in the Keychain.** API keys and database passwords are encrypted with a key the app keeps in your login Keychain (the *NeonSidekick* item), and a `passwordStore: credman` password is a Keychain item you make with `security add-generic-password -s <target> -a <user> -w`. After each new build macOS asks once whether it may use that key; choose **Always Allow**.
* **The shell is `zsh`.** `run_command` runs in zsh unless you pick `bash` or `powershell` (pwsh) under *Shell default*. Scripts run with `python3`, `node` or `pwsh`.
* **Copy and paste.** Cmd+V pastes through the terminal. `/copy` and the input line's copy use `pbcopy`.
* **Editors.** `/persona` and friends open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; a terminal editor like vim can't share the terminal with the app.
* **Terminals.** Terminal.app and iTerm2 both work. Turn on *Use Option as Meta key* for Alt shortcuts, and hold Fn (Terminal.app) or Option (iTerm2) to select text with the mouse while the app has it.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
