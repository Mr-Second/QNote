<#
.SYNOPSIS
    Sync master changes to dev branch via worktree (GitHub public mirror).

.DESCRIPTION
    QNote dual-remote strategy:
      - origin (gitea):  master branch, full content (incl. AI dirs)
      - github (GitHub): dev branch, source only (AI dirs stripped)

    Uses git worktree at ../QNote-github to avoid untracked conflicts.
    Safe to run from master in main worktree. Original branch unchanged.

.PARAMETER WorktreePath
    Path to dev worktree. Default: '../QNote-github'.

.PARAMETER Push
    Push dev to github remote after sync. Default: true.

.PARAMETER DryRun
    Print actions without modifying git state.

.EXAMPLE
    .\scripts\sync-github.ps1
    .\scripts\sync-github.ps1 -DryRun
    .\scripts\sync-github.ps1 -Push:$false
#>

[CmdletBinding()]
param(
    [string]$WorktreePath = '../QNote-github',
    [bool]$Push = $true,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$AiPaths = @(
    '.trellis', '.opencode', '.agents', '.codex', '.claude',
    'AGENTS.md', '.cursorrules', '.cursor', 'CLAUDE.md'
)

function Run([string]$Cmd, [switch]$CheckExit) {
    Write-Host "  $Cmd" -ForegroundColor DarkGray
    if ($DryRun) { return '' }
    $output = Invoke-Expression "$Cmd 2>&1"
    if ($CheckExit -and $LASTEXITCODE -ne 0) {
        Write-Error "$Cmd failed (exit $LASTEXITCODE):`n$output"
        exit 1
    }
    return ($output -join "`n")
}

$repoRoot = (& git rev-parse --show-toplevel).Trim()
$wtAbs = if ([System.IO.Path]::IsPathRooted($WorktreePath)) {
    $WorktreePath
} else {
    [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($repoRoot, $WorktreePath))
}

Write-Host "Repo root: $repoRoot" -ForegroundColor Cyan
Write-Host "Worktree:  $wtAbs (dev branch)" -ForegroundColor Cyan
Write-Host "DryRun:    $DryRun" -ForegroundColor Cyan
Write-Host ""

# 1. Ensure worktree exists
Write-Host "[1/4] Ensuring worktree..." -ForegroundColor Yellow
# git worktree list uses forward slashes; normalize both to forward slash for compare
$wtNorm = $wtAbs -replace '\\', '/'
$wtList = (git worktree list --porcelain) -join "`n"
$exists = $wtList -match ([regex]::Escape($wtNorm))
if (-not $exists) {
    Run "git worktree add `"$wtAbs`" -B dev" -CheckExit | Out-Null
    Write-Host "  Created worktree." -ForegroundColor DarkGray
} else {
    Write-Host "  Already exists." -ForegroundColor DarkGray
}

# 2. Merge master into dev
Write-Host "[2/4] Merging master into dev..." -ForegroundColor Yellow
$out = Run "git -C `"$wtAbs`" merge master --no-edit" -CheckExit
Write-Host $out -ForegroundColor DarkGray

# 3. Strip AI dirs from dev index (they re-enter via merge from master)
Write-Host "[3/4] Stripping AI dirs..." -ForegroundColor Yellow
$stripped = @()
foreach ($p in $AiPaths) {
    $tracked = git -C $wtAbs ls-files --error-unmatch $p 2>$null
    if ($LASTEXITCODE -eq 0 -and $tracked) {
        Run "git -C `"$wtAbs`" rm -r --cached `"$p`"" | Out-Null
        $stripped += $p
        Write-Host "    removed: $p" -ForegroundColor DarkGray
    }
}

if ($stripped) {
    $msg = "chore(sync): strip AI dirs from GitHub mirror`n`nRemoved: $($stripped -join ', ')"
    Run "git -C `"$wtAbs`" commit -m `"$msg`"" -CheckExit | Out-Null
} else {
    Write-Host "  (no AI dirs to strip)" -ForegroundColor DarkGray
}

# 4. Push
if ($Push) {
    Write-Host "[4/4] Pushing dev to github..." -ForegroundColor Yellow
    Run "git -C `"$wtAbs`" push github dev" -CheckExit | Out-Null
} else {
    Write-Host "[4/4] Skip push" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Done. dev synced." -ForegroundColor Green
Write-Host ""
Write-Host "To cut release:" -ForegroundColor Cyan
Write-Host "  cd $wtAbs" -ForegroundColor Cyan
Write-Host "  git tag v0.X.Y" -ForegroundColor Cyan
Write-Host "  git push github v0.X.Y" -ForegroundColor Cyan
