param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot,
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-LastExitCode([string]$Message)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw $Message
    }
}

function Invoke-Tool([string]$FilePath, [string[]]$Arguments, [string]$FailureMessage)
{
    & $FilePath @Arguments
    Assert-LastExitCode $FailureMessage
}

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

function Convert-ToAssemblyVersion([string]$SemVer)
{
    return "$SemVer.0"
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
$semVerVersion = Convert-ToSemVer $displayVersion
$assemblyVersion = Convert-ToAssemblyVersion $semVerVersion
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot))
{
    Join-Path $repoRoot ("artifacts\velopack\" + $displayVersion)
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
$publishDir = Join-Path $outputRoot ("publish\" + $Runtime)
$releaseNotesPath = Join-Path $outputRoot "release-notes.md"
$readmePath = Join-Path $outputRoot "README.txt"
$vpkPath = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"

$originalDotnetCliHome = $env:DOTNET_CLI_HOME
$originalDotnetSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$originalMsBuildEnableWorkloadResolver = $env:MSBuildEnableWorkloadResolver
$originalDotnetRollForward = $env:DOTNET_ROLL_FORWARD

try
{
    if (-not (Test-Path $vpkPath))
    {
        Invoke-Tool "dotnet" @("tool", "install", "--global", "vpk", "--version", "0.0.1298") "Unable to install the Velopack CLI."
    }

    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:MSBuildEnableWorkloadResolver = "false"
    $env:DOTNET_ROLL_FORWARD = "Major"

    Remove-Item $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    @"
# AudioBit $displayVersion

- Local updater-friendly Velopack package build
- Channel: stable
- Package version: $semVerVersion
- Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

Install using the generated setup executable from this folder.
"@ | Set-Content -Path $releaseNotesPath -Encoding utf8

    $publishArgs = @(
        "publish",
        "AudioBit.App\AudioBit.App.csproj",
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-p:Version=$semVerVersion",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$semVerVersion",
        "-o", $publishDir
    )

    if ($NoRestore)
    {
        $publishArgs += "--no-restore"
    }

    Write-Host "Building local Velopack package for version $displayVersion"
    Write-Host "Output folder: $outputRoot"

    Invoke-Tool "dotnet" $publishArgs "AudioBit publish failed."

    Invoke-Tool $vpkPath @(
        "pack",
        "--packId", "AudioBit",
        "--packTitle", "AudioBit",
        "--packVersion", $semVerVersion,
        "--channel", "stable",
        "--packDir", $publishDir,
        "--mainExe", "AudioBit.App.exe",
        "--releaseNotes", $releaseNotesPath,
        "--icon", "AudioBit.App\Assets\AudioBit.ico",
        "--outputDir", $outputRoot
    ) "Velopack pack failed."

    $releaseFiles = Get-ChildItem $outputRoot -File
    $setupAsset = $releaseFiles | Where-Object { $_.Name -match 'setup.*\.exe$|.*setup\.exe$' } | Select-Object -First 1
    $feedAsset = $releaseFiles | Where-Object { $_.Name -match '^RELEASES' } | Select-Object -First 1
    $packageAsset = $releaseFiles | Where-Object { $_.Extension -eq '.nupkg' } | Select-Object -First 1
    $setupAssetPath = if ($setupAsset) { $setupAsset.FullName } else { "(not found)" }
    $feedAssetPath = if ($feedAsset) { $feedAsset.FullName } else { "(not found)" }
    $packageAssetPath = if ($packageAsset) { $packageAsset.FullName } else { "(not found)" }

    @"
AudioBit local Velopack package
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Display version: $displayVersion
Package version: $semVerVersion
Runtime: $Runtime
Channel: stable

Version folder:
  $outputRoot

Publish folder:
  $publishDir

Setup asset:
  $setupAssetPath

Feed asset:
  $feedAssetPath

Package asset:
  $packageAssetPath

This build is local only. It is updater-friendly because it uses the same
Velopack packaging/channel format as the GitHub release flow, but it does not
create tags, releases, or uploads.
"@ | Set-Content -Path $readmePath -Encoding utf8

    Write-Host "Local Velopack package created."
    Write-Host "Version folder: $outputRoot"
    if ($setupAsset)
    {
        Write-Host "Setup asset: $($setupAsset.Name)"
    }
}
finally
{
    if ($null -eq $originalDotnetCliHome)
    {
        Remove-Item Env:DOTNET_CLI_HOME -ErrorAction SilentlyContinue
    }
    else
    {
        $env:DOTNET_CLI_HOME = $originalDotnetCliHome
    }

    if ($null -eq $originalDotnetSkipFirstTimeExperience)
    {
        Remove-Item Env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE -ErrorAction SilentlyContinue
    }
    else
    {
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = $originalDotnetSkipFirstTimeExperience
    }

    if ($null -eq $originalMsBuildEnableWorkloadResolver)
    {
        Remove-Item Env:MSBuildEnableWorkloadResolver -ErrorAction SilentlyContinue
    }
    else
    {
        $env:MSBuildEnableWorkloadResolver = $originalMsBuildEnableWorkloadResolver
    }

    if ($null -eq $originalDotnetRollForward)
    {
        Remove-Item Env:DOTNET_ROLL_FORWARD -ErrorAction SilentlyContinue
    }
    else
    {
        $env:DOTNET_ROLL_FORWARD = $originalDotnetRollForward
    }
}
