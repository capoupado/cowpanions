<#
.SYNOPSIS
  Builds a Cowpanion release with Velopack: publish -> fetch the previous release (for deltas) -> vpk pack -> hashes.

.DESCRIPTION
  Output goes to ..\dist\releases (kept between releases; it is the delta base and the folder you upload).
  The script never uploads anything. When it finishes it prints the two upload steps:
    1. GitHub Release v<Version> with the installer, the portable zip and SHA256SUMS.txt (what the site links to);
    2. the update feed (releases.win.json + *.nupkg) copied to /var/www/cows-updates on the VPS.
  Unsigned on purpose for now (docs/DECISIONS.md): Windows SmartScreen will warn on first install.

.EXAMPLE
  cd client; .\release.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Feed = "https://cows.carlospoupado.com/updates",
    [string]$PackId = "Cowpanion",
    [string]$PackTitle = "Cowpanion",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist"),
    # Release testing only: skip fetching the previous release from $Feed (no deltas).
    [switch]$NoDownload
)

$ErrorActionPreference = "Stop"
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Version must be SemVer, e.g. 0.1.0" }
# Works in Windows PowerShell 5.1 and PowerShell 7 (no ?. or ?? operators).
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCmd) { $dotnetCmd.Source } else { "C:\Program Files\dotnet\dotnet.exe" }

$publishDir = Join-Path $OutDir "publish\$Version"
$releaseDir = Join-Path $OutDir "releases"
New-Item -ItemType Directory -Force $releaseDir | Out-Null

Push-Location $PSScriptRoot
try {
    & $dotnet tool restore | Out-Null
    if ($LASTEXITCODE) { throw "dotnet tool restore failed" }

    Write-Host "== publish $Version"
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    # Not single-file: Velopack packages a folder and makes better deltas from separate files.
    # BaseOutputPath keeps this away from bin\Release, which a running dev client may lock.
    & $dotnet publish src/Cowpanion.App -c Release -r win-x64 --self-contained `
        -p:Version=$Version -p:BaseOutputPath=bin-publish/ -o $publishDir
    if ($LASTEXITCODE) { throw "dotnet publish failed" }

    if (-not $NoDownload) {
        Write-Host "== previous release from $Feed (delta base)"
        & $dotnet vpk --skip-updates download http --url $Feed -o $releaseDir --allowEmptyChannel
        if ($LASTEXITCODE) { throw "vpk download failed (is the feed reachable?)" }
    }

    Write-Host "== pack"
    $notes = Join-Path $OutDir "RELEASE_NOTES.md"
    $packArgs = @("--skip-updates", "pack", "-u", $PackId, "-v", $Version, "-p", $publishDir, "-e", "Cowpanion.exe",
        "-o", $releaseDir, "--packTitle", $PackTitle, "--packAuthors", "Carlos Poupado", "--shortcuts", "StartMenuRoot")
    if (Test-Path $notes) { $packArgs += @("--releaseNotes", $notes) }
    & $dotnet vpk @packArgs
    if ($LASTEXITCODE) { throw "vpk pack failed" }

    $setup = Join-Path $releaseDir "$PackId-win-Setup.exe"
    $portable = Join-Path $releaseDir "$PackId-win-Portable.zip"
    $sums = Join-Path $releaseDir "SHA256SUMS.txt"
    Get-ChildItem $releaseDir -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object Name |
        ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name } |
        Set-Content -Encoding ascii $sums

    Write-Host ""
    Write-Host "Release $Version is in $releaseDir. Nothing has been uploaded. Next:"
    Write-Host "  1. GitHub release (the site links to these names):"
    Write-Host "     gh release create v$Version `"$setup`" `"$portable`" `"$sums`" --title v$Version --notes-file <notes>"
    Write-Host "  2. Update feed (feed file last, so clients never see a release whose package is missing):"
    Write-Host "     scp `"$releaseDir\$PackId-$Version-*.nupkg`" root@<vps>:/var/www/cows-updates/"
    Write-Host "     scp `"$releaseDir\releases.win.json`" `"$releaseDir\assets.win.json`" root@<vps>:/var/www/cows-updates/"
}
finally {
    Pop-Location
}
