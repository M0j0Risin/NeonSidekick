<#
.SYNOPSIS
    Build, test, publish (NativeAOT) and smoke NeonSidekick.

.DESCRIPTION
    Mirrors the CI pipeline. Run from PowerShell, not Git Bash (the smoke gate hangs under MSYS
    and that hang is indistinguishable from the app deadlocking).

        .\build.ps1                # restore + build + test (with coverage) + AOT publish + smoke the published exe
        .\build.ps1 -TestOnly      # restore + build + test
        .\build.ps1 -Publish       # restore + build + AOT publish + smoke (skip tests)
        .\build.ps1 -Clean         # delete bin/, obj/, publish/
        .\build.ps1 -CoverageFloor 0           # report line coverage without gating (the default floor is 80%)
        .\build.ps1 -Package                   # ...and stage publish/package/NeonSidekick-v<ver>-win-x64.zip + .sha256
        .\build.ps1 -Package -Tag v0.2.0       # the same, refusing a tag that is not the csproj <Version> (release.yml)
        .\build.ps1 -TestOnly -CiLike          # the test step as a CI runner meets it: an empty home, fresh temp, no live tests, 2 cores
        .\build.ps1 -TestOnly -CiLike -Cores 1 -Filter "FullyQualifiedName~WebUiPageTests" -Repeat 20 -HangTimeout 60s

    On a Mac (2026-10-06, the macOS build; pwsh 7 and the Xcode command line tools), the same switches:

        pwsh ./build.ps1 -TestOnly             # -Runtime defaults to osx-arm64 there
        pwsh ./build.ps1 -Publish              # publish/output/NeonSidekick, espeak-ng marked executable, ad-hoc signed
        pwsh ./build.ps1 -Package              # publish/package/NeonSidekick-v<ver>-osx-arm64.tar.gz (a tar keeps the exec bits)

    NativeAOT cannot publish across operating systems, so -Runtime only picks between this OS's own RIDs.
#>

[CmdletBinding()]
param(
    [switch]$TestOnly,
    [switch]$Publish,
    [switch]$Clean,
    [double]$CoverageFloor = 80,
    [switch]$Package,
    [string]$Tag,
    [string]$Runtime,
    [switch]$CiLike,
    [int]$Cores = 2,
    [string]$Filter,
    [int]$Repeat = 1,
    [string]$HangTimeout = "10m"
)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 has no $IsWindows; it only ever runs on Windows.
$OnWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or $IsWindows
if (-not $Runtime) {
    if ($OnWindows) {
        $Runtime = "win-x64"
    } elseif ($IsMacOS) {
        $Runtime = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { "osx-arm64" } else { "osx-x64" }
    } else {
        $Runtime = "linux-x64"
    }
}
$ProjectRoot = $PSScriptRoot
$AppProject = Join-Path $ProjectRoot "src/NeonSidekick/NeonSidekick.csproj"
$TestProject = Join-Path $ProjectRoot "tests/NeonSidekick.Tests/NeonSidekick.Tests.csproj"
$PublishDir = Join-Path $ProjectRoot "publish/output"
$Exe = Join-Path $PublishDir $(if ($OnWindows) { "NeonSidekick.exe" } else { "NeonSidekick" })
$CoverageDir = Join-Path $ProjectRoot "publish/coverage"
$RunSettings = Join-Path $ProjectRoot "tests/coverage.runsettings"
$PackageRoot = Join-Path $ProjectRoot "publish/package"
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Write-Section($text) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Fail($text) {
    Write-Host "  $text" -ForegroundColor Red
    exit 1
}

# A filtered or repeated run is a check, not the pipeline (2026-10-19): it measures no coverage, so it never goes on to a publish.
if (($Filter -or $Repeat -gt 1) -and -not $TestOnly) { Fail "-Filter and -Repeat need -TestOnly." }
if ($Repeat -lt 1) { Fail "-Repeat is a count of runs, 1 or more." }
if ($PSBoundParameters.ContainsKey('Cores') -and -not $CiLike) { Fail "-Cores needs -CiLike." }
if ($Cores -lt 1) { Fail "-Cores is a count of cores, 1 or more." }
# An affinity mask is one processor group's, 64 bits (2026-10-20, the code review); past 64 there is nothing it could pin.
if ($Cores -gt 64) { Fail "-Cores is at most 64 (one processor group's affinity)." }
if ($Publish -and $CiLike) { Fail "-CiLike shapes the test step; -Publish has none." }

