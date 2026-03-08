param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$VersionFolder = "publish-ready",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishRoot = Join-Path $repoRoot ("artifacts\" + $VersionFolder)
$portableDir = Join-Path $publishRoot "AudioBit-$Runtime-portable"
$setupDir = Join-Path $publishRoot "AudioBit-Setup"
$payloadDir = Join-Path $setupDir "payload"
$payloadZip = Join-Path $payloadDir "AudioBit-$Runtime.zip"
$readmePath = Join-Path $publishRoot "README.txt"

Write-Host "Preparing publish-ready folder at $publishRoot"

$originalDotnetCliHome = $env:DOTNET_CLI_HOME
$originalDotnetSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$originalMsBuildEnableWorkloadResolver = $env:MSBuildEnableWorkloadResolver

$publishArgs = @()
if ($NoRestore)
{
    $publishArgs += "--no-restore"
}

function Assert-LastExitCode([string]$Message)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw $Message
    }
}

try
{
    # The local .NET 10 SDK install resolves workload imports incorrectly unless this is disabled.
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:MSBuildEnableWorkloadResolver = "false"

    Remove-Item $portableDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $portableDir | Out-Null
    New-Item -ItemType Directory -Path $setupDir | Out-Null

    Write-Host "Publishing AudioBit app..."
    dotnet publish (Join-Path $repoRoot "AudioBit.App\AudioBit.App.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        @publishArgs `
        -o $portableDir `
        /m:1
    Assert-LastExitCode "AudioBit app publish failed."

    Write-Host "Publishing custom installer..."
    dotnet publish (Join-Path $repoRoot "AudioBit.Installer\AudioBit.Installer.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        @publishArgs `
        -o $setupDir `
        /m:1
    Assert-LastExitCode "AudioBit installer publish failed."

    New-Item -ItemType Directory -Path $payloadDir | Out-Null
    Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $portableDir,
        $payloadZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    @"
AudioBit publish-ready output
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Runtime: $Runtime

Installer:
  $setupDir

Portable app publish:
  $portableDir

Run AudioBit.Setup.exe from the installer folder to install.
"@ | Set-Content -Path $readmePath

    Write-Host "Publish-ready output created."
    Write-Host "Installer folder: $setupDir"
    Write-Host "Portable folder: $portableDir"
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
}
