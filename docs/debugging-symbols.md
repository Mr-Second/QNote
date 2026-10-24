# 发布版调试符号

GitHub Release 为每个版本单独提供两份符号包，普通用户只需下载便携版应用包。

| 文件名后缀 | 用途 |
|---|---|
| `_native-aot_symbols.zip` | 同次构建的便携版 |
| `_store-aot_symbols.zip` | 同次构建的 Store MSIX |

符号包内包含原生 `QNote.pdb`、`symbols.json` 与使用说明。普通便携包不包含 PDB，
因此保留调试符号不会增大应用的下载体积。SHA256SUMS.txt 覆盖应用包和两份符号包。

## 可以调试 MSIX 吗？

可以。MSIX 中的 `QNote.exe` 也是可执行程序，配套 PDB 可用于调试该进程和分析
崩溃转储。**PDB 必须精确匹配二进制**，不能只看版本号或源码是否相同。
便携版与 Store 版分别编译，不能互换符号。

发布流水线会：

1. 分别编译便携版与 Store 版，保留 NativeAOT 生成的 `QNote.pdb`。
2. 使用 Windows DbgHelp 校验 EXE 与 PDB 的 GUID / age。
3. 对 Store 版进一步校验 MSIX 内 EXE 与符号对应 EXE 的 SHA-256 完全一致。
4. 将版本、渠道、源码提交、PDB 标识及文件哈希写入 `symbols.json`，再单独压缩符号。

**向商店上传时，请使用同次 Actions 运行的 `store-msix` artifact。**
自行在本地重新编译同版本 MSIX，不保证能使用 Release 中的 PDB。
`store-msix` 构建产物保留 90 天；请在有效期内下载并上传至 Partner Center。
GitHub Release 不提供该未签名 MSIX 作为安装下载。

## 使用方法

1. 下载与问题版本、分发渠道一致的符号包并解压。
2. 按 `symbols.json` 中的 commit 检出源码。
3. 在 Visual Studio 的「工具 → 选项 → 调试 → 符号」中添加解压目录，
   或将它加入 WinDbg 的符号搜索路径。
4. 附加到对应 QNote 进程，或打开该版本的崩溃转储，检查 `QNote.exe` 的符号加载状态。

发布版使用 NativeAOT，应选择**本机 / Native 调试**；普通托管调试器不适用。
Release 优化会影响部分变量和逐行调试体验。此 ZIP 用于手动调试，
不等同于微软商店自动崩溃解析所用的 `.appxsym` 符号包。

## 本地归档

```powershell
./scripts/Export-QNoteSymbols.ps1 `
  -PublishDirectory src/QNote/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish-portable-aot `
  -Channel native-aot -Version 2.0.0.0 -OutputDirectory artifacts/symbols
```

Store 版使用 `-Channel store-aot`，并额外提供 `-PackagePath <同次构建的.msix>`。
请从实际发布的二进制生成符号归档，并记录对应源码提交；不要重新编译来补配旧版本符号。

参考：[NativeAOT 调试信息](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#native-debug-information)、
[NativeAOT 本机调试](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics#native-debugging)、
[Visual Studio 符号匹配](https://learn.microsoft.com/en-us/visualstudio/debugger/specify-symbol-dot-pdb-and-source-files-in-the-visual-studio-debugger)。
