param(
    [switch]$SkipTests,
    [switch]$NoPush,
    [switch]$DryRun
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

function Invoke-ToolCapture([string]$FilePath, [string[]]$Arguments, [string]$FailureMessage)
{
    $output = & $FilePath @Arguments
    Assert-LastExitCode $FailureMessage
    return (@($output) -join [Environment]::NewLine).Trim()
}

function Test-VersionString([string]$Version)
{
    return $Version -match '^\d+\.\d+(?:\.\d+)?$'
}

function Get-NextVersion([string]$CurrentVersion)
{
    $parts = $CurrentVersion.Split('.') | ForEach-Object { [int]$_ }
    $parts[$parts.Length - 1]++
    return ($parts -join '.')
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$versionFilePath = Join-Path $repoRoot "version.json"

if (-not (Test-Path $versionFilePath))
{
    throw "version.json was not found at $versionFilePath"
}

$originalVersionFileContent = Get-Content -Raw $versionFilePath
$versionFileUpdated = $false
$commitCreated = $false

$originalDotnetCliHome = $env:DOTNET_CLI_HOME
$originalDotnetSkipFirstTimeExperience = $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE
$originalMsBuildEnableWorkloadResolver = $env:MSBuildEnableWorkloadResolver

try
{
    $versionState = $originalVersionFileContent | ConvertFrom-Json
    $currentVersion = [string]$versionState.currentVersion

    if ([string]::IsNullOrWhiteSpace($currentVersion) -or -not (Test-VersionString $currentVersion))
    {
        throw "version.json must contain currentVersion in X.Y or X.Y.Z format."
    }

    $nextVersion = Get-NextVersion $currentVersion
    $currentBranch = Invoke-ToolCapture "git" @("branch", "--show-current") "Unable to determine the current git branch."

    if ([string]::IsNullOrWhiteSpace($currentBranch))
    {
        throw "Release-Velopack.ps1 must be run from a named branch, not a detached HEAD."
    }

    $existingLocalTag = Invoke-ToolCapture "git" @("tag", "--list", $nextVersion) "Unable to query local git tags."
    if (-not [string]::IsNullOrWhiteSpace($existingLocalTag))
    {
        throw "The tag $nextVersion already exists locally."
    }

    if (-not $NoPush)
    {
        $null = Invoke-ToolCapture "git" @("remote", "get-url", "origin") "The git remote 'origin' is not configured."

        $existingRemoteTag = Invoke-ToolCapture "git" @("ls-remote", "--tags", "origin", "refs/tags/$nextVersion") "Unable to query remote git tags from origin."
        if (-not [string]::IsNullOrWhiteSpace($existingRemoteTag))
        {
            throw "The tag $nextVersion already exists on origin."
        }
    }

    $updatedVersionJson = @{
        currentVersion = $nextVersion
    } | ConvertTo-Json

    Write-Host "Current version: $currentVersion"
    Write-Host "Next version: $nextVersion"

    if ($DryRun)
    {
        Write-Host "Dry run complete. No commit, tag, or push was performed."
        return
    }

    $updatedVersionJson + [Environment]::NewLine | Set-Content -Path $versionFilePath -Encoding utf8
    $versionFileUpdated = $true

    # Match the local environment workaround used by the existing publish script.
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:MSBuildEnableWorkloadResolver = "false"

    Invoke-Tool "dotnet" @("restore", "AudioBit.sln") "dotnet restore failed."
    Invoke-Tool "dotnet" @("build", "AudioBit.sln", "-c", "Release", "--no-restore") "dotnet build failed."

    if (-not $SkipTests)
    {
        Invoke-Tool "dotnet" @("test", "AudioBit.Installer.Tests\AudioBit.Installer.Tests.csproj", "-c", "Release") "dotnet test failed."
    }

    Invoke-Tool "git" @("add", "-A") "git add failed."
    Invoke-Tool "git" @("commit", "-m", "Release $nextVersion") "git commit failed."
    $commitCreated = $true

    Invoke-Tool "git" @("tag", $nextVersion) "git tag failed."

    if ($NoPush)
    {
        Write-Host "Release commit and tag created locally."
        Write-Host "Push manually when ready:"
        Write-Host "git push origin $currentBranch"
        Write-Host "git push origin $nextVersion"
        return
    }

    Invoke-Tool "git" @("push", "origin", $currentBranch) "git push branch failed."
    Invoke-Tool "git" @("push", "origin", $nextVersion) "git push tag failed."

    Write-Host "Release tag $nextVersion pushed."
    Write-Host "GitHub Actions will build and publish the Velopack release."
    Write-Host "Installed builds from that release will be detected by the in-app updater."
}
catch
{
    if ($versionFileUpdated -and -not $commitCreated)
    {
        $originalVersionFileContent | Set-Content -Path $versionFilePath -Encoding utf8
    }

    throw
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
