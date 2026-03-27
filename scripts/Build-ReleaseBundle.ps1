param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot,
    [string]$ReleaseFolderName = "AudioBit-Setup",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-VersionString([string]$Value)
{
    return $Value -match '^\d+\.\d+(?:\.\d+)?$'
}

function Get-RequiredBundleAsset(
    [System.IO.FileInfo[]]$Files,
    [scriptblock]$Predicate,
    [string]$Description)
{
    $asset = $Files | Where-Object $Predicate | Select-Object -First 1
    if ($null -eq $asset)
    {
        throw "Required release asset missing: $Description"
    }

    return $asset
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionFilePath = Join-Path $repoRoot "version.json"

if ([string]::IsNullOrWhiteSpace($Version))
{
    if (-not (Test-Path $versionFilePath))
    {
        throw "version.json was not found at $versionFilePath"
    }

    $versionState = Get-Content -Raw $versionFilePath | ConvertFrom-Json
    $Version = [string]$versionState.currentVersion
}

if ([string]::IsNullOrWhiteSpace($Version) -or -not (Test-VersionString $Version))
{
    throw "Version must be in X.Y or X.Y.Z format."
}

$displayVersion = $Version.Trim()
$bundleRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot))
{
    Join-Path $repoRoot ("artifacts\release-bundles\" + $displayVersion)
}
else
{
    if ([System.IO.Path]::IsPathRooted($OutputRoot))
    {
        $OutputRoot
    }
    else
    {
        Join-Path $repoRoot $OutputRoot
    }
}
$bundleZipPath = Join-Path (Split-Path $bundleRoot -Parent) ($displayVersion + ".zip")
$setupDir = Join-Path $bundleRoot $ReleaseFolderName
$bundleReadmePath = Join-Path $setupDir "README.txt"
$notesPath = Join-Path $setupDir "UPLOAD-TO-GITHUB.txt"
$stagingRoot = Join-Path $bundleRoot "_staging"
$packScriptPath = Join-Path $PSScriptRoot "Pack-Velopack.ps1"

Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $bundleZipPath -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null

$packScriptArgs = @(
    "-ExecutionPolicy", "Bypass",
    "-File", $packScriptPath,
    "-Version", $displayVersion,
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-OutputRoot", $stagingRoot
)

if ($NoRestore)
{
    $packScriptArgs += "-NoRestore"
}

Write-Host "Building updater-ready Velopack release bundle for version $displayVersion"
& powershell @packScriptArgs
if ($LASTEXITCODE -ne 0)
{
    throw "Pack-Velopack.ps1 failed."
}

$bundleFiles = @(Get-ChildItem $stagingRoot -File)
$setupAsset = Get-RequiredBundleAsset $bundleFiles { $_.Name -match 'setup.*\.exe$|.*setup\.exe$' } "Velopack setup executable"
$feedAsset = Get-RequiredBundleAsset $bundleFiles { $_.Name -match '^RELEASES' } "Velopack RELEASES feed"
$fullPackageAsset = Get-RequiredBundleAsset $bundleFiles { $_.Extension -eq '.nupkg' } "Velopack full package"
$portableAsset = Get-RequiredBundleAsset $bundleFiles { $_.Name -eq "AudioBit-stable-Portable.zip" } "Velopack portable zip"
$assetsManifest = Get-RequiredBundleAsset $bundleFiles { $_.Name -eq "assets.stable.json" } "assets manifest"
$releasesManifest = Get-RequiredBundleAsset $bundleFiles { $_.Name -eq "releases.stable.json" } "releases manifest"
$releaseNotesAsset = Get-RequiredBundleAsset $bundleFiles { $_.Name -eq "release-notes.md" } "release notes"

New-Item -ItemType Directory -Path $setupDir -Force | Out-Null
foreach ($file in $bundleFiles)
{
    Copy-Item $file.FullName -Destination (Join-Path $setupDir $file.Name)
}

Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue

@"
AudioBit updater-ready release bundle
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Display version: $displayVersion
Runtime: $Runtime

Setup folder:
  $setupDir

This folder contains only the modern Velopack release assets required for
new installs and in-app updates:
- Setup installer
- RELEASES feed file
- Full package
- Portable package
- Release notes and manifest files

Zip:
  $bundleZipPath
"@ | Set-Content -Path $bundleReadmePath -Encoding utf8

@"
MANUAL GITHUB RELEASE UPLOAD

Release title suggestion:
AudioBit $displayVersion

Tag:
$displayVersion

Upload these files from this folder for updater support:
- $($setupAsset.Name)
- $($feedAsset.Name)
- $($fullPackageAsset.Name)
- $($portableAsset.Name)
- $($assetsManifest.Name)
- $($releasesManifest.Name)
- $($releaseNotesAsset.Name)

Optional extra upload:
- $displayVersion.zip
"@ | Set-Content -Path $notesPath -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $bundleRoot,
    $bundleZipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $true)

Write-Host "Release bundle created."
Write-Host "Setup folder: $setupDir"
Write-Host "Zip: $bundleZipPath"
