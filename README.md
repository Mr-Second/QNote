# QNote

**简体中文** | [English](README.en.md)

[![CI](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml)
[![Release](https://github.com/Mr-Second/QNote/actions/workflows/release.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/release.yml)

一款轻量的 Windows 便签与待办应用，基于 **WinUI 3** 构建，支持 **OneDrive / WebDAV 同步**与**可选端到端加密**。

QNote 是对早期 Qt/QML 版本的 C# / WinUI 3 从零重写，唯一的北极星指标是：**低内存占用**。
Qt 版空载约消耗 250 MB 内存；QNote 冷启动开窗约 **60 MB**，贴边隐藏 / 托盘常驻仅约 **3 MB**——足够精瘦，可以常驻后台一整天而无需惦记。

## 截图

| | |
|---|---|
| ![主界面——分类、便签列表与富文本编辑器](docs/screenshots/1-main.png) | ![即时全文搜索与关键词高亮](docs/screenshots/2-search.png) |
| ![设置面板](docs/screenshots/3-settings.png) | ![富文本便签](docs/screenshots/4-richtext.png) |
| ![待办清单——步骤、附件、到期日期与重要标记](docs/screenshots/5-todo.png) | ![OneDrive 与 WebDAV 同步设置](docs/screenshots/6-sync.png) |
| ![设置云端加密前的密码保管提醒](docs/screenshots/7-encryption-warning.png) | ![新设备同步时输入加密密码](docs/screenshots/8-encryption-unlock.png) |

新增截图使用演示数据。

## 功能

- **便签增删改查** + 富文本编辑器与格式工具栏（加粗 / 斜体 / 列表 / 颜色 / ……）
- 基于 SQLite FTS5 的**即时搜索**，带关键词高亮
- 侧边栏**分类**，支持拖拽排序
- 便签内**插图**（右键可另存 / 替换 / 备注替代文本）
- **待办清单** —— 今日 / 重要 / 计划内视图、自定义清单、步骤、附件、到期提醒与重复任务
- **多设备同步** —— 选择 OneDrive 或自己的 WebDAV，自动同步便签、分类、待办清单、步骤及附件，也可手动立即同步
- **可选端到端加密** —— 使用 AES-256-GCM 加密云端同步内容，新设备输入加密密码后解锁
- **备份与恢复** —— `.qns` 归档（ZIP + AES-256 加密），三种恢复模式
- **顶边自动隐藏** —— 便签停靠到屏幕边缘；外加全局热键
- **系统托盘** —— 关闭到托盘、开机自启
- **设置面板** —— 主题、排序、密度、置顶、记住窗口位置
- 崩溃转储 + 滚动日志，便于诊断

## 同步与加密

QNote 本地优先，无需注册 QNote 账号即可使用。需要跨 Windows 设备同步时，打开「设置 → 同步」：

1. 选择 **OneDrive** 并登录微软账号，或填写 **WebDAV** 目录、用户名与应用密码。
2. 点击「保存并启用」。首次连接已有内容的同步目标时，可选择合并或覆盖方向。
3. 在其他设备配置相同目标，即可同步便签与待办。两个页面均有「立即同步」按钮。
4. 如需保护云端内容，点击「加密存储 → 设置加密」，确认提示并设置独立加密密码。

加密密码与 WebDAV 应用密码相互独立。**请牢记并妥善保存加密密码**：QNote 无法找回密码；
若密码遗失，且没有可用的已解锁设备或备份，云端加密数据将无法恢复。
关闭加密会先确认，再转换云端数据，完成后才提示解密成功。

此功能加密的是**云端同步内容**，不加密本机数据库；云端文件名、条目标识和部分同步元数据仍可见。
WebDAV 推荐使用 HTTPS；HTTP 连接不保护账号凭据，即使启用了内容加密也应注意这一点。
同步会传播删除操作，不能替代独立备份。登录状态、WebDAV 凭据和记住的加密密钥受当前 Windows 用户保护，
迁移到其他电脑或 Windows 用户后，需要重新登录或配置，并输入加密密码。

数据处理说明见[隐私政策](docs/privacy-policy.md)。

## 安装

两个渠道，同一个应用：

- **Microsoft Store**（推荐——一键安装、自动更新、微软信任签名）：
  [QNote on Microsoft Store](https://apps.microsoft.com/detail/9NV57VJPTPCZ)
- **便携版 zip**（绿色软件——任意目录解压即用，数据随目录走）：
  [Releases](https://github.com/Mr-Second/QNote/releases) 下载
  `QNote_<version>_win-x64_native-aot.zip` —— NativeAOT 原生编译：体积小、启动快、零依赖

解压到任意**可写**目录后直接运行 `QNote.exe`。全部数据（笔记数据库、图片、日志）都在
exe 同级的 `data\` 目录——拷贝该目录即可迁移。便携版未做代码签名；下载后请用每个
Release 附带的 `SHA256SUMS.txt`（或 zip 的 `.sha256` 边车文件）校验，参见
[docs/code-signing-policy.md](docs/code-signing-policy.md)。

开发者排查发布版问题时，可下载同版本、同渠道的独立 PDB 符号包，见[调试符号说明](docs/debugging-symbols.md)。

Store 版与便携版数据相互独立——两版可在同一台机器共存。切换渠道请用应用内
「设置 → 备份 / 恢复」迁移数据。

**系统要求：** Windows 10 19041+ / Windows 11，x64。

## 从源码构建

需要 Windows 上的 **.NET 10 SDK**。

```powershell
# 构建
dotnet build QNote.slnx -c Debug

# 运行
dotnet run --project src/QNote/QNote.csproj -c Debug

# 运行测试（Core 层，无界面依赖）
dotnet test tests/QNote.Tests/QNote.Tests.csproj -c Debug
```

## 技术栈

| 关注点 | 选型 |
|---|---|
| UI | WinUI 3 / Windows App SDK 2.5.1，XAML + `x:Bind` |
| 运行时 | .NET 10 |
| MVVM | CommunityToolkit.Mvvm |
| 数据 | SQLite（Microsoft.Data.Sqlite），FTS5 全文搜索 |
| 测试 | xUnit |
| 分发 | Microsoft Store（MSIX）+ 便携版 zip（GitHub Releases） |

## 许可证

[MIT](LICENSE)

## 社区

本开源项目已链接并认可 [LINUX DO](https://linux.do) 社区。
