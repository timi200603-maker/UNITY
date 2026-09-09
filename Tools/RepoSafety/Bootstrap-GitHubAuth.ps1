# Bootstrap GitHub CLI authentication for this project.
#
# Purpose
#   gh cannot push / open PRs until it has an authenticated host entry. Normally
#   `gh auth login` writes %APPDATA%\GitHub CLI\hosts.yml. If that file is
#   missing but a valid token is still available (e.g. cached by `gh auth token`
#   or via the GH_TOKEN / GITHUB_TOKEN env vars), this script repairs the config
#   so subsequent `gh` commands work without manual GH_TOKEN injection.
#
# Usage
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\RepoSafety\Bootstrap-GitHubAuth.ps1
#
# Behavior
#   1. Abort if gh is not installed.
#   2. If already authenticated -> report account + token scopes, exit 0.
#   3. Else resolve a token (GH_TOKEN / GITHUB_TOKEN env, else `gh auth token`).
#   4. With a token, resolve the account, write hosts.yml, verify, exit 0.
#   5. Without a token -> print the interactive `gh auth login` steps, exit 1.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-GhConfigDir {
    $appData = Join-Path $env:APPDATA "GitHub CLI"
    $xdg = Join-Path $HOME ".config\gh"
    if (Test-Path -LiteralPath $appData) { return $appData }
    if (Test-Path -LiteralPath $xdg) { return $xdg }
    # gh's default on Windows is %AppData%\GitHub CLI; create it if absent.
    New-Item -ItemType Directory -Path $appData -Force | Out-Null
    return $appData
}

function Resolve-GhToken {
    if ($env:GH_TOKEN) { return $env:GH_TOKEN }
    if ($env:GITHUB_TOKEN) { return $env:GITHUB_TOKEN }
    try {
        $t = (& gh auth token 2>$null | Select-Object -First 1).Trim()
        if ($t) { return $t }
    } catch { }
    return $null
}

Write-Host "== GitHub CLI auth bootstrap =="

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "gh CLI not found. Install it from https://cli.github.com/ and re-run."
    exit 2
}

# Already authenticated?
$authOut = (& gh auth status 2>&1 | Out-String)
if ($LASTEXITCODE -eq 0) {
    Write-Host "Already authenticated:" -ForegroundColor Green
    Write-Host $authOut
    exit 0
}

$token = Resolve-GhToken
if (-not $token) {
    Write-Host "No GitHub token is available to write hosts.yml." -ForegroundColor Yellow
    Write-Host @"

To authenticate interactively, run:

    gh auth login --hostname github.com --git-protocol https --web

Complete the browser prompt, then re-run this script. If you have a token
already, export it first:   `$env:GH_TOKEN = "<token>"
"@
    exit 1
}

# Use the token to identify the account.
$env:GH_TOKEN = $token
$login = (& gh api user -q .login 2>$null | Select-Object -First 1).Trim()
if (-not $login) {
    Write-Error "Token did not resolve to an account (invalid or expired?)."
    Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue
    exit 1
}

$dir = Get-GhConfigDir
$hosts = Join-Path $dir "hosts.yml"
$lines = @(
    "github.com:"
    "    user: $login"
    "    oauth_token: $token"
    "    git_protocol: https"
    "    users:"
    "        ${login}:"
    "            oauth_token: $token"
)
[System.IO.File]::WriteAllLines($hosts, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote hosts.yml -> $hosts (account $login)."

Remove-Item Env:\GH_TOKEN -ErrorAction SilentlyContinue

# Verify without GH_TOKEN injection.
$verify = (& gh auth status 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    Write-Error "hosts.yml was written but 'gh auth status' still failed."
    Write-Host $verify
    exit 1
}
Write-Host "Verified (no GH_TOKEN injection needed):" -ForegroundColor Green
Write-Host $verify
exit 0
