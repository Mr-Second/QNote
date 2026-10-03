# QNote

**简体中文** | [English](README.en.md)

[![CI](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml)
[![Release](https://github.com/Mr-Second/QNote/actions/workflows/release.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/release.yml)

一款轻量的 Windows 便签应用，基于 **WinUI 3** 构建。

QNote 是对早期 Qt/QML 版本的 C# / WinUI 3 从零重写，唯一的北极星指标是：**低内存占用**。
Qt 版空载约消耗 250 MB 内存，QNote 只需 **45–50 MB**——足够精瘦，可以常驻后台一整天而无需惦记。

## 截图

| | |
|---|---|
| ![主界面——分类、便签列表与富文本编辑器](docs/screenshots/1-main.png) | ![即时全文搜索与关键词高亮](docs/screenshots/2-search.png) |
| ![设置面板](docs/screenshots/3-settings.png) | ![富文本便签](docs/screenshots/4-richtext.png) |

## 功能

- **便签增删改查** + 富文本编辑器与格式工具栏（加粗 / 斜体 / 列表 / 颜色 / ……）
- 基于 SQLite FTS5 的**即时搜索**，带关键词高亮
- 侧边栏**分类**，支持拖拽排序
- 便签内**插图**（右键可另存 / 替换 / 备注替代文本）
- **备份与恢复** —— `.qns` 归档（ZIP + AES-256 加密），三种恢复模式
- **顶边自动隐藏** —— 便签停靠到屏幕边缘；外加全局热键
- **系统托盘** —— 关闭到托盘、开机自启
- **设置面板** —— 主题、排序、密度、置顶、记住窗口位置
- 崩溃转储 + 滚动日志，便于诊断

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
