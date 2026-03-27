param(
    [switch]$SkipTests,
    [switch]$NoPush,
    [switch]$DryRun,
    [int]$ReleaseWaitTimeoutMinutes = 20
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

function Get-GithubRepoSlug()
{
    $originUrl = Invoke-ToolCapture "git" @("remote", "get-url", "origin") "The git remote 'origin' is not configured."
    $match = [regex]::Match($originUrl, '(?:github\.com[:/])(?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?$')
    if (-not $match.Success)
    {
        throw "Unable to determine the GitHub repository from origin '$originUrl'."
    }

    return "$($match.Groups['owner'].Value)/$($match.Groups['repo'].Value)"
}

function Test-GhAuthentication()
{
    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue))
    {
        return $false
    }

    try
    {
        $null = Invoke-ToolCapture "gh" @("auth", "status", "-h", "github.com") "GitHub CLI authentication check failed."
        return $true
    }
    catch
    {
        return $false
    }
}

function TryGetReleaseWorkflowRun([string]$RepositorySlug, [string]$TagName)
{
    try
    {
        $json = Invoke-ToolCapture "gh" @(
            "run", "list",
            "-R", $RepositorySlug,
            "--workflow", "Release Velopack",
            "--branch", $TagName,
            "--event", "push",
            "--limit", "1",
            "--json", "databaseId,status,conclusion,url,displayTitle"
        ) "Unable to query the Release Velopack workflow run."

        if ([string]::IsNullOrWhiteSpace($json) -or $json -eq "[]")
        {
            return $null
        }

        $runs = $json | ConvertFrom-Json
        return @($runs)[0]
    }
    catch
    {
        return $null
    }
}

function TryGetPublishedReleaseWithGh([string]$RepositorySlug, [string]$TagName)
{
    try
    {
        $json = Invoke-ToolCapture "gh" @(
            "release", "view", $TagName,
            "-R", $RepositorySlug,
            "--json", "url,isDraft,body,assets"
        ) "Unable to query the GitHub release."

        if ([string]::IsNullOrWhiteSpace($json))
        {
            return $null
        }

        return $json | ConvertFrom-Json
    }
    catch
    {
        return $null
    }
}

function Test-ReleaseAssetsReady($Release)
{
    if ($null -eq $Release -or $Release.isDraft)
    {
        return $false
    }

    $assetNames = @($Release.assets | ForEach-Object { $_.name })
    $hasSetupAsset = @($assetNames | Where-Object { $_ -match 'setup.*\.exe$|.*setup\.exe$' }).Count -gt 0
    $hasFeedAsset = @($assetNames | Where-Object { $_ -match '^RELEASES' }).Count -gt 0
    $hasPackageAsset = @($assetNames | Where-Object { $_ -match '\.nupkg$' }).Count -gt 0
    $hasNotes = -not [string]::IsNullOrWhiteSpace([string]$Release.body)

    return $hasSetupAsset -and $hasFeedAsset -and $hasPackageAsset -and $hasNotes
}

function Get-WorkflowFailureDetails([string]$RepositorySlug, $WorkflowRun)
{
    if ($null -eq $WorkflowRun)
    {
        return "The release workflow failed before a GitHub release was published."
    }

    try
    {
        return Invoke-ToolCapture "gh" @("run", "view", [string]$WorkflowRun.databaseId, "-R", $RepositorySlug) "Unable to view the failed workflow run."
    }
    catch
    {
        return "Workflow URL: $($WorkflowRun.url)"
    }
}