# -CiLike (2026-10-19, the pre-CI check in .notes/ci_test_debug_guidance.md): the test step as a GitHub runner meets it, since
# nearly every CI-only failure so far was a race a fast, warm machine never showed. The test host gets an empty NEONSIDEKICK_HOME,
# a fresh TEMP/TMP/TMPDIR (a new runner has no %TEMP%\NeonSidekick; the folder itself is left alone, the open app may be using
# it), every other NEONSIDEKICK_* variable cleared, the workflows' dead LLM URL, and $Cores cores: DOTNET_PROCESSOR_COUNT
# everywhere, plus this process's affinity on Windows (children inherit it; --disable-build-servers keeps a warm MSBuild node,
# started at full affinity, from launching the test host) or, on a Mac (no affinity there), busy loops: 2 per core for 2 cores, 4 for
# 1, 1 for 4 or more, each a /bin/sh loop that ends itself once this script's process is gone (a Ctrl+C never leaves them burning).
# Not `taskpolicy -c background` (the first Mac run, 2026-10-19): that clamp made the CPU ~5x slower but every timer ~10x coarser
# (100 sleeps of 10 ms took 15 s, not 1.4 s), as no runner's are, and a test counting real-time sleeps failed every time; 20 loops
# make the CPU ~4x slower with the sleeps on time. Everything is put back afterwards: $env: and the affinity outlive the script in
# the caller's session, and the loops are stopped.
# The fresh temp folder's path is as long as a runner's, never longer (the first -CiLike run, 2026-10-19: five tests that print
# a temp path into a notice or the toolbar row wrapped or padded negative under a 66-character one): 39 characters on Windows,
# C:\Users\runneradmin\AppData\Local\Temp (the runner's account, from its logs; RUNNER~1 is 36), and on a Mac a one-letter sibling
# of this Mac's own TMPDIR, /var/folders/…/<letter>: the runner's /var/folders/…/T/ shape and length, under /var's link to /private/var
# as the runner's is (the first Mac run, 2026-10-19: under /tmp, itself a link to /private/tmp, a test that knows only /var's link
# read `pwd -P` as another folder). The home is a short folder of its own.
function New-CiFolder([string]$parent, [int]$length, [int]$least = 4) {
    $parent = $parent.TrimEnd('\', '/')
    $size = [Math]::Max($least, $length - $parent.Length - 1)
    do {
        $name = -join (1..$size | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
        $path = Join-Path $parent $name
    } while (Test-Path $path)
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

# What -CiLike changed, kept as it changes (2026-10-20, the code review: Enter-CiLike ran before the try, so a throw after it had changed the
# environment skipped Exit-CiLike, and the variables, the temp folders and a Mac's busy loops outlived the run). Enter-CiLike fills it step by
# step, each value saved before it is changed and each folder and loop recorded the moment it exists, so Exit-CiLike undoes exactly what was
# done, however far Enter-CiLike got.
function New-CiState {
    return @{
        Folders = New-Object System.Collections.Generic.List[string]
        Saved = @{}
        Affinity = $null
        Loops = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
    }
}

function Enter-CiLike($state) {
    $machineTemp = [System.IO.Path]::GetTempPath()
    $machineTrimmed = $machineTemp.TrimEnd('/')
    $ciTemp = if ($OnWindows) { New-CiFolder $machineTemp 39 }
        elseif ($IsMacOS) { New-CiFolder (Split-Path $machineTrimmed -Parent) $machineTrimmed.Length 1 }
        else { New-CiFolder "/tmp" $machineTrimmed.Length }
    $state.Folders.Add($ciTemp)
    $ciHome = New-CiFolder $machineTemp 0
    $state.Folders.Add($ciHome)
    $cleared = @(Get-ChildItem env: | Where-Object { $_.Name -like 'NEONSIDEKICK_*' } | ForEach-Object { $_.Name })
    foreach ($name in $cleared + @('NEONSIDEKICK_HOME', 'NEONSIDEKICK_TEST_LLM_URL', 'TEMP', 'TMP', 'TMPDIR', 'DOTNET_PROCESSOR_COUNT')) {
        $state.Saved[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    foreach ($name in $cleared) { Remove-Item "env:$name" }
    $env:NEONSIDEKICK_HOME = $ciHome
    $env:NEONSIDEKICK_TEST_LLM_URL = "http://127.0.0.1:9/v1"
    $env:TEMP = $ciTemp
    $env:TMP = $ciTemp
    if (-not $OnWindows) { $env:TMPDIR = $ciTemp + "/" }
    $cores = [Math]::Min($Cores, [Environment]::ProcessorCount)
    $env:DOTNET_PROCESSOR_COUNT = $cores.ToString($Invariant)
    if ($OnWindows) {
        $self = [System.Diagnostics.Process]::GetCurrentProcess()
        $state.Affinity = $self.ProcessorAffinity
        # 64 cores is every bit of the mask (2026-10-20, the code review: 1L -shl 64 wraps to 1, so the mask came out 0 and the set threw).
        $mask = if ($cores -ge 64) { [long]-1 } else { (1L -shl $cores) - 1 }
        $self.ProcessorAffinity = [IntPtr]$mask
    }
    if ($IsMacOS) {
        $count = [Environment]::ProcessorCount * [Math]::Max(1, [int][Math]::Floor(4 / $cores))
        $busy = "while kill -0 $PID 2>/dev/null; do :; done"
        for ($i = 0; $i -lt $count; $i++) {
            $start = New-Object System.Diagnostics.ProcessStartInfo "/bin/sh"
            $start.ArgumentList.Add("-c")
            $start.ArgumentList.Add($busy)
            $start.UseShellExecute = $false
            $state.Loops.Add([System.Diagnostics.Process]::Start($start))
        }
    }
    $how = if ($OnWindows) { "affinity" } elseif ($IsMacOS) { "DOTNET_PROCESSOR_COUNT, $($state.Loops.Count) busy loops" } else { "DOTNET_PROCESSOR_COUNT only" }
    Write-Host "  CI-like: $cores of $([Environment]::ProcessorCount) cores ($how); home $ciHome; temp $ciTemp ($($ciTemp.Length) characters);" -ForegroundColor Yellow
    Write-Host "  $($cleared.Count) NEONSIDEKICK_* variable(s) cleared, the LLM URL dead (http://127.0.0.1:9/v1)." -ForegroundColor Yellow
}

function Exit-CiLike($state) {
    foreach ($name in $state.Saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $state.Saved[$name])
    }
    if ($null -ne $state.Affinity) {
        [System.Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = $state.Affinity
    }
    foreach ($loop in $state.Loops) {
        if (-not $loop.HasExited) { $loop.Kill() }
        $loop.Dispose()
    }
    # A headless browser a test started can outlive the test host by a moment, holding its profile in the temp folder: a few tries.
    foreach ($folder in $state.Folders) {
        for ($try = 0; $try -lt 10 -and (Test-Path $folder); $try++) {
            Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
            if (Test-Path $folder) { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path $folder) { Write-Host "  CI-like: $folder left behind (a file still open); delete it by hand." -ForegroundColor Yellow }
    }
}

if ($Clean) {
    Write-Section "Clean"
    Get-ChildItem -Path $ProjectRoot -Recurse -Directory -Include bin, obj |
        Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' } |
        ForEach-Object { Remove-Item -Recurse -Force $_.FullName; Write-Host "  removed $($_.FullName)" }
    if (Test-Path (Join-Path $ProjectRoot "publish")) {
        Remove-Item -Recurse -Force (Join-Path $ProjectRoot "publish")
        Write-Host "  removed publish/"
    }
    exit 0
}

Write-Section "Restore"
dotnet restore (Join-Path $ProjectRoot "NeonSidekick.slnx")
if ($LASTEXITCODE -ne 0) { Fail "Restore FAILED" }

# Warnings are captured and counted. The AOT analyzers (IsAotCompatible) report our own
# reflection/trim hazards as warnings here, and a warning that scrolls by is a warning nobody
# reads. The budget for our code is zero.
Write-Section "Build (Release)"
$buildLog = @(dotnet build (Join-Path $ProjectRoot "NeonSidekick.slnx") --no-restore -c Release --verbosity minimal 2>&1)
$buildLog | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { Fail "Build FAILED" }
$buildWarnings = @($buildLog | Where-Object { $_ -match ': warning ' })
if ($buildWarnings.Count -gt 0) {
    Fail "Build produced $($buildWarnings.Count) warning(s); the budget is zero."
}
Write-Host "  Build succeeded, 0 warnings." -ForegroundColor Green

if (-not $Publish) {
    # -c Release is load-bearing next to --no-build. Without a configuration, dotnet test
    # resolves the Debug output path and --no-build suppresses the rebuild that would have
    # failed, so it silently runs whatever stale Debug assembly exists.
    Write-Section "Test (Release, no rebuild, coverage)"
    # Coverage rides on the same run: coverlet.collector is referenced by the test project and
    # tests/coverage.runsettings names the exclusions (the WinMM device classes and Program are
    # proven by the published-exe smoke, not by lines). The default floor (80) was set from a
    # CI-like local run (the live-server and model-gated facts skipping: 88.7 % on 2026-09-11)
    # minus a 5-point margin, rounded down to a multiple of 5; a full local run with the
    # servers and models present reads a few points higher. -CoverageFloor 0 reports only.
    # --blame-hang: a test that stops making progress for 10 minutes is named (Sequence_*.xml,
    # its last entry), its host mini-dumped (threads and stacks; a full dump of the host is
    # gigabytes) under $CoverageDir, and the run failed. Without it the first release run
    # (2026-09-21) sat for GitHub's six-hour job maximum with nothing in the log after the last
    # failure.
    # The facts that load an embedded model onto the GPU are left out (2026-10-01): the app is usually open during a build,
    # its own model holding VRAM, so the VRAM-only fact failed and both ate the card meanwhile. CI skips them anyway (no
    # model installed); a plain dotnet test still runs them where one is.
    # -Filter narrows the run (still without those facts) and -Repeat runs it again and again, each run's hang diagnostics
    # under publish/coverage/run-<n>; every run goes ahead whatever the one before did, so a flake's rate is counted
    # (2026-10-19). Neither measures coverage: a slice of the suite can't meet the floor.
    if (Test-Path $CoverageDir) { Remove-Item -Recurse -Force $CoverageDir }
    $partial = $Filter -or $Repeat -gt 1
    $testFilter = if ($Filter) { "Category!=LoadsEmbeddedModel&($Filter)" } else { "Category!=LoadsEmbeddedModel" }
    # -CiLike is entered and left once per run (2026-10-20, the code review: under -Repeat every run shared one home and one temp folder,
    # so runs 2..N met what run 1 left), each inside its own try, so whatever Enter-CiLike changed is put back even when it throws part-way.
    $failedRuns = New-Object System.Collections.Generic.List[int]
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        for ($run = 1; $run -le $Repeat; $run++) {
            $ciState = if ($CiLike) { New-CiState } else { $null }
            try {
                if ($null -ne $ciState) { Enter-CiLike $ciState }
                $results = if ($Repeat -gt 1) { Join-Path $CoverageDir "run-$run" } else { $CoverageDir }
                $testArgs = @("test", $TestProject, "-c", "Release", "--no-build", "--verbosity", "minimal", "--filter", $testFilter,
                    "--blame-hang", "--blame-hang-timeout", $HangTimeout, "--blame-hang-dump-type", "mini", "--results-directory", $results)
                if (-not $partial) { $testArgs += @("--collect:XPlat Code Coverage", "--settings", $RunSettings) }
                if ($CiLike) { $testArgs += "--disable-build-servers" }
                dotnet @testArgs
                $exitCode = $LASTEXITCODE
            } finally {
                if ($null -ne $ciState) { Exit-CiLike $ciState }
            }
            if ($exitCode -ne 0) { $failedRuns.Add($run) }
            if ($Repeat -gt 1) {
                $verdict = if ($exitCode -eq 0) { "passed" } else { "FAILED" }
                Write-Host "  Run $run of ${Repeat}: $verdict" -ForegroundColor $(if ($exitCode -eq 0) { "Green" } else { "Red" })
            }
        }
    } finally {
        $watch.Stop()
    }

    Write-Host "  Tests took $($watch.Elapsed.ToString('hh\:mm\:ss', $Invariant))$(if ($Repeat -gt 1) { " for $Repeat runs" })."
    if ($Repeat -gt 1) {
        $passedRuns = $Repeat - $failedRuns.Count
        Write-Host "  Repeat: $passedRuns of $Repeat runs passed$(if ($failedRuns.Count -gt 0) { "; failed: run " + ($failedRuns -join ', ') })." `
            -ForegroundColor $(if ($failedRuns.Count -eq 0) { "Green" } else { "Red" })
    }
    if ($failedRuns.Count -gt 0) { Fail "Tests FAILED" }
    Write-Host "  Tests passed." -ForegroundColor Green
    if ($partial) {
        Write-Section "Done (a filtered or repeated run; coverage not measured)"
        exit 0
    }

    $report = Get-ChildItem -Path $CoverageDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $report) { Fail "No coverage.cobertura.xml under $CoverageDir; the collector did not run." }
    [xml]$cobertura = Get-Content $report.FullName
    $summary = $cobertura.coverage
    $lineRate = [double]::Parse($summary.'line-rate', $Invariant) * 100
    $coverageLine = "Coverage: " + $lineRate.ToString("N1", $Invariant) + "% lines (" +
        $summary.'lines-covered' + "/" + $summary.'lines-valid' + ", floor " + $CoverageFloor.ToString($Invariant) + "%)"
    if ($lineRate -lt $CoverageFloor) {
        Write-Host "  $coverageLine" -ForegroundColor Red
        Fail "Coverage below the floor."
    }
    Write-Host "  $coverageLine" -ForegroundColor Green
    Write-Host "  Report: $($report.FullName)"
}

if ($TestOnly) {
    if ($Package) { Fail "-Package needs the publish; drop -TestOnly." }
    Write-Section "Done (tests only)"
    exit 0
}

Write-Section "Publish NativeAOT ($Runtime)"

# The native link step shells out to vswhere.exe. In a shell with
# NoDefaultCurrentDirectoryInExePath=1 (Git Bash, some terminals) that lookup fails with a
# mangled MSB3073, so put the installer directory on PATH when vswhere is not already there.
# Windows only: a Mac links with the Xcode command line tools' clang.
if ($OnWindows -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $vswhereDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (Test-Path (Join-Path $vswhereDir "vswhere.exe")) {
        $env:PATH = "$vswhereDir;$env:PATH"
    } else {
        Write-Host "  WARNING: vswhere.exe not found; if the native link fails, install the VS C++ build tools." -ForegroundColor Yellow
    }
}

$publishLog = @(dotnet publish $AppProject -c Release -r $Runtime --self-contained `
    /p:PublishAot=true /p:StripSymbols=true -o $PublishDir 2>&1)
$publishLog | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { Fail "Publish FAILED" }

# Third-party AOT warnings (Whisper.net's library loader, the OpenAI SDK) are reported and
# tolerated; a warning that points into our own code fails the build. ILC appends the project
# path "[...NeonSidekick.csproj]" to EVERY warning line, so that suffix is stripped before
# deciding whose warning it is: ours name a NeonSidekick.* symbol or a src/NeonSidekick/*.cs file.
$aotWarnings = @($publishLog | Where-Object { $_ -match 'warning IL\d+' } | Sort-Object -Unique)
$ownWarnings = @($aotWarnings | Where-Object {
    $text = $_ -replace '\s*\[[^\]]*\.csproj\]\s*$', ''
    ($text -match 'src[\\/]NeonSidekick[\\/].*\.cs') -or ($text -match '\bNeonSidekick\.[A-Z]')
})
$aotWarnings | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
Write-Host ""
Write-Host "  AOT warnings: $($aotWarnings.Count) unique, $($ownWarnings.Count) in our code." -ForegroundColor $(if ($ownWarnings.Count -eq 0) { "Green" } else { "Red" })
if ($ownWarnings.Count -gt 0) { Fail "AOT warnings in NeonSidekick code; fix them, do not suppress them." }

# macOS (2026-10-06): NuGet unpacks packages without Unix modes, so KokoroSharp's espeak-ng executables (named .dll
# whatever they are) land without the exec bit; and an arm64 Mac runs no unsigned code, so they and the exe get an
# ad-hoc signature (the linker signs the exe already; signing again after the strip is harmless). The app sets the bit
# itself too (KokoroInProcessSynthesizer.EnsureEspeakRunnable), for a copy that lost it.
if ($IsMacOS) {
    $espeakExes = @(Get-ChildItem -Path (Join-Path $PublishDir "espeak") -Filter "espeak-ng-macos-*.dll" -ErrorAction SilentlyContinue)
    foreach ($file in $espeakExes) {
        chmod +x $file.FullName
        if ($LASTEXITCODE -ne 0) { Fail "chmod +x $($file.Name) FAILED" }
    }
    foreach ($path in @($Exe) + @($espeakExes | ForEach-Object { $_.FullName })) {
        codesign --force --sign - $path 2>&1 | ForEach-Object { Write-Host "  $_" }
        if ($LASTEXITCODE -ne 0) { Fail "codesign $path FAILED" }
    }
    Write-Host "  espeak-ng marked executable; $(1 + $espeakExes.Count) file(s) ad-hoc signed." -ForegroundColor Green
}

Write-Section "Smoke (published binary)"
if (-not (Test-Path $Exe)) { Fail "No published binary at $Exe" }

# System.Diagnostics.Process rather than Start-Process -PassThru: the latter does not reliably
# populate ExitCode. stdout is drained before the wait, or a full pipe deadlocks.
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = (Resolve-Path $Exe).Path
$info.Arguments = "--smoke"
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.UseShellExecute = $false
$info.StandardOutputEncoding = [System.Text.Encoding]::UTF8

$proc = [System.Diagnostics.Process]::Start($info)
$smokeOut = $proc.StandardOutput.ReadToEnd()
$smokeErr = $proc.StandardError.ReadToEnd()
if (-not $proc.WaitForExit(120000)) {
    $proc.Kill()
    Fail "Smoke timed out after 120 s."
}

# One line per check, its wrapped detail joined back on (2026-10-04): the redirected console wraps at 80 columns,
# and the old case-insensitive 'PASS|FAIL' filter kept only the continuation lines that happened to hold those
# letters ("(Failed to connect…" from postgres:driver's expected refusal, shown red; "password" from docker's) and
# dropped the rest. A check starts at an indented PASS/FAIL verdict, matched case-sensitively; the lines after it up
# to the next check or a blank line are its detail; the SMOKE PASS/FAIL summary stands alone.
# Since the second 2026-10-04 review the smoke run writes each check on one line when its output is redirected
# (SidekickApp.SmokeRedirectedWidth): a long word's last piece of exactly 80 columns looked the same as one in its middle,
# so no rule here could tell whether to glue the next line on. A continuation line, should one still come, is joined back
# with a space.
$checkLines = New-Object System.Collections.Generic.List[string]
$current = $null
foreach ($line in $smokeOut -split "`r?`n") {
    if ($line -cmatch '^\s+(PASS|FAIL)\s' -or $line -cmatch '^SMOKE (PASS|FAIL)\b') {
        if ($null -ne $current) { $checkLines.Add($current) }
        $current = $line.TrimEnd()
    } elseif ($line.Trim().Length -eq 0) {
        if ($null -ne $current) { $checkLines.Add($current) }
        $current = $null
    } elseif ($null -ne $current) {
        $current = $current + " " + $line.Trim()
    }
}
if ($null -ne $current) { $checkLines.Add($current) }
foreach ($check in $checkLines) {
    $colour = if ($check -cmatch '^\s*(SMOKE )?FAIL\b') { "Red" } else { "Green" }
    Write-Host "  $check" -ForegroundColor $colour
}
if ($proc.ExitCode -ne 0) {
    if ($smokeErr) { Write-Host $smokeErr -ForegroundColor Red }
    Fail "Smoke FAILED (exit $($proc.ExitCode))."
}

if ($Package) {
    Write-Section "Package"

    # The version is the csproj <Version>, nothing else: the exe prints it (--version), the
    # folder and the zip are named after it, and a release tag must equal it. Plain semver only.
    [xml]$csproj = Get-Content $AppProject
    $version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if (-not $version) { Fail "No <Version> in $AppProject." }
    if ($Tag) {
        if ($Tag -notmatch '^v\d+\.\d+\.\d+$') { Fail "Tag '$Tag' is not a plain semver tag (vX.Y.Z)." }
        if ($Tag -ne "v$version") { Fail "Tag '$Tag' does not match the csproj <Version> ($version); bump the csproj and tag again." }
    }

    $versionOut = (& $Exe --version | Out-String).Trim()
    if ($versionOut -ne "NeonSidekick $version") {
        Fail "The published exe reports '$versionOut', expected 'NeonSidekick $version'."
    }

    $stageName = "NeonSidekick-v$version-$Runtime"
    $stage = Join-Path $PackageRoot $stageName
    if (Test-Path $PackageRoot) { Remove-Item -Recurse -Force $PackageRoot }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $stage -Recurse
    if (-not $OnWindows) {
        # What a Mac never runs stays out of the tarball (2026-10-06, the first Mac package: 566 MB unpacked): NativeAOT's
        # debug symbols (NeonSidekick.dSYM, 247 MB; kept in publish/output for reading a crash), the Windows ONNX Runtime
        # dlls the package brings along, and espeak-ng for every platform but this one. The smoke ran on publish/output.
        $espeakOwn = if ($Runtime -eq "osx-arm64") { "espeak-ng-macos-arm64.dll" } else { "espeak-ng-macos-amd64.dll" }
        $leftOut = @(Get-ChildItem -Path $stage -Filter "*.dSYM" -Directory) +
            @(Get-ChildItem -Path $stage -Filter "onnxruntime*.dll" -File) +
            @(Get-ChildItem -Path (Join-Path $stage "espeak") -Filter "espeak-ng-*.dll" -File | Where-Object { $_.Name -ne $espeakOwn })
        foreach ($item in $leftOut) {
            Remove-Item -Recurse -Force $item.FullName
        }
        Write-Host "  left out of the package: $(($leftOut | ForEach-Object { $_.Name }) -join ', ')"
        if (-not (Test-Path (Join-Path (Join-Path $stage "espeak") $espeakOwn))) { Fail "The package lost $espeakOwn." }
    }
    Copy-Item -Path (Join-Path $ProjectRoot "LICENSE") -Destination $stage
    Copy-Item -Path (Join-Path $ProjectRoot "README.md") -Destination $stage
    # README links into docs\ (the full references and HEADLESS.md, moved there on 2026-10-05), so the zip keeps that layout.
    $stageDocs = Join-Path $stage "docs"
    New-Item -ItemType Directory -Path $stageDocs | Out-Null
    Copy-Item -Path (Join-Path $ProjectRoot "docs/*.md") -Destination $stageDocs

    if (-not $OnWindows) {
        # A tar.gz off Windows (2026-10-06, the macOS build): a zip written here keeps no Unix modes, and the exe and
        # espeak-ng must stay executable. The system tar (bsdtar on macOS) keeps them.
        $tarball = Join-Path $PackageRoot "$stageName.tar.gz"
        tar -czf $tarball -C $PackageRoot $stageName
        if ($LASTEXITCODE -ne 0) { Fail "tar FAILED" }
        $hash = (Get-FileHash -Algorithm SHA256 -Path $tarball).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText("$tarball.sha256", "$hash *$stageName.tar.gz`n", [System.Text.Encoding]::ASCII)
        $tarSize = [Math]::Round((Get-Item $tarball).Length / 1MB, 1)
        Write-Host "  $tarball  ($tarSize MB)" -ForegroundColor Green
        Write-Host "  sha256 $hash"
    } else {
        # Entries are written by hand with forward slashes. Compress-Archive, and ZipFile under
        # Windows PowerShell 5.1 (.NET Framework), write backslash entry names, which some unzippers
        # turn into files called "dir\file" instead of a directory; CI (pwsh 7) would then produce a
        # different zip from a local run.
        $zip = Join-Path $PackageRoot "$stageName.zip"
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $base = (Get-Item $PackageRoot).FullName.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $zipStream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
        try {
            $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                Get-ChildItem -Path $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
                    $entryName = $_.FullName.Substring($base.Length).Replace('\', '/')
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
                }
            } finally {
                $archive.Dispose()
            }
        } finally {
            $zipStream.Dispose()
        }
        $hash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText("$zip.sha256", "$hash *$stageName.zip`n", [System.Text.Encoding]::ASCII)

        $zipSize = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
        Write-Host "  $zip  ($zipSize MB)" -ForegroundColor Green
        Write-Host "  sha256 $hash"
    }
}

Write-Section "Done"
$size = [Math]::Round((Get-Item $Exe).Length / 1MB, 1)
Write-Host "  $Exe  ($size MB)" -ForegroundColor Green
