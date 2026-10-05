[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$TaskDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Message,

    [switch]$Wip
)

. (Join-Path $PSScriptRoot "Common.ps1")

$config = Get-RepoSafetyConfig
$taskDirectory = Get-NormalizedPath $TaskDirectory
$state = Read-TaskState $taskDirectory
$repositories = @(
    [pscustomobject]@{ Name = "Unity"; Worktree = [string]$state.unityWorktree },
    [pscustomobject]@{ Name = "Python"; Worktree = [string]$state.pythonWorktree }
)

foreach ($entry in $repositories) {
    $branch = @(Invoke-Git -Repository $entry.Worktree -Arguments @(
        "symbolic-ref", "--short", "HEAD"
    ))[0].Trim()
    if ($branch -ne [string]$state.branch) {
        throw "$($entry.Name) worktree is on '$branch', expected '$($state.branch)'."
    }
    Assert-SafeRepositoryFiles $entry.Worktree
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @("diff", "--check"))
}

if (-not $Wip) {
    $logDirectory = Join-Path $taskDirectory "validation"
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

    $unityLog = Join-Path $logDirectory "unity.log"
    $unityResults = Join-Path $logDirectory "unity-editmode-results.xml"
    $unityArgs = @(
        "-batchmode",
        "-nographics",
        "-projectPath", ('"' + [string]$state.unityWorktree + '"'),
        "-executeMethod", "RepoSafetyEditModeValidation.Run",
        "-quit",
        "-testResults", ('"' + $unityResults + '"'),
        "-logFile", ('"' + $unityLog + '"')
    )
    $unityProcess = Start-Process -FilePath $config.unityExe -ArgumentList $unityArgs -Wait -PassThru -WindowStyle Hidden
    if ($unityProcess.ExitCode -ne 0) {
        throw "Unity validation failed with exit code $($unityProcess.ExitCode). See $unityLog"
    }
    if (-not (Test-Path -LiteralPath $unityResults -PathType Leaf)) {
        throw "Unity validation did not create a test result file. See $unityLog"
    }
    [xml]$unityTestRun = Get-Content -Raw -LiteralPath $unityResults
    if ([string]$unityTestRun.'test-run'.result -ne "Passed") {
        throw "Unity EditMode tests did not pass. See $unityResults"
    }

    & $config.pythonExe -m compileall -q ([string]$state.pythonWorktree)
    if ($LASTEXITCODE -ne 0) {
        throw "Python compileall validation failed."
    }
    Push-Location ([string]$state.pythonWorktree)
    try {
        & $config.pythonExe -c "import pose_estimation_dual, mocap_app, calibration_gui"
        if ($LASTEXITCODE -ne 0) {
            throw "Python import smoke validation failed."
        }
    }
    finally {
        Pop-Location
    }
    $testsPath = Join-Path ([string]$state.pythonWorktree) "tests"
    if (Test-Path -LiteralPath $testsPath -PathType Container) {
        & $config.pythonExe -c "import pytest"
        if ($LASTEXITCODE -ne 0) {
            throw "Python tests exist but pytest is not installed. Install requirements-dev.txt."
        }
        & $config.pythonExe -m pytest -q $testsPath
        if ($LASTEXITCODE -ne 0) {
            throw "Python pytest validation failed."
        }
    }
}

$commitPrefix = if ($Wip) { "wip" } else { "checkpoint" }
foreach ($entry in $repositories) {
    Assert-SafeRepositoryFiles $entry.Worktree
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @("add", "-A"))
    & git -C $entry.Worktree diff --cached --quiet
    if ($LASTEXITCODE -eq 1) {
        $commitMessage = $commitPrefix + ": " + $Message
        [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
            "commit", "-m", $commitMessage
        ))
    }
    elseif ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect staged changes in $($entry.Name)."
    }
}

if ($Wip) {
    foreach ($entry in $repositories) {
        [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
            "push", "origin", [string]$state.branch
        ))
    }
    Write-Host "WIP commits pushed. No stable checkpoint tag was created."
    exit 0
}

$number = ([int]$state.nextCheckpoint).ToString("00")
$checkpoint = "checkpoint/$($state.taskId)-$number"

foreach ($entry in $repositories) {
    $head = @(Invoke-Git -Repository $entry.Worktree -Arguments @("rev-parse", "HEAD"))[0].Trim()
    & git -C $entry.Worktree show-ref --verify --quiet "refs/tags/$checkpoint"
    if ($LASTEXITCODE -eq 0) {
        $tagHead = @(Invoke-Git -Repository $entry.Worktree -Arguments @(
            "rev-list", "-n", "1", $checkpoint
        ))[0].Trim()
        if ($tagHead -ne $head) {
            throw "Existing checkpoint tag points to another commit in $($entry.Name): $checkpoint"
        }
    }
    else {
        [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
            "tag", "-a", $checkpoint, $head, "-m", "Paired stable checkpoint $checkpoint"
        ))
    }
}

foreach ($entry in $repositories) {
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "push", "origin", [string]$state.branch
    ))
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "push", "origin", "refs/tags/$checkpoint"
    ))
}

$state.nextCheckpoint = [int]$state.nextCheckpoint + 1
$state.lastCheckpoint = $checkpoint
$state.lastCheckpointAt = (Get-Date).ToString("o")
Write-TaskState -TaskDirectory $taskDirectory -State $state
Write-Host "Stable paired checkpoint pushed: $checkpoint"
