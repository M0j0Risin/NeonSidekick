# Building from source

How to build, test and publish Neon Sidekick yourself. Back to the [README](../README.md).

## Contents

- [What you need](#what-you-need)
- [Quick start](#quick-start)
- [build.ps1](#buildps1)
- [Tests](#tests)
- [Checking the native exe](#checking-the-native-exe)
- [Building on a Mac](#building-on-a-mac)
- [Troubleshooting](#troubleshooting)

## What you need

* **Windows x64** for the Windows build, or an **Apple Silicon Mac** for the Mac build. NativeAOT builds only for the OS it runs on; see [Building on a Mac](#building-on-a-mac).
* **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).**
* **For the native build (NativeAOT):** Visual Studio 2022 or later, or its Build Tools, with the **Desktop development with C++** workload. A plain `dotnet build` doesn't need it.
* **PowerShell** to run `build.ps1`, not Git Bash: the smoke step hangs under Git Bash.

## Quick start

```powershell
git clone https://github.com/M0j0Risin/NeonSidekick.git
cd NeonSidekick
dotnet build NeonSidekick.slnx -c Release
dotnet run --project src/NeonSidekick -c Release
```

`dotnet run` starts the app on the .NET runtime, which is fine for trying a change. The native, self-contained `NeonSidekick.exe` that releases ship comes from `build.ps1`.

## build.ps1

`build.ps1` is the whole pipeline in one script. CI runs the same script, so a local run matches it.

| Command | What it does |
|---|---|
| `.\build.ps1` | Restore, build, run the tests with coverage, publish the native exe and smoke-test it. |
| `.\build.ps1 -TestOnly` | Restore, build and test, with no publish. The quickest full check. |
| `.\build.ps1 -Publish` | Restore, build, publish the native exe and smoke-test it, with no tests. |
| `.\build.ps1 -Package` | The full run, then the release package in `publish\package\`: a `.zip` on Windows or a `.tar.gz` on a Mac, with the README, the docs, the license and a `.sha256`. It can't be combined with `-TestOnly`. |
| `.\build.ps1 -Package -Tag v1.2.3` | The same, refusing a tag that isn't `v` plus the project's `<Version>`. |
| `.\build.ps1 -CoverageFloor 0` | Reports coverage without failing under the floor (80% by default). |
| `.\build.ps1 -TestOnly -CiLike` | Runs the tests the way a CI runner meets them: an empty home and temp folder, every `NEONSIDEKICK_*` variable cleared (so the live tests skip), and only 2 CPU cores (`-Cores <n>` for another count, at most 64). With `-Repeat` each run gets a fresh home and temp folder. Everything is put back afterwards, even when a run fails. It catches timing races a fast machine hides. |
| `.\build.ps1 -TestOnly -Filter "<filter>" -Repeat 20` | Runs only the tests a `dotnet test` filter picks, as many times as asked, and counts the runs that pass. Coverage isn't measured. Add `-HangTimeout 60s` to stop a hung test sooner than 10 minutes. |
| `.\build.ps1 -Clean` | Deletes every `bin\` and `obj\` folder and `publish\`. |
| `.\build.ps1 -Runtime <rid>` | Picks the runtime: `win-x64` by default on Windows, `osx-arm64` on an Apple Silicon Mac. NativeAOT can't publish for another OS. |

The native exe lands in `publish\output\NeonSidekick.exe`. Close it before a publish: a running copy locks the file.

The build is strict on purpose:
* **Zero warnings.** Any compiler warning fails the build, and so does any NativeAOT warning about the app's own code. Warnings from other libraries are listed and tolerated.
* **Coverage.** The tests must cover at least 80% of the lines.
* **Smoke test.** After publishing, the script runs the native exe with `--smoke`: every native library must load and work, and the Web UI's server must still serve. `dotnet build` and `dotnet test` can't catch a missing native DLL; this can.
* **ASP.NET Core.** The Web UI uses its shared framework, which the .NET SDK carries; a native publish restores its runtime pack (`Microsoft.AspNetCore.App.Runtime.<rid>`) from NuGet.
* **Version check.** `-Package` checks that the published exe's `--version` matches the project's `<Version>`.

## Tests

Run the whole suite through `build.ps1`, not plain `dotnet test`: the script leaves out the tests that load a model onto the GPU and stops a test that hangs for 10 minutes. Save its output to a file to find the failing tests' names:

```powershell
.\build.ps1 -TestOnly *> build.log
Select-String -Path build.log -Pattern '^\s+Failed |Failed!|Passed!'
```

For one class or one test while you work on it, plain `dotnet test` with a filter is fine (`-c Release` is needed next to `--no-build`):

```powershell
dotnet test tests/NeonSidekick.Tests -c Release --filter "FullyQualifiedName~AssistantTests"
```

* The tests run one at a time, by design.
* **Before a CI run**, run the suite with `-CiLike`, then repeat the classes you changed under it (`-CiLike -Filter … -Repeat 20`). Most tests that failed only on CI were timing races that a slower machine shows.
* **Live tests** (a real LLM server, database, camera, microphone, speech model…) are skipped unless their resource is there. [ENVIRONMENT.md](ENVIRONMENT.md#test-suite) lists the variables that turn each one on, and [TESTCONTAINERS.md](TESTCONTAINERS.md) the Docker containers they run against.
* **Browser PDF tests** read and edit hand-written PDFs (an outline, a locked file, Japanese text, a form) in the test machine's Edge, Chrome or Brave (or Chromium on a Mac), headless. Without one they pass without proving anything; the `pdf:cmap`, `pdf:edit` and `shopping:pdf-read` smoke checks prove the published exe.
* **GPU tests:** two tests load an embedded model onto the GPU, so `build.ps1` leaves them out. Run them with `dotnet test tests/NeonSidekick.Tests -c Release --filter Category=LoadsEmbeddedModel` while no other model holds the GPU.

## Checking the native exe

Besides `--smoke`, the published exe has checks for real hardware and servers. [COMMANDLINE.md](COMMANDLINE.md) says what each one does.

* **Hardware:** `--audio-check`, `--voice-check`, `--camera-check`.
* **Databases:** `--sql-check`, `--oracle-check`, `--mysql-check`, `--postgres-check`, `--sqlite-check`.
* **Admin and the rest:** `--unc-check`, `--docker-check`, `--eventlog-check`, `--network-check`, `--services-check`, `--tasks-check`, `--ad-check`.

## Building on a Mac

The Mac build is for Apple Silicon (`osx-arm64`). Each release has a Mac package, so you only need to build it for a change of your own. [On a Mac](MAC.md) is the user's guide: what works and what differs from Windows.

NativeAOT can't build a Mac binary from Windows, so build on the Mac itself. You need:

* **Microsoft's [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** for macOS Arm64, not Homebrew's (see below).
* **The Xcode command line tools** (`xcode-select --install`) for the native link.
* **PowerShell 7** to run `build.ps1`, installed as a .NET tool (see below).

### Setting up the Mac

Homebrew's `dotnet` can't publish NativeAOT: the publish fails at the very end with `ld: library 'ssl' not found`. Homebrew's `powershell` depends on it, so both have to go:

```sh
brew uninstall powershell dotnet          # if they are installed
brew install --cask dotnet-sdk            # Microsoft's SDK, in /usr/local/share/dotnet (asks for your password)
dotnet tool install --global PowerShell   # pwsh, in ~/.dotnet/tools
echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.zshrc
```

The `~/.zshrc` line is needed because the SDK's installer adds `~/.dotnet/tools` with a literal `~`, which zsh doesn't expand. In a new shell, check:

```sh
which pwsh dotnet      # ~/.dotnet/tools/pwsh, and /usr/local/share/dotnet/dotnet (or a link to it)
dotnet --list-sdks     # 10.0.x [/usr/local/share/dotnet/sdk]
```

Run `pwsh ./build.ps1 -Clean` once after switching SDKs, so nothing built by the old one is reused.

### Building

```sh
pwsh ./build.ps1 -TestOnly     # build and test, the Windows-only tests skipped
pwsh ./build.ps1 -TestOnly -CiLike   # as a CI runner: a Mac can't pin cores, so busy loops (2 per core) load it while the tests run
pwsh ./build.ps1 -Publish      # publish/output/NeonSidekick, smoke-tested
pwsh ./build.ps1 -Package      # publish/package/NeonSidekick-v<version>-osx-arm64.tar.gz
```

* **Expected warnings.** The publish prints trim and AOT warnings from Oracle, SqlClient, NumSharp and Whisper.net, and `ILC: Method … will always throw` lines for Oracle's optional cloud assemblies. The Windows publish prints them too; they don't fail the build.
* **Signing.** The script makes the bundled `espeak-ng` executable and gives it and the app an ad-hoc signature.
* **The package** is a `.tar.gz`, so the executable bits survive. It leaves out what a Mac never needs: the other platforms' native files and the debug symbols (`NeonSidekick.dSYM`, kept in `publish/output` for reading crash reports).

### Notes for developers

* **Windows-only tests.** Mark a test whose subject is Windows-only `[WindowsFact]` or `[WindowsTheory]`. A test of a portable feature written with Windows paths or `cmd` lines gets a Unix twin (`…_Unix`, `[UnixFact]`).
* **Python.** The tests need the Xcode tools' `/usr/bin/python3`.
* **Printing tests** never reach paper. The Mac tests and the smoke check draw PDFs and read them back; the live tests (`NEONSIDEKICK_TEST_PRINTER=<queue>`) send jobs held with `job-hold-until=indefinite` and cancel them.
* **The Keychain.** The tests use a Keychain item of their own (`NeonSidekick.Tests` / `master-key`), never the app's; you can delete it in Keychain Access at any time.
* **Seeing the terminal.** `screencapture -l <window id>` captures what Terminal.app or iTerm2 drew (the script's terminal needs the Screen Recording permission).
* **Which app has the keyboard:** ask `NSWorkspace.shared.frontmostApplication`. System Events' `frontmost` never names an accessory app like this one.

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. `build.ps1` adds `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` when it finds it there; otherwise install the C++ build tools, or add the folder that holds `vswhere.exe` to `PATH`.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
* **On a Mac, `ld: library 'ssl' not found` at the end of the publish:** the SDK is Homebrew's. See [Setting up the Mac](#setting-up-the-mac).
* **On a Mac, `pwsh: command not found` after installing it as a .NET tool:** `~/.dotnet/tools` isn't on `PATH`. Add it in `~/.zshrc`.
