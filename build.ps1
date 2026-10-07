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
    [string]$Runtime
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
    if (Test-Path $CoverageDir) { Remove-Item -Recurse -Force $CoverageDir }
    dotnet test $TestProject -c Release --no-build --verbosity minimal `
        --filter "Category!=LoadsEmbeddedModel" `
        --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type mini `
        --collect:"XPlat Code Coverage" --settings $RunSettings --results-directory $CoverageDir
    if ($LASTEXITCODE -ne 0) { Fail "Tests FAILED" }
    Write-Host "  Tests passed." -ForegroundColor Green

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
