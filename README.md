# QNote

> Qt/QML + C++ 桌面便签应用 — 卡通拟物化视觉，MVP 阶段

一个跨平台的桌面便签应用，用 Qt 6 / QML 打造温暖的纸张质感视觉，支持富文本编辑、全文搜索、分类管理、系统托盘驻留。

<!-- 截图占位：后续添加主界面截图 -->
<!-- ![QNote Main Window](docs/screenshot-main.png) -->

## 功能特性

### 核心功能
- **便签管理**：创建 / 编辑 / 删除（含确认弹窗）
- **富文本编辑器**：加粗 / 斜体 / 下划线 / 删除线 / 列表 / 对齐 / 字体 / 字号 / 颜色
- **图片插入**：本地文件 + PicGo + 剪贴板粘贴
- **全文搜索**：基于 [Xapian](https://xapian.org/) 的中文分词搜索 + 高亮匹配
- **分类管理**：自定义图标 / 改名 / 删除 / 移动便签
- **数据备份/恢复**：7z 压缩 + 可选 AES-256 加密 + 异步进度 + 恢复后索引重建

### 系统集成
- **系统托盘**：显隐 / 右键菜单 / 最小化驻留
- **窗口贴边隐藏**：贴屏幕边缘自动隐藏，鼠标靠近浮现
- **开机自启动**：注册表 HKCU Run 项
- **响应式布局**：窗口缩放自适应三栏布局

### 视觉设计
- **纸张质感背景**：QML Canvas 绘制的温暖纹理
- **异形窗口**：自定义标题栏 + SVG 角标
- **主题系统**：Light / Dark / System 三态切换（[HuskarUI](https://github.com/mengps/HuskarUI) 集成）
- **自定义视觉层**：AppTheme token 体系（科技蓝品牌色 + 中性深灰）

### 国际化
- **多语言**：简体中文（源语言）/ English，运行时切换
- **源字符串是中文**：`qsTr()` 包裹英文翻译，切换语言实时生效

### 稳定性基础设施
- **运行期日志**：`qDebug` 按天落盘到 `%APPDATA%\QNote\QNote\logs\`，7 天滚动
- **崩溃捕获**：SEH + CRT + pure virtual call + 未捕获异常，生成 minidump + triage 文本
- **启动自检**：版本 / 环境 / 字体 / Manager 加载状态日志

## 下载安装

### 方式一：安装版（推荐）

从 [Releases](../../releases) 页面下载 `QNote-Setup-<version>-x64.exe`，双击安装。

- 标准安装向导（欢迎 / 路径 / 组件 / 完成）
- 自动注册到"添加/删除程序"
- 开始菜单快捷方式
- 安装前自动检测 QNote 运行中并提示关闭
- 完成页可选"运行 QNote"
- 卸载时询问是否删除用户数据

### 方式二：Portable 绿色版

下载 `QNote-Portable-<version>-x64.zip`，解压后双击 `QNote.exe` 运行。

- 不写注册表、不留系统痕迹
- 用户数据仍存 `%APPDATA%\QNote\`（与安装版共享）

### 系统要求
- Windows 10/11 x64
- 无需预装运行时（Qt 静态链接 + windeployqt 部署）

## 从源码构建

### 依赖

- **Qt** 6.9.3+（msvc2022_64）
- **xmake** 3.0+（构建系统）
- **Visual Studio 2022**（MSVC 工具链）
- **[HuskarUI](https://github.com/mengps/HuskarUI)**（Qt 组件库，需先 CMake 安装到 Qt 目录）
- **Python 3**（xmake 包管理用）

### 构建命令

```powershell
# 配置（指定 Qt 路径）
xmake f -p windows -a x64 -m release --qt="D:/Qt/6.9.3/msvc2022_64"

# 编译主程序
xmake build

# 运行
xmake run

# 散文件打包（部署 Qt 运行时到 build/.../pack/）
xmake build QNotePack

# 生成安装包 + 绿色版（输出到 build/xpack/QNote/）
xmake pack QNote
# 单独某种格式：
xmake pack QNote -f nsis   # NSIS 安装包
xmake pack QNote -f zip    # Portable 绿色版
```

### 运行测试

```powershell
xmake build QNoteTest
xmake run QNoteTest -o "$env:TEMP\qnt\results.txt,txt"
```

测试基线：48 passed / 0 failed（EdgeHide 7 + NoteDatabase 11 + NoteModel 8 + RebuildIndexWorker 4 + SearchManager 8 + SearchManagerSwap 3 + TextFormatHelper 7）。

## 项目结构

```
QNote/
├── src/                    # 源码
│   ├── main.cpp            # 入口（CrashHandler → Logger → QApplication → QML）
│   ├── app.rc              # Windows 资源（图标、版本信息）
│   ├── resources.qrc       # Qt 资源文件
│   ├── assets/             # 图标、颜色预设
│   ├── database/           # SQLite 数据层（NoteDatabase）
│   ├── models/             # 数据模型（NoteModel）
│   ├── controllers/        # 控制器（NoteController、EdgeHideController）
│   ├── managers/           # 业务逻辑（SettingsManager、CategoryManager、
│   │                       #            SearchManager、BackupManager、
│   │                       #            TranslationManager、ImageManager...）
│   ├── qml/                # QML UI
│   │   ├── Main.qml
│   │   ├── theme/          # AppTheme、颜色 token
│   │   └── components/     # 自定义组件（编辑器、列表、对话框...）
│   ├── i18n/               # 翻译文件（qnote_en.ts）
│   ├── relauncher/         # 重启辅助进程（避免新旧进程重叠）
│   └── tools/              # 工具（7za.exe 用于备份）
├── tests/                  # 单元测试（QtTest）
├── packaging/              # NSIS 安装包配置
│   ├── QNote.nsi           # NSI specfile（基于 xpack 默认模板 + 自定义）
│   └── zh_strings.nsh      # 中文 LangString（UTF-8 BOM，避免编码损坏）
├── .github/workflows/      # GitHub Actions
│   └── release.yml         # tag 触发的 release 打包
├── pack-excludes.json      # windeployqt 裁剪规则
└── xmake.lua               # 构建配置（含 xpack 打包块）
```

## 技术栈

| 类别 | 技术 |
|---|---|
| 语言 | C++17 / QML / JavaScript |
| 框架 | Qt 6.9.3（QtCore / QtGui / QtQml / QtQuick / QtQuickControls2）|
| 构建 | [xmake](https://xmake.io) 3.0+ |
| UI 库 | [HuskarUI](https://github.com/mengps/HuskarUI)（Ant Design QML 组件）|
| 数据库 | SQLite（通过 [sqlitecpp](https://github.com/SRombauts/SQLiteCpp) C++ 封装）|
| 搜索 | [Xapian](https://xapian.org/) 1.4.24（中文分词全文索引）|
| 打包 | xmake xpack（NSIS + zip 双格式）+ GitHub Actions |
| 视觉 | 自定义 QML Canvas / SVG（纸张质感、异形窗口）|

## 数据存储

| 路径 | 内容 |
|---|---|
| `%APPDATA%\QNote\QNote\notes.db` | SQLite 数据库（便签内容、分类、设置）|
| `%APPDATA%\QNote\QNote\images\` | 便签图片 |
| `%APPDATA%\QNote\QNote\settings.db` | 应用设置（字体、主题、窗口位置等）|
| `%APPDATA%\QNote\QNote\logs\` | 按天滚动日志（7 天保留）|
| `%APPDATA%\QNote\QNote\CrashDumps\` | 崩溃 minidump + triage 文本 |

## 路线图

- [x] MVP（便签 CRUD + 搜索 + 编辑器 + 分类）
- [x] 系统集成（托盘 + 贴边隐藏 + 自启动）
- [x] 稳定性基础设施（日志 + 崩溃捕获 + 启动自检）
- [x] 多语言（中英运行时切换）
- [x] 数据备份/恢复（7z + 加密）
- [x] 安装包分发（NSIS + Portable）
- [x] CI/CD（GitHub Actions release）
- [ ] 自动更新检查
- [ ] 代码签名
- [ ] 多设备同步（可选）

## 许可证

待定（添加 LICENSE 文件）。

## 致谢

- [Qt](https://www.qt.io/) — 跨平台应用框架
- [xmake](https://xmake.io/) — Lua 驱动的构建系统
- [HuskarUI](https://github.com/mengps/HuskarUI) — Ant Design 风格 QML 组件库
- [Xapian](https://xapian.org/) — 全文搜索引擎
- [sqlitecpp](https://github.com/SRomboutys/SQLiteCpp) — SQLite C++ 封装
