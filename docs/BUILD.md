# Building from source

How to build, test and publish Neon Sidekick yourself. Back to the [README](../README.md).

## What you need

* **Windows x64.** The release build targets `win-x64` only.
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

## Troubleshooting

* **"'vswhere.exe' is not recognized" during the publish:** the linker can't find Visual Studio. Add `C:\Program Files (x86)\Microsoft Visual Studio\Installer` to `PATH` and run it again.
* **"The file is locked" during the publish:** `NeonSidekick.exe` is still running from `publish\output\`. Close it first.
* **The build fails with a warning:** that's the zero-warning rule. Fix the warning; don't silence it project-wide.
