QNote {VERSION}：便签与待办，随处同步。本次新增完整待办清单、OneDrive / WebDAV 云同步，以及可选的端到端加密。

## 更新内容

- **待办清单**：今日 / 重要 / 计划内视图、自定义清单、步骤、附件、到期提醒与重复任务，便签与待办可随时切换
- **多设备同步**：支持 OneDrive 与 HTTP / HTTPS WebDAV；自动同步便签、分类、待办清单、步骤及附件，两个页面均可立即同步
- **首次配对与冲突处理**：已有内容可选择合并或覆盖方向，发生编辑冲突时保留副本
- **可选端到端加密**：AES-256-GCM 保护云端同步内容，新设备在主窗口输入加密密码；关闭加密时显示转换进度，校验完成后才提示成功
- **主题与界面优化**：设置弹窗实时跟随主窗口主题，修复浅色主题下待办文字与图标显示问题，更新编辑器图标与标题样式
- **备份与恢复**：待办、步骤和附件一并纳入备份；同步首次覆盖、加密转换等操作前保留本地自动备份

## 升级与同步须知

- 升级前建议保留一份独立备份。新版本会自动升级本地数据库；请保留升级前的原始数据副本，以便需要时回退。
- 同步默认不额外加密云端内容；可在「设置 → 同步 → 加密存储」中启用。请为所有设备使用同一同步目标，并将设备统一升级到新版本后再启用加密。
- **请牢记加密密码**。QNote 无法找回密码；若密码遗失，且没有可用的已解锁设备或备份，云端加密数据将无法恢复。此功能不加密本机数据库。
- WebDAV 推荐 HTTPS。HTTP 不保护账号凭据；内容加密也不能替代 HTTPS。
- 同步会传播删除操作，不能替代备份。迁移到新电脑或 Windows 用户后，需要重新登录 / 配置同步并输入加密密码。

## Microsoft Store 商店版

商店版支持一键安装和通过 Microsoft Store 自动更新，由微软签名分发，无需手动导入证书。商店可用版本以商店页面显示为准。

[![从 Microsoft Store 获取 QNote](https://get.microsoft.com/images/zh-cn%20dark.svg)](https://apps.microsoft.com/detail/9NV57VJPTPCZ)

## 便携版下载

| 文件 | 说明 |
|---|---|
| `QNote_{VERSION}_win-x64_native-aot.zip` | **推荐**：NativeAOT 原生编译，体积更小、启动更快，零运行时依赖，解压即用 |

该变体为免安装绿色版：解压到任意**可写**目录，运行 `QNote.exe` 即可；「卸载」= 删除整个目录。

另外提供 `_native-aot_symbols.zip`（便携版）与 `_store-aot_symbols.zip`（Store 版）
两份独立调试符号包，普通使用无需下载。开发者可用其分析匹配版本的崩溃；
Store 上传包请取同次 Actions 的 `store-msix` artifact，详见
[调试符号说明](https://github.com/Mr-Second/QNote/blob/main/docs/debugging-symbols.md)。

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
