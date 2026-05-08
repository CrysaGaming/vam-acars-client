# =============================================================================
# release.ps1 — VAM ACARS Client release builder
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
#      .NET 10 to run this — they download one .exe and double-click.
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
#     https://github.com/settings/tokens — fine-grained or classic both work.
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
# tolerate either — if vpk can't find the publish dir or dotnet returns
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
    throw "Tray csproj not found at $TrayCsproj — is this script in the repo root?"
}

# ---- determine version -----------------------------------------------------
# When -Version isn't passed, read <Version> from the csproj. We use a
# simple regex rather than [xml]…SelectSingleNode so this script doesn't
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
Write-Host " VAM ACARS Client — packaging release v$Version"
Write-Host "============================================================"
Write-Host ""

# Verify vpk CLI is available. Without it, the pack step would fail with
# a less-helpful "command not recognised" message, so check up-front.
$vpk = Get-Command vpk -ErrorAction SilentlyContinue
if (-not $vpk) {
    throw "vpk CLI not found on PATH. Install with: dotnet tool install -g vpk"
}
Write-Host "vpk: $($vpk.Source)"
Write-Host "Repo: $RepoUrl"
Write-Host ""

# Kill any running tray. dotnet publish will fail with a file lock on
# VamAcarsClientTray.dll otherwise. taskkill returns non-zero when the
# process isn't running, which we don't want to abort on — wrap and
# swallow. -ErrorAction SilentlyContinue won't help here because
# taskkill writes the "not found" message to stdout, not stderr.
Write-Host "Stopping any running tray app…"
& taskkill -F -IM $MainExe 2>$null
$LASTEXITCODE = 0  # reset; we don't care if it wasn't running

# ---- publish ---------------------------------------------------------------
# Self-contained: include the .NET 10 runtime in the package so users
# don't need a separate runtime install. PublishSingleFile=false because
# Velopack handles its own bundling — single-file publish would actually
# confuse vpk.
#
# Clean publish dir first so we don't accumulate stale files from
# previous SDK versions or removed projects.
if (Test-Path $PublishDir) {
    Write-Host "Cleaning $PublishDir"
    Remove-Item $PublishDir -Recurse -Force
}

Write-Host "Publishing $TrayCsproj to $PublishDir…"
& dotnet publish $TrayCsproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit $LASTEXITCODE)"
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
    Write-Host "Pulling previous release for delta computation…"
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
    & vpk @downloadArgs 2>&1 | Tee-Object -Variable downloadOut | Out-Host
    # download returns non-zero if there's literally no previous release.
    # That's fine for a first-run; we just won't have a delta. Reset.
    $LASTEXITCODE = 0
}

Write-Host "Running vpk pack (id=$VelopackId, version=$Version)…"
& vpk pack `
    --packId $VelopackId `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe $MainExe `
    --packTitle $PackTitle
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed (exit $LASTEXITCODE)"
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

Write-Host "Uploading to GitHub Releases…"
& vpk upload github `
    --repoUrl $RepoUrl `
    --publish `
    --releaseName "v$Version" `
    --tag "v$Version" `
    --token $env:GITHUB_TOKEN
if ($LASTEXITCODE -ne 0) {
    throw "vpk upload github failed (exit $LASTEXITCODE) — partial release may remain on GitHub. Inspect $RepoUrl/releases."
}

Write-Host ""
Write-Host "============================================================"
Write-Host " ✅  Release v$Version published to $RepoUrl/releases"
Write-Host "============================================================"
