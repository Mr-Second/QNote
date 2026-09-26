# QNote MSIX 安装说明（侧载分发）

QNote 以自签名证书签名的 MSIX 包分发。首次安装前需要信任一次证书。

## 一键安装（推荐）

下载 Release 页的 **`install.ps1`**，与 `qnote.cer`、`QNote_<版本>_x64.msix` 放在同一目录，
右键 `install.ps1` →“使用 PowerShell 运行”。脚本会自动请求管理员权限、信任证书（一次性）
并安装 MSIX，全程无需手动操作。手动安装方式见下文。

## 产物

| 文件 | 说明 |
|---|---|
| `QNote_<版本>_x64.msix` | 安装包（仅 x64，自包含 .NET 运行时 + R2R） |
| `qnote.cer` | 签名证书公钥（用于信任，随包分发） |
| `install.ps1` | 一键安装脚本（可选，导证书 + 装包一步完成） |

签名证书：Subject `CN=QNote`，Thumbprint `8125C390B50D7D2D0B9AC1FD0C80030F78CEE9BA`，
有效期至 2031-09-25。签名带 DigiCert 时间戳，证书过期后已安装的包仍有效。

系统要求：Windows 10 19041+ / Windows 11，x64。需要 Windows App Runtime 2.3+
（Windows 11 通常已自带；缺失时安装包会提示，或从微软官网下载安装：
https://aka.ms/windowsappsdk/2.3/latest/windowsappruntimeinstall-x64.exe）。

## 第一步：信任证书（一次性，需要管理员）

方式 A — 图形界面：

1. 双击 `qnote.cer` → “安装证书”
2. 选择“本地计算机”（需管理员权限）→ 下一步
3. 选择“将所有的证书都放入下列存储” → 浏览 → “受信任的根证书颁发机构” → 完成

方式 B — PowerShell（管理员）：

```powershell
Import-Certificate -FilePath .\qnote.cer -CertStoreLocation Cert:\LocalMachine\Root
```

## 第二步：安装

双击 `QNote_<版本>_x64.msix`，或 PowerShell：

```powershell
Add-AppxPackage .\QNote_1.0.0.0_x64.msix
```

安装后从开始菜单启动 “QNote”。

## 升级

直接安装新版本 MSIX 覆盖即可（同一证书签名、同包名，版本号递增）。
“开机自启”开关状态由系统保留（任务管理器 → 启动应用）。

> 数据位置：MSIX 安装版的笔记数据在系统虚拟化目录
> `%LOCALAPPDATA%\Packages\71E74B05-BE20-45DC-B948-E90C0A09B4F8_t0w8ygyw6ak7r\LocalCache\Roaming\QNote\QNote\`，
> **卸载会删除该目录**——卸载前请先用应用内的备份功能导出 `.qns`。升级覆盖安装不影响数据。

> 注意：如果机器上装过用旧开发证书（`CN=AppPublisher`）签名的开发版 QNote，
> 两者包标识不同，不能互相覆盖升级。请先卸载旧版：
> `Get-AppxPackage -Name 71E74B05-BE20-45DC-B948-E90C0A09B4F8 | Remove-AppxPackage`

## 卸载

设置 → 应用 → QNote → 卸载，或：

```powershell
Get-AppxPackage -Name 71E74B05-BE20-45DC-B948-E90C0A09B4F8 | Remove-AppxPackage
```

卸载会清除开机自启注册（由包管理，无残留），**同时删除虚拟化目录中的笔记数据**
（见上方“数据位置”提醒）。
