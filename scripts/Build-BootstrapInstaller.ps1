param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-VersionString([string]$Value)
{
    return $Value -match '^\d+\.\d+(?:\.\d+)?$'
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
$versionFolder = Join-Path "bootstrap-installer" $displayVersion
$publishScriptPath = Join-Path $PSScriptRoot "Publish-Release.ps1"
$scriptArgs = @(
    "-ExecutionPolicy", "Bypass",
    "-File", $publishScriptPath,
    "-Version", $displayVersion,
    "-Configuration", $Configuration,
    "-Runtime", $Runtime,
    "-VersionFolder", $versionFolder
)

if ($NoRestore)
{
    $scriptArgs += "-NoRestore"
}

Write-Host "Building custom bootstrap installer for version $displayVersion"
& powershell @scriptArgs
if ($LASTEXITCODE -ne 0)
{
    throw "Publish-Release.ps1 failed."
}

Write-Host "Bootstrap installer folder:"
Write-Host (Join-Path $repoRoot ("artifacts\" + $versionFolder + "\AudioBit-Setup"))
