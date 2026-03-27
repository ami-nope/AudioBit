param(
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

function Get-NextMinorVersion([string]$CurrentVersion)
{
    $parts = $CurrentVersion.Split('.') | ForEach-Object { [int]$_ }
    if ($parts.Length -eq 2)
    {
        return "$($parts[0]).$($parts[1] + 1)"
    }

    if ($parts.Length -eq 3)
    {
        return "$($parts[0]).$($parts[1] + 1).0"
    }

    throw "Unsupported version format '$CurrentVersion'."
}

function Invoke-Script([string]$ScriptPath, [string[]]$Arguments, [string]$FailureMessage)
{
    & powershell "-ExecutionPolicy" "Bypass" "-File" $ScriptPath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw $FailureMessage
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionFilePath = Join-Path $repoRoot "version.json"

if (-not (Test-Path $versionFilePath))
{
    throw "version.json was not found at $versionFilePath"
}

$originalVersionFileContent = Get-Content -Raw $versionFilePath
$versionUpdated = $false

try
{
    $versionState = $originalVersionFileContent | ConvertFrom-Json
    $currentVersion = [string]$versionState.currentVersion
    if ([string]::IsNullOrWhiteSpace($currentVersion) -or -not (Test-VersionString $currentVersion))
    {
        throw "version.json must contain currentVersion in X.Y or X.Y.Z format."
    }

    $nextVersion = Get-NextMinorVersion $currentVersion
    $releaseRootRelative = Join-Path "artifacts\github-release" $nextVersion
    $releaseRoot = Join-Path $repoRoot $releaseRootRelative
    $bootstrapScriptPath = Join-Path $PSScriptRoot "Publish-Release.ps1"
    $bundleScriptPath = Join-Path $PSScriptRoot "Build-ReleaseBundle.ps1"
    $rootReadmePath = Join-Path $releaseRoot "README.txt"
    $zipPath = Join-Path (Split-Path $releaseRoot -Parent) ($nextVersion + ".zip")

    @{
        currentVersion = $nextVersion
    } | ConvertTo-Json | Set-Content -Path $versionFilePath -Encoding utf8
    $versionUpdated = $true

    Write-Host "Current version: $currentVersion"
    Write-Host "Next version: $nextVersion"
    Write-Host "Building GitHub release folder at $releaseRoot"

    $bootstrapArgs = @(
        "-Version", $nextVersion,
        "-Configuration", $Configuration,
        "-Runtime", $Runtime,
        "-VersionFolder", (Join-Path "github-release" $nextVersion)
    )
    if ($NoRestore)
    {
        $bootstrapArgs += "-NoRestore"
    }

    Invoke-Script $bootstrapScriptPath $bootstrapArgs "Publish-Release.ps1 failed."

    $bundleArgs = @(
        "-Version", $nextVersion,
        "-Configuration", $Configuration,
        "-Runtime", $Runtime,
        "-OutputRoot", $releaseRootRelative,
        "-ReleaseFolderName", "GitHub-Upload"
    )
    if ($NoRestore)
    {
        $bundleArgs += "-NoRestore"
    }

    Invoke-Script $bundleScriptPath $bundleArgs "Build-ReleaseBundle.ps1 failed."

    @"
AudioBit GitHub release folder
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Previous version: $currentVersion
New version: $nextVersion
Runtime: $Runtime

Folders in this versioned release root:
- AudioBit-Setup
  Custom AudioBit.Setup bootstrap installer folder for sharing directly with other PCs.
  The payload is Velopack-based, so installs remain updater-friendly.

- GitHub-Upload
  Updater-friendly Velopack assets for GitHub Releases upload.
  Upload the files listed in GitHub-Upload\UPLOAD-TO-GITHUB.txt.

Zip:
  $zipPath

version.json has been updated to:
  $nextVersion
"@ | Set-Content -Path $rootReadmePath -Encoding utf8

    Write-Host "GitHub release folder created."
    Write-Host "Version root: $releaseRoot"
    Write-Host "Bootstrap installer: $(Join-Path $releaseRoot 'AudioBit-Setup')"
    Write-Host "GitHub upload folder: $(Join-Path $releaseRoot 'GitHub-Upload')"
}
catch
{
    if ($versionUpdated)
    {
        $originalVersionFileContent | Set-Content -Path $versionFilePath -Encoding utf8
    }

    throw
}
