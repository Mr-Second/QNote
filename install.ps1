<#
  QNote one-click installer (interim, self-signed distribution).

  What it does:
    1. Elevates to Administrator (required to trust the certificate).
    2. Imports qnote.cer into the LocalMachine Trusted Root store — a one-time
       step that makes Windows accept packages signed with the QNote cert.
    3. Installs the QNote MSIX package next to this script.

  Usage: place install.ps1, qnote.cer and QNote_*_x64.msix in the same folder,
  right-click install.ps1 -> "Run with PowerShell". Both files are published
  together on the GitHub Releases page.

  This script exists only until QNote ships with publicly-trusted signing
  (SignPath / Microsoft Store); then plain double-click install works and
  this script is unnecessary.
#>
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

# --- Self-elevate -----------------------------------------------------------
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting administrator privileges... / 正在请求管理员权限...'
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit
}

try {
    $dir = Split-Path -Parent $PSCommandPath
    $cer  = Join-Path $dir 'qnote.cer'
    $msix = Get-ChildItem $dir -Filter '*.msix' | Select-Object -First 1

    if (-not (Test-Path $cer))  { throw "qnote.cer not found next to the script. / 未在脚本旁找到 qnote.cer" }
    if (-not $msix)             { throw "No .msix package found next to the script. / 未在脚本旁找到 .msix 安装包" }

    Write-Host "[1/2] Trusting QNote certificate (one-time)... / 信任 QNote 证书(一次性)..."
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null

    Write-Host "[2/2] Installing $($msix.Name)... / 正在安装..."
    Add-AppxPackage $msix.FullName

    Write-Host ''
    Write-Host 'Done! QNote is installed — launch it from the Start menu.' -ForegroundColor Green
    Write-Host '完成!QNote 已安装,从开始菜单启动即可。' -ForegroundColor Green
}
catch {
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '安装失败,可将本窗口信息截图反馈到 GitHub Issues。' -ForegroundColor Red
}
finally {
    Write-Host ''
    Read-Host 'Press Enter to close / 按回车关闭'
}
