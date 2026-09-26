#Requires -Version 7.0
<#
.SYNOPSIS
    Cold-start and steady-state memory measurement for the installed QNote MSIX package.

.DESCRIPTION
    Permanent development tool (perf-size-optimization task, R1). Repeats N cold
    starts of the INSTALLED package (loose dev registrations and `dotnet run` builds
    are NOT the subject — the packaged Release binary is):

      1. Kill every running QNote.exe (the app close-to-trays and never exits on
         its own; only the tray Quit menu or Stop-Process ends it).
      2. Launch via `explorer.exe shell:AppsFolder\<PFN>!App`.
      3. Poll `Get-Process QNote`: record process-spawn latency, window-visible
         latency (MainWindowHandle becomes non-zero), then sample
         WorkingSet64 / PrivateMemorySize64 every second until the working set is
         steady (last 5 samples within 3%, after >= 10 s of sampling).

    Package auto-detection: the QNote package NAME is a GUID (template default), so
    `Get-AppxPackage *QNote*` matches nothing. The script instead scans every package
    for a `QNote.exe` under its InstallLocation and picks the highest version.
    Override with -PackageFamilyName when several QNote packages coexist (e.g. a
    loose `winapp run` dev registration next to the installed MSIX).

    Prints a per-run table plus medians; with -OutFile it also appends a markdown
    section (used to build research/baseline.md).

.EXAMPLE
    pwsh scripts/Measure-QNotePerf.ps1 -Runs 5 -OutFile .trellis/tasks/09-26-perf-size-optimization/research/baseline.md
#>
[CmdletBinding()]
param(
    [int]$Runs = 5,
    [string]$PackageFamilyName,
    [string]$AppId = 'App',
    [int]$ProcessTimeoutSec = 30,
    [int]$WindowTimeoutSec = 30,
    [int]$SteadyTimeoutSec = 90,
    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-QNotePackage {
    if ($PackageFamilyName) {
        $pkg = Get-AppxPackage | Where-Object PackageFamilyName -EQ $PackageFamilyName
        if (-not $pkg) { throw "No installed package with PackageFamilyName '$PackageFamilyName'." }
        return $pkg
    }
    $candidates = Get-AppxPackage | Where-Object {
        $_.InstallLocation -and (Test-Path -LiteralPath (Join-Path $_.InstallLocation 'QNote.exe'))
    }
    if (-not $candidates) { throw 'No installed package with a QNote.exe under its InstallLocation was found.' }
    return ($candidates | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1)
}

function Stop-QNote {
    $procs = Get-Process -Name QNote -ErrorAction SilentlyContinue
    if (-not $procs) { return }
    $procs | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Name QNote -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 200
    }
    throw 'QNote.exe did not exit within 10 s of Stop-Process.'
}

