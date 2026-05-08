# =============================================================================
# release.ps1 -- VAM ACARS Client release builder
# =============================================================================
#
# Builds a Velopack release package from VamAcarsClient.Tray and
# (optionally) uploads it to the GitHub Releases page at
# github.com/CrysaGaming/vam-acars-client.
#
# Workflow:
#   1. Kill any running tray-app (file lock on VamAcarsClientTray.dll).
#   2. dotnet publish the Tray project to ./publish/ in Release mode,
#      self-contained for win-x64. Velopack ships a copy of the .NET
#      runtime alongside the app, so users don't need a system-installed
#      .NET 10 to run this -- they download one .exe and double-click.
#   3. vpk pack reads the publish dir and produces:
#        - releases/VamAcarsClient-win-Setup.exe   (first-time installer)
#        - releases/VamAcarsClient-{version}-full.nupkg
#        - releases/VamAcarsClient-{version}-delta.nupkg (if a previous
#          release was downloaded with `vpk download github` first)
#        - releases/RELEASES                       (manifest)
#   4. (optional, with -Publish) vpk upload github creates a GitHub
#      Release tagged `v{version}` and uploads the artefacts.
#
# Prerequisites:
#   - vpk CLI: `dotnet tool install -g vpk` (one-time, global)
#   - For -Publish: $env:GITHUB_TOKEN set to a PAT with `contents: write`
#     on CrysaGaming/vam-acars-client. Generate at
#     https://github.com/settings/tokens -- fine-grained or classic both work.
#
# Examples:
#   ./release.ps1                 # local pack only, no upload
#   ./release.ps1 -Version 0.2.0  # override version baked in csproj
#   ./release.ps1 -Publish        # pack AND push to GitHub Releases
#   ./release.ps1 -Version 0.1.0 -Publish  # explicit version + upload
#
# Re-running for the same version:
#   GitHub doesn't allow overwriting a release at the same tag without
#   first deleting it. If you need to re-publish v0.1.0 (e.g. caught a
#   bug right after upload), delete the release on GitHub manually,
#   then re-run with -Publish. The script doesn't auto-delete because
#   it's a destructive op and we'd rather force the reflection.
#
# =============================================================================

[CmdletBinding()]
param(
    # Override the version baked into VamAcarsClient.Tray.csproj. Useful
    # for hotfixes where you want to bump without editing the csproj.
    # Defaults to the <Version> element value parsed from the csproj.
    [string]$Version = "",

    # When set, runs `vpk upload github` after packing. Without this the
    # script just produces local artefacts in ./releases/ for inspection.
    [switch]$Publish
)

# Stop on error; treat warnings as errors. Release builds shouldn't
# tolerate either -- if vpk can't find the publish dir or dotnet returns
# non-zero we want to know immediately, not press through.
$ErrorActionPreference = "Stop"

# ---- locate paths ----------------------------------------------------------
$RepoRoot       = $PSScriptRoot
$TrayCsproj     = Join-Path $RepoRoot "VamAcarsClient.Tray\VamAcarsClient.Tray.csproj"
$PublishDir     = Join-Path $RepoRoot "publish"
$ReleasesDir    = Join-Path $RepoRoot "releases"
$VelopackId     = "VamAcarsClient"
$MainExe        = "VamAcarsClientTray.exe"
$PackTitle      = "VAM ACARS Client"
$RepoUrl        = "https://github.com/CrysaGaming/vam-acars-client"

if (-not (Test-Path $TrayCsproj)) {
    throw "Tray csproj not found at $TrayCsproj -- is this script in the repo root?"
}

# ---- determine version -----------------------------------------------------
# When -Version isn't passed, read <Version> from the csproj. We use a
# simple regex rather than [xml]...SelectSingleNode so this script doesn't
# care about XML namespace shenanigans.
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojContent = Get-Content $TrayCsproj -Raw
    if ($csprojContent -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
        Write-Host "Detected version $Version from csproj"
    } else {
        throw "Could not find <Version> element in $TrayCsproj. Pass -Version explicitly."
    }
}

# Sanity-check version looks like SemVer. Velopack is strict here.
if ($Version -notmatch '^\d+\.\d+\.\d+(-[\w.-]+)?$') {
    throw "Version '$Version' doesn't look like SemVer (e.g. 0.1.0 or 1.2.3-beta1)."
}

# ---- preflight -------------------------------------------------------------
Write-Host ""
Write-Host "============================================================"
Write-Host " VAM ACARS Client -- packaging release v$Version"
Write-Host "============================================================"
Write-Host ""

# Verify vpk CLI is available. Without it, the pack step would fail with
# a less-helpful "command not recognised" message, so check up-front.
# `dotnet tool install -g` writes to ~/.dotnet/tools, which is supposed
# to be on PATH after install but in practice often isn't until the
# user re-opens their shell. We resolve the binary to an absolute path
# and store it in $VpkExe; every later invocation uses & $VpkExe rather
# than relying on PATH lookup, which sidesteps stale-PATH issues
# entirely (Get-Command after $env:Path mutation didn't reliably
# pick up the change in some shells).
$VpkExe = $null
$cmd = Get-Command vpk -ErrorAction SilentlyContinue
if ($cmd) {
    $VpkExe = $cmd.Source
} else {
    $vpkFallback = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
    if (Test-Path $vpkFallback) {
        $VpkExe = $vpkFallback
    }
}
if (-not $VpkExe) {
    throw "vpk CLI not found on PATH or in ~/.dotnet/tools. Install with: dotnet tool install -g vpk"
}
Write-Host "vpk: $VpkExe"
Write-Host "Repo: $RepoUrl"
Write-Host ""

