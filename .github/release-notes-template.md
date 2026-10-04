QNote {VERSION} 包含两项新功能与两项修复：启动时更新提醒、「关于 QNote」页；修复 Store 版中文界面不生效的问题，并让最小化 / 贴边隐藏后也能自动释放内存。

## 更新内容

- **新增**：启动时检查更新——发现新版本弹窗提示（版本号 + 变更内容 + 「不再提示」勾选），便携版一键打开 GitHub 下载页，Store 版直达商店更新页；可在设置中关闭
- **新增**：「关于 QNote」页——版本信息、GitHub 仓库、Store 评分、隐私政策与开源许可入口，从设置的「关于」区块打开
- **修复**：Store 版切换语言不生效的问题（界面始终显示英文，「跟随系统」与手动切换中文现已正常；便携版不受影响）
- **优化**：最小化、贴边隐藏与关闭到托盘后统一自动释放内存（后台占用从约 60MB 降至约 3MB，唤出时无可感知卡顿）

## 下载哪个变体？

| 文件 | 说明 |
|---|---|
| `QNote_{VERSION}_win-x64_native-aot.zip` | **推荐**：NativeAOT 原生编译，体积更小、启动更快，零运行时依赖，解压即用 |

该变体为免安装绿色版：解压到任意**可写**目录，运行 `QNote.exe` 即可；「卸载」= 删除整个目录。

## 从旧便携版升级

笔记数据全部在 `QNote.exe` 同级的 **`data\` 目录**。升级时**解压覆盖到原目录**（保留 `data\`），或把旧目录的 `data\` 整个拷到新目录即可——不拷则视为全新数据。目录不可写（如 `Program Files`）时应用自动回退 `%APPDATA%\QNote\QNote\`，首次启动会弹窗告知。

**Store 版与便携版数据互相独立**：同一台机器可同时安装两个渠道，互不干扰。切换渠道请用应用内「设置 → 备份 / 恢复」迁移数据。

## 从旧版自签 MSIX 迁移（v1.1.0 及更早的 GitHub 用户）

GitHub 的自签 MSIX 渠道（`Installer.zip` / `install.ps1` / 证书信任）已停止发布。**卸载旧版会一并删除 MSIX 虚拟化目录中的笔记数据**：卸载前请先在旧版中用「备份」导出 `.qns` 归档，再于新渠道（Store 版或便携版）中「恢复」。详见 [docs/msix-install.md](https://github.com/Mr-Second/QNote/blob/main/docs/msix-install.md)。

## 全局热键说明

全局热键同一时刻只能注册到一个 QNote 实例。Store 版与便携版（或不同目录的多个便携版）**同时运行时，后启动的实例热键注册会失败**，属预期行为——关闭先启动的实例后重启后者即可恢复。

## 完整性校验

便携版 zip **不做代码签名**（见 [docs/code-signing-policy.md](https://github.com/Mr-Second/QNote/blob/main/docs/code-signing-policy.md)）。下载后请用随 Release 发布的 `SHA256SUMS.txt`（或任意 zip 的 `.sha256` 边车文件）校验：

```powershell
Get-FileHash .\QNote_{VERSION}_win-x64_native-aot.zip -Algorithm SHA256
```
