param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$VersionFolder = "publish-ready",
    [string]$SetupFolderName = "AudioBit-Setup",
    [switch]$SkipRootReadme,
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
$publishRoot = Join-Path $repoRoot ("artifacts\" + $VersionFolder)
$setupDir = Join-Path $publishRoot $SetupFolderName
$payloadDir = Join-Path $setupDir "payload"
$payloadZip = Join-Path $payloadDir ("AudioBit-" + $Runtime + ".zip")
$stagingRoot = Join-Path $publishRoot "_staging"
$rootReadmePath = Join-Path $publishRoot "README.txt"
$setupReadmePath = Join-Path $setupDir "README.txt"
$packScriptPath = Join-Path $PSScriptRoot "Pack-Velopack.ps1"

$originalDotnetCliHome = $env:DOTNET_CLI_HOME
$originalDotnetSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$originalMsBuildEnableWorkloadResolver = $env:MSBuildEnableWorkloadResolver

$publishArgs = @()
if ($NoRestore)
{
    $publishArgs += "--no-restore"
}

Write-Host "Preparing bootstrap installer output at $publishRoot"

try
{
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:MSBuildEnableWorkloadResolver = "false"

    Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

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

    Write-Host "Building Velopack portable payload..."
    & powershell @packScriptArgs
    Assert-LastExitCode "Pack-Velopack.ps1 failed."

    $portablePayload = Get-ChildItem $stagingRoot -File |
        Where-Object { $_.Name -eq "AudioBit-stable-Portable.zip" } |
        Select-Object -First 1

    if ($null -eq $portablePayload)
    {
        throw "Velopack portable payload was not found in $stagingRoot"
    }

    Remove-Item $setupDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $setupDir -Force | Out-Null

    Write-Host "Publishing AudioBit bootstrap installer..."
    dotnet publish (Join-Path $repoRoot "AudioBit.Installer\AudioBit.Installer.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$semVerVersion `
        -p:AssemblyVersion=$assemblyVersion `
        -p:FileVersion=$assemblyVersion `
        -p:InformationalVersion=$semVerVersion `
        @publishArgs `
        -o $setupDir `
        /m:1
    Assert-LastExitCode "AudioBit installer publish failed."

    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
    Copy-Item $portablePayload.FullName -Destination $payloadZip -Force

    $readmeContent = @"
AudioBit bootstrap installer output
Built: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Display version: $displayVersion
Package version: $semVerVersion
Runtime: $Runtime

Installer folder:
  $setupDir

Payload:
  $payloadZip

This setup folder keeps the custom AudioBit.Setup bootstrap installer UI, but the
bundled payload is the Velopack portable layout. Installs made from this setup can
participate in the in-app updater because the installed files include Update.exe,
sq.version, and the Velopack launcher.
"@

    $readmeContent | Set-Content -Path $setupReadmePath -Encoding utf8
    if (-not $SkipRootReadme)
    {
        $readmeContent | Set-Content -Path $rootReadmePath -Encoding utf8
    }

    Write-Host "Bootstrap installer output created."
    Write-Host "Installer folder: $setupDir"
    Write-Host "Payload zip: $payloadZip"
}
finally
{
    Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue

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