# Kill any running tray. dotnet publish will fail with a file lock on
# VamAcarsClientTray.dll otherwise. Stop-Process is PowerShell-native
# (no PATH dependency on taskkill.exe), -Force skips the friendly
# close-window attempt that GUI apps don't honour anyway, and
# -ErrorAction SilentlyContinue swallows the not-found error when
# the process isn't running. The trailing $null discard makes sure
# we don't accumulate a "no process found" error on $Error.
Write-Host "Stopping any running tray app..."
Get-Process -Name "VamAcarsClientTray" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$LASTEXITCODE = 0  # cmdlets don't set this but defensive reset

# ---- publish ---------------------------------------------------------------
# Self-contained: include the .NET 10 runtime in the package so users
# don't need a separate runtime install. PublishSingleFile=false because
# Velopack handles its own bundling -- single-file publish would actually
# confuse vpk.
#
# Clean publish dir first so we don't accumulate stale files from
# previous SDK versions or removed projects.
if (Test-Path $PublishDir) {
    Write-Host "Cleaning $PublishDir"
    Remove-Item $PublishDir -Recurse -Force
}

Write-Host "Publishing $TrayCsproj to $PublishDir..."
& dotnet publish $TrayCsproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
}

# Belt-and-braces existence check on the published exe. PowerShell's
# $LASTEXITCODE for `dotnet publish` is unreliable when the new
# MSBuild Terminal Logger is active (10.0.x SDKs default to it):
# native-MSBuild errors like NETSDK1152 print "fehlerhaft mit X
# Fehler(n)" to stdout but the exit code can still come back 0
# because the redirected output stream confuses the propagation
# chain. So we positively verify the artefact landed before handing
# off to vpk — vpk itself only fails AFTER it can't find the exe
# with a less-actionable error message ("I searched the following
# paths and none exist:..."). Catching it here keeps the chain of
# blame short.
$expectedExe = Join-Path $PublishDir $MainExe
if (-not (Test-Path $expectedExe)) {
    throw "Publish completed but $MainExe is missing from $PublishDir. Re-check the dotnet publish output above for errors (NETSDK1152 duplicate-output is a common cause)."
}

# ---- vpk pack --------------------------------------------------------------
# Optionally pull the previous release first so vpk can build a delta
# against it. Skipped when -Publish isn't set (no point downloading if
# we won't be pushing out the delta either).
#
# vpk download github fetches the latest release's nupkg into ./releases/
# from where vpk pack picks it up automatically. If there's no previous
# release (first run), the download command emits a warning and exits 0,
# which we tolerate.
if (-not (Test-Path $ReleasesDir)) {
    New-Item -ItemType Directory -Path $ReleasesDir | Out-Null
}

if ($Publish) {
    Write-Host "Pulling previous release for delta computation..."
    # Token isn't strictly required for public-repo reads but supplying
    # it avoids the 60-req/hour anonymous rate limit; we already need
    # the token for upload anyway.
    $downloadArgs = @(
        "download", "github",
        "--repoUrl", $RepoUrl
    )
    if ($env:GITHUB_TOKEN) {
        $downloadArgs += @("--token", $env:GITHUB_TOKEN)
    }
    & $VpkExe @downloadArgs 2>&1 | Tee-Object -Variable downloadOut | Out-Host
    # download returns non-zero if there's literally no previous release.
    # That's fine for a first-run; we just won't have a delta. Reset.
    $LASTEXITCODE = 0
}

Write-Host "Running vpk pack (id=$VelopackId, version=$Version)..."
& $VpkExe pack `
    --packId $VelopackId `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe $MainExe `
    --packTitle $PackTitle
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed (exit $LASTEXITCODE)"
}

# Same defensive existence check as the publish step. vpk's exit code
# propagates more reliably than dotnet's, but it has its own quirks
# (e.g. when stdin is non-interactive vpk can FTL-log without
# propagating non-zero), so a positive artefact check belongs here
# too. Setup.exe is vpk's user-facing first-time installer; if that
# didn't get written, the release is unshippable regardless of what
# the rest of the pipeline thinks.
$expectedSetup = Join-Path $ReleasesDir "$VelopackId-win-Setup.exe"
if (-not (Test-Path $expectedSetup)) {
    throw "vpk pack completed but $VelopackId-win-Setup.exe is missing from $ReleasesDir. Re-check the vpk output above; common cause is the publish exe missing or having a non-Velopack Main entry point."
}

Write-Host ""
Write-Host "Pack succeeded. Artefacts in $ReleasesDir :"
Get-ChildItem $ReleasesDir | ForEach-Object { Write-Host "  $($_.Name)" }
Write-Host ""

# ---- upload (optional) -----------------------------------------------------
if (-not $Publish) {
    Write-Host "Local pack done. Re-run with -Publish to push to GitHub Releases."
    exit 0
}

if (-not $env:GITHUB_TOKEN) {
    throw "GITHUB_TOKEN env var is not set. Set it to a PAT with 'contents: write' scope on CrysaGaming/vam-acars-client and re-run."
}

Write-Host "Uploading to GitHub Releases..."
& $VpkExe upload github `
    --repoUrl $RepoUrl `
    --publish `
    --releaseName "v$Version" `
    --tag "v$Version" `
    --token $env:GITHUB_TOKEN
if ($LASTEXITCODE -ne 0) {
    throw "vpk upload github failed (exit $LASTEXITCODE) -- partial release may remain on GitHub. Inspect $RepoUrl/releases."
}

Write-Host ""
Write-Host "============================================================"
Write-Host " [OK]  Release v$Version published to $RepoUrl/releases"
Write-Host "============================================================"