function Measure-OneRun {
    Stop-QNote
    Start-Sleep -Seconds 2 # let the OS settle between cold starts

    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Process explorer.exe "shell:AppsFolder\$($script:Package.PackageFamilyName)!$AppId"

    # --- wait for the process ---------------------------------------------
    $proc = $null
    while ($clock.Elapsed.TotalSeconds -lt $ProcessTimeoutSec) {
        $proc = Get-Process -Name QNote -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $proc) { throw "QNote.exe did not appear within $ProcessTimeoutSec s of launch." }
    $processMs = [int]$clock.ElapsedMilliseconds

    # --- wait for a visible main window (StartMinimized launches never show one) ---
    $windowMs = $null
    while ($clock.Elapsed.TotalSeconds -lt $WindowTimeoutSec) {
        $proc = Get-Process -Name QNote -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { throw 'QNote.exe exited during startup.' }
        if ($proc.MainWindowHandle -ne 0) { $windowMs = [int]$clock.ElapsedMilliseconds; break }
        Start-Sleep -Milliseconds 200
    }

    # --- sample memory until steady ---------------------------------------
    $samples = [System.Collections.Generic.List[long]]::new()
    $steadyReached = $false
    while ($clock.Elapsed.TotalSeconds -lt $SteadyTimeoutSec) {
        $proc = Get-Process -Name QNote -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { throw 'QNote.exe exited while sampling.' }
        $samples.Add($proc.WorkingSet64)
        $privateBytes = $proc.PrivateMemorySize64
        if ($samples.Count -ge 10) {
            $tail = $samples.GetRange($samples.Count - 5, 5)
            $min = ($tail | Measure-Object -Minimum).Minimum
            $max = ($tail | Measure-Object -Maximum).Maximum
            if ($max -gt 0 -and ($max - $min) / $max -lt 0.03) { $steadyReached = $true; break }
        }
        Start-Sleep -Seconds 1
    }

    return [pscustomobject]@{
        ProcessMs      = $processMs
        WindowMs       = $windowMs          # $null = no window (StartMinimized)
        WorkingSetMB   = [math]::Round($samples[$samples.Count - 1] / 1MB, 1)
        PrivateMB      = [math]::Round($privateBytes / 1MB, 1)
        SteadyAfterSec = [int]$clock.Elapsed.TotalSeconds
        SteadyReached  = $steadyReached
    }
}

function Get-Median([double[]]$values) {
    if (-not $values) { return $null }
    $sorted = $values | Sort-Object
    $mid = [int]($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return [math]::Round(($sorted[$mid - 1] + $sorted[$mid]) / 2, 1)
}

# ---------------------------------------------------------------------------
$script:Package = Find-QNotePackage
Write-Host "Package: $($script:Package.PackageFullName)"
Write-Host "Runs:    $Runs"
Write-Host ''

$results = for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "Run $i/$Runs ..." -NoNewline
    $r = Measure-OneRun
    Write-Host " process=$($r.ProcessMs)ms window=$($r.WindowMs)ms ws=$($r.WorkingSetMB)MB private=$($r.PrivateMB)MB steady@$($r.SteadyAfterSec)s"
    $r
}

Stop-QNote # leave no instance behind

$median = [pscustomobject]@{
    ProcessMs    = Get-Median ($results | ForEach-Object { $_.ProcessMs })
    WindowMs     = Get-Median ($results | Where-Object WindowMs | ForEach-Object { $_.WindowMs })
    WorkingSetMB = Get-Median ($results | ForEach-Object { $_.WorkingSetMB })
    PrivateMB    = Get-Median ($results | ForEach-Object { $_.PrivateMB })
}

Write-Host ''
Write-Host ('Median: process={0}ms window={1}ms ws={2}MB private={3}MB' -f `
    $median.ProcessMs, $median.WindowMs, $median.WorkingSetMB, $median.PrivateMB)

if ($OutFile) {
    $lines = @(
        ''
        "## Measured: $(Get-Date -Format 'yyyy-MM-dd HH:mm') — $($script:Package.Version)"
        ''
        "| Run | Process spawn (ms) | Window visible (ms) | Steady WorkingSet (MB) | Steady PrivateMemory (MB) | Steady after (s) |"
        "|-----|--------------------|---------------------|------------------------|---------------------------|------------------|"
    )
    $n = 0
    foreach ($r in $results) {
        $n++
        $lines += "| $n | $($r.ProcessMs) | $(if ($null -eq $r.WindowMs) { 'n/a' } else { $r.WindowMs }) | $($r.WorkingSetMB) | $($r.PrivateMB) | $($r.SteadyAfterSec) |"
    }
    $lines += "| **Median** | **$($median.ProcessMs)** | **$($median.WindowMs)** | **$($median.WorkingSetMB)** | **$($median.PrivateMB)** | |"
    $lines += ''
    Add-Content -LiteralPath $OutFile -Value $lines -Encoding utf8
    Write-Host "Appended results to $OutFile"
}
