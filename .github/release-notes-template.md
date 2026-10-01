QNote {VERSION} 起正式提供**便携版（绿色软件）**：GitHub Release 从本版开始分发免安装 zip；安装版（MSIX）仅通过 [Microsoft Store](https://apps.microsoft.com/detail/9NV57VJPTPCZ) 分发，两渠道可并存。

## 下载哪个变体？

| 文件 | 说明 |
|---|---|
| `QNote_{VERSION}_win-x64_native-aot.zip` | **推荐**：NativeAOT 原生编译，体积更小、启动更快，零运行时依赖，解压即用 |
| `QNote_{VERSION}_win-x64_framework-dependent.zip` | 需先安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（x64）；自 AOT 版上线后体积优势已无，仅按需保留 |

两个变体内容完全相同，均为免安装绿色版：解压到任意**可写**目录，运行 `QNote.exe` 即可；「卸载」= 删除整个目录。

## 数据位置

- 笔记数据保存在 **`QNote.exe` 同级的 `data\` 目录**，随目录整体移动、复制即迁移。
- 若 exe 所在目录不可写（例如直接解压到了 `Program Files`），应用会自动回退到 `%APPDATA%\QNote\QNote\`，并在首次启动时弹窗告知。
- **Store 版与便携版数据互相独立**：同一台机器可以同时安装两个渠道，互不干扰。切换渠道时请用应用内「设置 → 备份 / 恢复」迁移数据。

## 从旧版自签 MSIX 迁移（v1.1.0 及更早的 GitHub 用户）

GitHub 的自签 MSIX 渠道（`Installer.zip` / `install.ps1` / 证书信任）已停止发布。**卸载旧版会一并删除 MSIX 虚拟化目录中的笔记数据**：卸载前请先在旧版中用「备份」导出 `.qns` 归档，再于新渠道（Store 版或便携版）中「恢复」。详见 [docs/msix-install.md](https://github.com/Mr-Second/QNote/blob/main/docs/msix-install.md)。

## 全局热键说明

全局热键同一时刻只能注册到一个 QNote 实例。Store 版与便携版（或不同目录的多个便携版）**同时运行时，后启动的实例热键注册会失败**，属预期行为——关闭先启动的实例后重启后者即可恢复。

## 完整性校验

便携版 zip **不做代码签名**（见 [docs/code-signing-policy.md](https://github.com/Mr-Second/QNote/blob/main/docs/code-signing-policy.md)）。下载后请用随 Release 发布的 `SHA256SUMS.txt`（或任意 zip 的 `.sha256` 边车文件）校验：

```powershell
Get-FileHash .\QNote_{VERSION}_win-x64_native-aot.zip -Algorithm SHA256
```
