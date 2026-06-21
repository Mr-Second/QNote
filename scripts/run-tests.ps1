<#
.SYNOPSIS
    构建、运行并汇总 QNote 单元测试结果。
.DESCRIPTION
    这个脚本封装了 QNote 测试运行的完整流程，解决 QtTest 在 Windows/PowerShell 下的输出捕获痛点。
    核心问题：QNoteTest 的 main 用 4 次 QTest::qExec 运行 4 个测试类，普通管道/重定向拿不到 stdout，
    且 -o file,txt 会被最后一个类覆盖。C++ main 已改为支持 per-class 输出文件。
    本脚本会：
      1. 构建 QNoteTest（可选 --skip-build 跳过）
      2. 设置 PATH（Qt bin 目录）让 DLL 可加载
      3. 运行 QNoteTest -o <result.txt,txt>，触发 per-class 输出
      4. 收集所有 per-class 文件，汇总打印通过/失败计数
      5. 非 0 退出码表示有测试失败
.PARAMETER QtDir
    Qt 安装目录（包含 bin/msvc2022_64）。默认 D:\Qt\6.9.3\msvc2022_64
.PARAMETER Config
    构建配置。默认 release
.PARAMETER SkipBuild
    跳过构建步骤，直接运行已有的 QNoteTest.exe
.PARAMETER KeepResults
    保留 per-class 结果文件，不清理（默认运行后删除）
.EXAMPLE
    .\scripts\run-tests.ps1
    构建并运行所有测试，打印汇总
.EXAMPLE
    .\scripts\run-tests.ps1 -SkipBuild
    不重新构建，直接运行已编译的 QNoteTest.exe
.EXAMPLE
    .\scripts\run-tests.ps1 -KeepResults
    运行并保留所有 per-class .txt 结果文件供后续查看
#>
[CmdletBinding()]
param(
    [string]$QtDir = "D:\Qt\6.9.3\msvc2022_64",
    [ValidateSet("debug", "release")]
    [string]$Config = "release",
    [switch]$SkipBuild,
    [switch]$KeepResults
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "=== QNote Test Runner ===" -ForegroundColor Cyan
Write-Host "Repo: $RepoRoot"
Write-Host "QtDir: $QtDir"
Write-Host "Config: $Config"
Write-Host ""

# Step 1: 构建（可选）
if (-not $SkipBuild) {
    Write-Host "[1/4] Building QNoteTest ($Config)..." -ForegroundColor Yellow
    Push-Location $RepoRoot
    try {
        & xmake f -m $Config -y 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "xmake config failed (exit $LASTEXITCODE)" }
        & xmake build QNoteTest 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "xmake build failed (exit $LASTEXITCODE)" }
        Write-Host "      Build OK" -ForegroundColor Green
    }
    finally { Pop-Location }
} else {
    Write-Host "[1/4] Skipping build (-SkipBuild)" -ForegroundColor DarkGray
}
Write-Host ""

# Step 2: 定位 QNoteTest.exe
$exePath = Join-Path $RepoRoot "build\windows\x64\$Config\out\QNoteTest.exe"
if (-not (Test-Path $exePath)) {
    # 兜底：搜索 build 目录
    $found = Get-ChildItem -Path (Join-Path $RepoRoot "build") -Recurse -Filter "QNoteTest.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $exePath = $found.FullName }
    else { throw "QNoteTest.exe not found under build\. Run without -SkipBuild first." }
}
Write-Host "[2/4] Test executable: $exePath" -ForegroundColor Yellow
Write-Host ""

# Step 3: 设置环境 + 运行
$qtBin = Join-Path $QtDir "bin"
if (-not (Test-Path $qtBin)) { throw "Qt bin dir not found: $qtBin" }

$resultDir = Join-Path $env:TEMP "qnote_test_results"
$resultBase = Join-Path $resultDir "result"
if (Test-Path $resultDir) { Remove-Item $resultDir -Recurse -Force }
New-Item -ItemType Directory -Path $resultDir -Force | Out-Null

Write-Host "[3/4] Running tests..." -ForegroundColor Yellow
$env:PATH = "$qtBin;$env:PATH"

# C++ main 已支持 per-class 输出：传 -o result.txt,txt 会生成 result_<ClassName>.txt
& $exePath -o "$resultBase.txt,txt"
$testExitCode = $LASTEXITCODE
Write-Host "      Exit code: $testExitCode"
Write-Host ""

# Step 4: 汇总 per-class 结果
Write-Host "[4/4] Results summary" -ForegroundColor Yellow
$perClassFiles = Get-ChildItem -Path $resultDir -Filter "result_*.txt" -ErrorAction SilentlyContinue | Sort-Object Name

if (-not $perClassFiles) {
    Write-Host "      WARNING: No per-class result files found." -ForegroundColor Red
    Write-Host "      (C++ main may not have the per-class -o patch yet)"
    Write-Host ""
    Write-Host "      Falling back to exit code: $testExitCode"
    exit $testExitCode
}

$totalPassed = 0
$totalFailed = 0
$totalSkipped = 0
$failedClasses = @()

foreach ($f in $perClassFiles) {
    $content = Get-Content $f.FullName -Raw
    # 匹配 "Totals: 7 passed, 0 failed, 0 skipped, 0 blacklisted, 7887ms"
    $totalsLine = ($content -split "`n" | Where-Object { $_ -match "Totals:" } | Select-Object -First 1)
    if (-not $totalsLine) {
        Write-Host "      $($f.BaseName): <no Totals line>" -ForegroundColor Red
        continue
    }
    $passed = if ($totalsLine -match "(\d+) passed") { [int]$Matches[1] } else { 0 }
    $failed = if ($totalsLine -match "(\d+) failed") { [int]$Matches[1] } else { 0 }
    $skipped = if ($totalsLine -match "(\d+) skipped") { [int]$Matches[1] } else { 0 }

    $totalPassed += $passed
    $totalFailed += $failed
    $totalSkipped += $skipped

    $status = if ($failed -eq 0) { "PASS" } else { "FAIL" }
    $color = if ($failed -eq 0) { "Green" } else { "Red" }
    if ($failed -gt 0) { $failedClasses += $f.BaseName }
    Write-Host ("      {0,-40} {1}  {2} passed, {3} failed, {4} skipped" -f $f.BaseName, $status, $passed, $failed, $skipped) -ForegroundColor $color

    # 如果有失败，打印失败的测试函数名
    if ($failed -gt 0) {
        $failLines = $content -split "`n" | Where-Object { $_ -match "FAIL!" }
        foreach ($fl in $failLines) {
            Write-Host "        $fl" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host ("      TOTAL: {0} passed, {1} failed, {2} skipped" -f $totalPassed, $totalFailed, $totalSkipped) -ForegroundColor $(if ($totalFailed -eq 0) { "Green" } else { "Red" })

if ($KeepResults) {
    Write-Host ""
    Write-Host "Result files kept in: $resultDir" -ForegroundColor DarkGray
} else {
    Remove-Item $resultDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($testExitCode -ne 0) {
    Write-Host "RESULT: FAILED (exit $testExitCode)" -ForegroundColor Red
} else {
    Write-Host "RESULT: PASSED" -ForegroundColor Green
}
exit $testExitCode
