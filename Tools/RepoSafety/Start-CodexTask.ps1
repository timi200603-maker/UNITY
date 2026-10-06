[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Task
)

. (Join-Path $PSScriptRoot "Common.ps1")

$config = Get-RepoSafetyConfig
$slug = Get-SafeSlug $Task
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$taskId = "$stamp-$slug"
$branch = "codex/$taskId"
$baselineTag = "checkpoint/$taskId-00-baseline"
$taskDirectory = Join-Path $config.worktreeRoot $taskId
$unityWorktree = Join-Path $taskDirectory "unity"
$pythonWorktree = Join-Path $taskDirectory "python"

if (Test-Path -LiteralPath $taskDirectory) {
    throw "Task directory already exists: $taskDirectory"
}

$repositories = @(
    [pscustomobject]@{ Name = "Unity"; Repository = $config.unityRepo; Worktree = $unityWorktree },
    [pscustomobject]@{ Name = "Python"; Repository = $config.pythonRepo; Worktree = $pythonWorktree }
)

foreach ($entry in $repositories) {
    Assert-CleanRepository $entry.Repository
    Assert-SafeRepositoryFiles $entry.Repository
    Assert-OriginMain $entry.Repository

    & git -C $entry.Repository show-ref --verify --quiet "refs/heads/$branch"
    if ($LASTEXITCODE -eq 0) {
        throw "Local branch already exists in $($entry.Name): $branch"
    }
    & git -C $entry.Repository ls-remote --exit-code --heads origin $branch *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Remote branch already exists in $($entry.Name): $branch"
    }
    & git -C $entry.Repository show-ref --verify --quiet "refs/tags/$baselineTag"
    if ($LASTEXITCODE -eq 0) {
        throw "Baseline tag already exists in $($entry.Name): $baselineTag"
    }
}

New-Item -ItemType Directory -Force -Path $taskDirectory | Out-Null

foreach ($entry in $repositories) {
    [void](Invoke-Git -Repository $entry.Repository -Arguments @(
        "worktree", "add", "-b", $branch, $entry.Worktree, "refs/remotes/origin/main"
    ))
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "push", "--set-upstream", "origin", $branch
    ))
}

foreach ($entry in $repositories) {
    $head = @(Invoke-Git -Repository $entry.Worktree -Arguments @("rev-parse", "HEAD"))[0].Trim()
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "tag", "-a", $baselineTag, $head, "-m", "Baseline for paired task $taskId"
    ))
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "push", "origin", "refs/tags/$baselineTag"
    ))
}

$state = [ordered]@{
    taskId = $taskId
    branch = $branch
    baselineTag = $baselineTag
    nextCheckpoint = 1
    unityWorktree = $unityWorktree
    pythonWorktree = $pythonWorktree
    createdAt = (Get-Date).ToString("o")
}
Write-TaskState -TaskDirectory $taskDirectory -State $state

Write-Host "Paired Codex task created."
Write-Host "Task directory: $taskDirectory"
Write-Host "Unity worktree: $unityWorktree"
Write-Host "Python worktree: $pythonWorktree"
Write-Host "Branch: $branch"
Write-Host "Baseline: $baselineTag"
