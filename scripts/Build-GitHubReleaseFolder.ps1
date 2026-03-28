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

function Convert-ToSemVer([string]$Value)
{
    $parts = $Value.Split('.')
    if ($parts.Length -eq 2)
    {
        return "$Value.0"
    }

    return $Value
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
    $packageVersion = Convert-ToSemVer $nextVersion
    $releaseRootRelative = Join-Path "artifacts\github-release" $nextVersion
    $releaseRoot = Join-Path $repoRoot $releaseRootRelative
    $githubUploadRoot = Join-Path $releaseRoot "GitHub-Upload"
    $bootstrapInstallerDir = Join-Path $githubUploadRoot "AudioBit-Setup"
    $bootstrapInstallerZipPath = Join-Path $githubUploadRoot "AudioBit-Setup.zip"
    $releaseBriefPath = Join-Path $githubUploadRoot "release-notes-brief.txt"
    $bootstrapScriptPath = Join-Path $PSScriptRoot "Publish-Release.ps1"
    $bundleScriptPath = Join-Path $PSScriptRoot "Build-ReleaseBundle.ps1"
    $rootReadmePath = Join-Path $releaseRoot "README.txt"
    $uploadNotesPath = Join-Path $githubUploadRoot "UPLOAD-TO-GITHUB.txt"
    $zipPath = Join-Path (Split-Path $releaseRoot -Parent) ($nextVersion + ".zip")

    @{
        currentVersion = $nextVersion
    } | ConvertTo-Json | Set-Content -Path $versionFilePath -Encoding utf8
    $versionUpdated = $true

    Write-Host "Current version: $currentVersion"
    Write-Host "Next version: $nextVersion"
    Write-Host "Building GitHub release folder at $releaseRoot"

    Remove-Item $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

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

    $bootstrapArgs = @(
        "-Version", $nextVersion,
        "-Configuration", $Configuration,
        "-Runtime", $Runtime,
        "-VersionFolder", (Join-Path "github-release" (Join-Path $nextVersion "GitHub-Upload")),
        "-SetupFolderName", "AudioBit-Setup",
        "-SkipRootReadme"
    )
    if ($NoRestore)
    {
        $bootstrapArgs += "-NoRestore"
    }

    Invoke-Script $bootstrapScriptPath $bootstrapArgs "Publish-Release.ps1 failed."

    if (-not (Test-Path $bootstrapInstallerDir))
    {
        throw "The bootstrap installer folder was not created at $bootstrapInstallerDir"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Remove-Item $bootstrapInstallerZipPath -Force -ErrorAction SilentlyContinue
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $bootstrapInstallerDir,
        $bootstrapInstallerZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $releaseBriefLines = @(
        "AudioBit release brief",
        "Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")",
        "Previous version: $currentVersion",
        "New version: $nextVersion",
        "Runtime: $Runtime",
        "Channel: stable",
        "Tag to publish: $nextVersion",
        "Primary GitHub setup asset: AudioBit-stable-Setup.exe",
        "Feed asset: RELEASES-stable",
        "Full package: AudioBit-$packageVersion-stable-full.nupkg",
        "Portable package: AudioBit-stable-Portable.zip",
        "Custom bootstrap installer folder: GitHub-Upload\\AudioBit-Setup",
        "Custom bootstrap installer zip: GitHub-Upload\\AudioBit-Setup.zip",
        "Version root zip: $nextVersion.zip",
        "Auto-update support: enabled through Velopack assets and installer payload layout.",
        "Upload guidance: use GitHub-Upload\\UPLOAD-TO-GITHUB.txt for the asset checklist."
    )
    $releaseBriefLines | Set-Content -Path $releaseBriefPath -Encoding utf8

    if (Test-Path $uploadNotesPath)
    {
@"

Optional custom bootstrap installer upload:
- AudioBit-Setup.zip

Brief text release note:
- release-notes-brief.txt
"@ | Add-Content -Path $uploadNotesPath -Encoding utf8
    }

    @"
AudioBit GitHub release folder
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Previous version: $currentVersion
New version: $nextVersion
Runtime: $Runtime

Folders in this versioned release root:
- GitHub-Upload
  Updater-friendly Velopack assets for GitHub Releases upload.
  Upload the files listed in GitHub-Upload\UPLOAD-TO-GITHUB.txt.
  Custom bootstrap installer folder:
    GitHub-Upload\AudioBit-Setup
  Zipped custom bootstrap installer:
    GitHub-Upload\AudioBit-Setup.zip
  Brief text release note:
    GitHub-Upload\release-notes-brief.txt

Zip:
  $zipPath

version.json has been updated to:
  $nextVersion
"@ | Set-Content -Path $rootReadmePath -Encoding utf8

    Write-Host "GitHub release folder created."
    Write-Host "Version root: $releaseRoot"
    Write-Host "GitHub upload folder: $githubUploadRoot"
    Write-Host "Bootstrap installer: $bootstrapInstallerDir"
    Write-Host "Bootstrap installer zip: $bootstrapInstallerZipPath"
    Write-Host "Brief release note: $releaseBriefPath"
}
catch
{
    if ($versionUpdated)
    {
        $originalVersionFileContent | Set-Content -Path $versionFilePath -Encoding utf8
    }

    throw
}
