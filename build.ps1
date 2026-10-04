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
#>

[CmdletBinding()]
param(
    [switch]$TestOnly,
    [switch]$Publish,
    [switch]$Clean,
    [double]$CoverageFloor = 80,
    [switch]$Package,
    [string]$Tag
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$AppProject = Join-Path $ProjectRoot "src/NeonSidekick/NeonSidekick.csproj"
$TestProject = Join-Path $ProjectRoot "tests/NeonSidekick.Tests/NeonSidekick.Tests.csproj"
$PublishDir = Join-Path $ProjectRoot "publish/output"
$Exe = Join-Path $PublishDir "NeonSidekick.exe"
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
        Where-Object { $_.FullName -notmatch '\\\.git\\' } |
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

Write-Section "Publish NativeAOT (win-x64)"

# The native link step shells out to vswhere.exe. In a shell with
# NoDefaultCurrentDirectoryInExePath=1 (Git Bash, some terminals) that lookup fails with a
# mangled MSB3073, so put the installer directory on PATH when vswhere is not already there.
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $vswhereDir = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    if (Test-Path (Join-Path $vswhereDir "vswhere.exe")) {
        $env:PATH = "$vswhereDir;$env:PATH"
    } else {
        Write-Host "  WARNING: vswhere.exe not found; if the native link fails, install the VS C++ build tools." -ForegroundColor Yellow
    }
}

$publishLog = @(dotnet publish $AppProject -c Release -r win-x64 --self-contained `
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
# A wrapped line is joined back with a space, save one piece of a word too long for a line (the 2026-10-04 review: a path
# came back "…keeps-going-an d-going…"): Spectre starts such a word on a line of its own and cuts it every 80 columns, so a
# line of exactly 80 with no space in it is a piece, and what follows it is glued on. A word-boundary wrap can fill 80
# columns too, but always has a space in it.
$SmokeWidth = 80
$checkLines = New-Object System.Collections.Generic.List[string]
$current = $null
$previous = ""
foreach ($line in $smokeOut -split "`r?`n") {
    if ($line -cmatch '^\s+(PASS|FAIL)\s' -or $line -cmatch '^SMOKE (PASS|FAIL)\b') {
        if ($null -ne $current) { $checkLines.Add($current) }
        $current = $line.TrimEnd()
    } elseif ($line.Trim().Length -eq 0) {
        if ($null -ne $current) { $checkLines.Add($current) }
        $current = $null
    } elseif ($null -ne $current) {
        $piece = $previous.Length -eq $SmokeWidth -and -not $previous.Contains(' ')
        $current = $current + $(if ($piece) { "" } else { " " }) + $line.Trim()
    }
    $previous = $line
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

    $stageName = "NeonSidekick-v$version-win-x64"
    $stage = Join-Path $PackageRoot $stageName
    if (Test-Path $PackageRoot) { Remove-Item -Recurse -Force $PackageRoot }
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $stage -Recurse
    Copy-Item -Path (Join-Path $ProjectRoot "LICENSE") -Destination $stage
    Copy-Item -Path (Join-Path $ProjectRoot "README.md") -Destination $stage

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

Write-Section "Done"
$size = [Math]::Round((Get-Item $Exe).Length / 1MB, 1)
Write-Host "  $Exe  ($size MB)" -ForegroundColor Green