function Wait-ForPublishedGitHubRelease([string]$RepositorySlug, [string]$TagName, [int]$TimeoutMinutes)
{
    $releaseApiUrl = "https://api.github.com/repos/$RepositorySlug/releases/tags/$TagName"
    $deadline = [DateTime]::UtcNow.AddMinutes([Math]::Max(1, $TimeoutMinutes))
    $ghAuthenticated = Test-GhAuthentication

    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ($ghAuthenticated)
        {
            $workflowRun = TryGetReleaseWorkflowRun $RepositorySlug $TagName
            if ($null -ne $workflowRun -and $workflowRun.status -eq "completed" -and $workflowRun.conclusion -ne "success")
            {
                $failureDetails = Get-WorkflowFailureDetails $RepositorySlug $workflowRun
                throw "GitHub Actions release workflow failed for tag '$TagName'.`n`n$failureDetails"
            }

            $release = TryGetPublishedReleaseWithGh $RepositorySlug $TagName
            if (Test-ReleaseAssetsReady $release)
            {
                return $release
            }
        }
        else
        {
            try
            {
                $release = Invoke-RestMethod -Headers @{ 'User-Agent' = 'AudioBit-ReleaseScript' } -Uri $releaseApiUrl
                if (Test-ReleaseAssetsReady $release)
                {
                    return $release
                }
            }
            catch
            {
                # Release may not exist yet while GitHub Actions is still running.
            }
        }

        Start-Sleep -Seconds 15
    }

    throw "Timed out waiting for the GitHub release '$TagName' to publish with setup, feed, package assets, and release notes. If GitHub Actions failed, run 'gh run view -R $RepositorySlug' to inspect the latest release workflow."
}

function Sync-BranchWithOrigin([string]$BranchName)
{
    Invoke-Tool "git" @("fetch", "origin", $BranchName) "git fetch failed."

    $counts = Invoke-ToolCapture "git" @("rev-list", "--left-right", "--count", "HEAD...origin/$BranchName") "Unable to compare the local branch with origin."
    $parts = $counts -split '\s+'
    if ($parts.Length -lt 2)
    {
        throw "Unable to parse branch divergence information from git."
    }

    $aheadCount = [int]$parts[0]
    $behindCount = [int]$parts[1]
    if ($behindCount -le 0)
    {
        return
    }

    Write-Host "origin/$BranchName is ahead by $behindCount commit(s). Rebasing before release."

    $statusBeforeSync = Invoke-ToolCapture "git" @("status", "--porcelain") "Unable to inspect the working tree."
    $stashName = "release-velopack-autostash-" + [Guid]::NewGuid().ToString("N")
    $stashCreated = $false

    try
    {
        if (-not [string]::IsNullOrWhiteSpace($statusBeforeSync))
        {
            Invoke-Tool "git" @("stash", "push", "--include-untracked", "--message", $stashName) "git stash failed."
            $stashCreated = $true
        }

        Invoke-Tool "git" @("pull", "--rebase", "origin", $BranchName) "git pull --rebase failed."

        if ($stashCreated)
        {
            Invoke-Tool "git" @("stash", "pop") "git stash pop failed after rebasing."
        }
    }
    catch
    {
        if ($stashCreated)
        {
            Write-Warning "Your local changes were stashed as '$stashName'. Reapply them after resolving the sync failure."
        }

        throw
    }
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

    if (-not $NoPush -and -not $DryRun)
    {
        $null = Invoke-ToolCapture "git" @("remote", "get-url", "origin") "The git remote 'origin' is not configured."
        Sync-BranchWithOrigin $currentBranch
    }

    $existingLocalTag = Invoke-ToolCapture "git" @("tag", "--list", $nextVersion) "Unable to query local git tags."
    if (-not [string]::IsNullOrWhiteSpace($existingLocalTag))
    {
        throw "The tag $nextVersion already exists locally."
    }

    if (-not $NoPush)
    {
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

    $repoSlug = Get-GithubRepoSlug
    Write-Host "Waiting for GitHub Actions to publish release $nextVersion..."
    $publishedRelease = Wait-ForPublishedGitHubRelease $repoSlug $nextVersion $ReleaseWaitTimeoutMinutes

    Write-Host "Release tag $nextVersion pushed."
    Write-Host "Published release: $($publishedRelease.html_url)"
    Write-Host "Installed builds from that release will be detected by the in-app updater."
}
catch
{
    if ($versionFileUpdated -and -not $commitCreated)
    {
        $originalVersionFileContent | Set-Content -Path $versionFilePath -Encoding utf8
    }
    elseif ($versionFileUpdated -and $commitCreated)
    {
        Invoke-Tool "git" @("restore", "--source=HEAD", "--", "version.json") "Unable to restore version.json after the release failed."
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
