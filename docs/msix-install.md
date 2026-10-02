# QNote MSIX 安装说明

> **渠道变更（v1.2.0 起）**：MSIX 安装版改经 **Microsoft Store** 分发；GitHub
> Releases 改为提供**便携版（绿色软件）zip**。本页保留原路径作为旧链接的落点，
> 并说明旧渠道用户的迁移方式。

## 安装（Microsoft Store）

打开 [Microsoft Store — QNote](https://apps.microsoft.com/detail/9NV57VJPTPCZ)，
或在商店内搜索「QNote」。Store 版由微软受信签名发布，无需任何证书信任步骤，
安装后自动更新。

系统要求：Windows 10 19041+ / Windows 11，x64。

## 便携版（GitHub）

见 [README](https://github.com/Mr-Second/QNote#install) 与
[Releases](https://github.com/Mr-Second/QNote/releases)：便携版（NativeAOT
编译，零依赖）解压即用；笔记数据保存在 `QNote.exe` 同级的 `data\` 目录，随文件夹
整体移动。下载校验用随 Release 发布的 `SHA256SUMS.txt`。

## 历史说明（自签 MSIX 渠道，≤ v1.1.0）

v1.1.0 及更早版本，GitHub Release 曾以自签证书（`CN=QNote`）签名的 MSIX 包
（`Installer.zip` + `install.ps1` + `qnote.cer` 证书信任仪式）分发。该渠道已
停止：不再需要任何证书信任操作，后续 Release 也不再提供 `.msix` / `.cer`。

**老用户迁移**：自签版与 Store 版包标识不同，无法覆盖升级。**卸载旧版会一并
删除 MSIX 虚拟化目录中的笔记数据**——卸载前请先在旧版「设置 → 备份」导出
`.qns` 归档，再安装 Store 版或便携版，用「设置 → 恢复」导回：

```powershell
Get-AppxPackage -Name 71E74B05-BE20-45DC-B948-E90C0A09B4F8 | Remove-AppxPackage
```

Store 版与便携版的数据目录互相独立，可在同一台机器并存；从旧版迁出的数据由
备份/恢复功能搬运，卸载/安装流程不再触碰数据。
