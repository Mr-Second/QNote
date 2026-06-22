set_project("QNote")
set_version("0.1.0")
set_languages("c++17")

add_rules("mode.debug", "mode.release")
add_rules("plugin.compile_commands.autoupdate", {outputdir = ".vscode"})

-- 启用 xpack 插件（用于产出 NSIS 安装包 + Portable zip 绿色版）
includes("@builtin/xpack")

option("qml_debug")
    set_default(false)
    set_showmenu(true)
    set_description("Enable QML debug logging to file")
option_end()

add_requires("sqlitecpp")
-- xapian-core 通过 conan 安装：强制 settings compiler.version=194（VS 17.4+，MSVC 19.4x）
-- conan center 上 xapian-core 1.4.24 有 msvc 192/193/194 的 Windows 二进制，
-- zlib 1.3.2 有 msvc 194 的 Windows 二进制。用 194 让两者都能直接下载 binary，
-- 避免 CI（VS 2026=195）和本地（193）从源码编译。
add_requires("conan::xapian-core/1.4.24", {alias = "xapian",
    configs = {
        -- 锁定 compiler.version=194（VS 17.4+，MSVC 19.4x）
        -- conan center 上 xapian-core 1.4.24 有 msvc 192/193/194 的 Windows 二进制，
        -- zlib 1.3.2 有 msvc 194 的 Windows 二进制。
        -- 用 194 让两者都能直接下载 binary，避免从源码编译。
        -- CI 的 VS 2026 (MSVC 19.5x=195) 与 194 ABI 兼容。
        settings = {"compiler=msvc", "compiler.version=194", "compiler.cppstd=14"}
    }})

-- Qt 路径：环境变量 QT_ROOT_DIR 优先（CI 用 install-qt-action 设置），否则用本地固定路径
local QT_DIR = os.getenv("QT_ROOT_DIR") or "D:/Qt/6.9.3/msvc2022_64"
local QT_BIN = QT_DIR .. "/bin"
local QT_QML = QT_DIR .. "/qml"
local QT_PLUGINS = QT_DIR .. "/plugins"

target("QNote")
    set_kind("binary")
    set_targetdir("$(builddir)/$(plat)/$(arch)/$(mode)/out")

    add_rules("qt.quickapp")

    if has_config("qml_debug") then
        add_defines("QML_DEBUG")
    end

    add_defines('QNOTE_VERSION="0.1.0"')
    -- QNOTE_BUILD_DATE 不通过 xmake 注入：源码 fallback 到 __DATE__ " " __TIME__
    -- （CrashHandler.cpp:32），避免 os.date 每次变化导致全量重编

    -- 排除 relauncher/ 目录（它有独立的 main()，属于 QNoteRelauncher target）
    add_files("src/**.cpp|relauncher/main.cpp")
    add_files("src/**.h|relauncher/**.h")
    add_files("src/resources.qrc")
    add_files("src/app.rc")
    add_includedirs("src")

    add_packages("sqlitecpp")
    add_packages("xapian")

    add_frameworks("QtCore", "QtGui", "QtQml", "QtQuick", "QtWidgets", "QtQuickControls2")

    add_cxxflags("/utf-8", {force = true})

    add_includedirs(QT_DIR .. "/include/HuskarUI")
    add_links("HuskarUIBasic")

    if is_plat("windows") then
        add_syslinks("user32", "shell32", "dbghelp", "psapi", "version", "ole32")
    end

    after_build(function(target)
        local outdir = target:targetdir()
        os.cp(path.join(QT_QML, "HuskarUI"), path.join(outdir, "HuskarUI"))
        os.cp(path.join(QT_BIN, "HuskarUIBasic.dll"), path.join(outdir, "HuskarUIBasic.dll"))
        os.cp(path.join(QT_BIN, "HuskarUIImpl.dll"), path.join(outdir, "HuskarUIImpl.dll"))
        os.cp(path.join(os.projectdir(), "src/tools/7za.exe"), path.join(outdir, "7za.exe"))

        -- 用 windeployqt 自动部署 Qt 运行时依赖，保证双击 exe 可直接运行
        local windeployqt = path.join(QT_BIN, "windeployqt.exe")
        if os.isfile(windeployqt) then
            os.exec("%s --release --no-translations --no-system-d3d-compiler --no-opengl-sw --dir %s --qmldir %s %s",
                windeployqt,
                outdir,
                path.join(os.projectdir(), "src/qml"),
                path.join(outdir, "QNote.exe"))
        end

        -- lrelease: 编译 .ts → .qm 并部署到 outdir/i18n/
        -- 用 try/catch 包裹，避免 .ts 语法错误或 lrelease 异常时阻塞主 build
        -- （用户仍可运行，只是翻译缺失，UI 显示源语言中文）
        local lrelease = path.join(QT_BIN, "lrelease.exe")
        if os.isfile(lrelease) then
            local ts_dir = path.join(os.projectdir(), "src/i18n")
            local qm_dir = path.join(outdir, "i18n")
            os.mkdir(qm_dir)
            for _, tsfile in ipairs(os.files(path.join(ts_dir, "*.ts")) or {}) do
                try {
                    function()
                        os.exec("%s %s -qm %s/%s.qm",
                            lrelease,
                            tsfile,
                            qm_dir,
                            path.basename(tsfile))
                    end,
                    catch {
                        function(errors)
                            print("warning: lrelease failed for " .. tsfile .. ": " .. tostring(errors))
                        end
                    }
                }
            end
        end
    end)

    on_run(function(target)
        local old_path = os.getenv("PATH") or ""
        local new_path = QT_BIN .. ";" .. QT_PLUGINS .. ";" .. old_path
        os.setenv("PATH", new_path)
        os.setenv("QML2_IMPORT_PATH", QT_QML)
        os.execv(target:targetfile())
    end)

target("QNoteI18n")
    set_kind("phony")
    set_default(false)

    on_build(function(target)
        local lupdate = path.join(QT_BIN, "lupdate.exe")
        if not os.isfile(lupdate) then
            print("lupdate.exe not found at " .. lupdate)
            return
        end
        local ts_dir = path.join(os.projectdir(), "src/i18n")
        local src_dir = path.join(os.projectdir(), "src")
        for _, tsfile in ipairs(os.files(path.join(ts_dir, "*.ts"))) do
            os.exec("%s %s -ts %s", lupdate, src_dir, tsfile)
        end
        print("lupdate done. Review src/i18n/*.ts for new entries.")
    end)

target("QNoteRelauncher")
    set_kind("binary")
    set_targetdir("$(builddir)/$(plat)/$(arch)/$(mode)/out")

    add_rules("qt.console")

    add_files("src/relauncher/main.cpp")
    add_includedirs("src")

    add_frameworks("QtCore")

    add_cxxflags("/utf-8", {force = true})

    on_run(function(target)
        local args = table.wrap(option.get("arguments") or {})
        os.execv(target:targetfile(), args)
    end)

target("QNotePack")
    set_kind("phony")
    set_default(false)
    add_deps("QNote")
    add_deps("QNoteRelauncher")

    on_build(function(target)
        import("core.base.json")

        local mode = is_mode("debug") and "debug" or "release"
        local srcdir = path.join("$(builddir)/$(plat)/$(arch)/$(mode)/out")
        local outdir = path.join("$(builddir)/$(plat)/$(arch)/$(mode)/pack")

        os.rm(outdir)
        os.mkdir(outdir)

        os.cp(path.join(srcdir, "QNote.exe"), outdir)
        os.cp(path.join(srcdir, "HuskarUIBasic.dll"), outdir)
        os.cp(path.join(srcdir, "HuskarUIImpl.dll"), outdir)
        os.cp(path.join(srcdir, "QNoteRelauncher.exe"), outdir)

        local excludes = json.loadfile(path.join(os.projectdir(), "pack-excludes.json"))
        local deploy_args = {}

        -- windeployqt 只支持有限的 --no-* 选项（--no-translations, --no-opengl-sw, --no-ffmpeg 等）
        -- excludeLibraries 里的模块名（quick3d/3dcore 等）不是 windeployqt 选项，
        -- 传给 windeployqt 会报 "Unknown options"。改为 windeployqt 后手动删除对应 DLL。
        -- 这里只把 excludeFlags（translations/opengl-sw/ffmpeg 等）传给 windeployqt。
        if excludes.skipPluginTypes and #excludes.skipPluginTypes > 0 then
            table.insert(deploy_args, "--skip-plugin-types " .. table.concat(excludes.skipPluginTypes, ","))
        end

        -- excludeLibraries 不传给 windeployqt（避免 Unknown options 错误）
        -- 对应的 DLL 会在 windeployqt 后通过 libPatterns 删除

        if excludes.excludeFlags then
            for _, flag in ipairs(excludes.excludeFlags) do
                table.insert(deploy_args, "--no-" .. flag)
            end
        end

        local mode_flag = (mode == "debug") and "--debug" or "--release"

        os.exec("%s %s --dir %s --qmldir %s %s %s",
            path.join(QT_BIN, "windeployqt.exe"),
            mode_flag,
            outdir,
            path.join(os.projectdir(), "src/qml"),
            path.join(outdir, "QNote.exe"),
            table.concat(deploy_args, " "))

        if excludes.cleanPatterns then
            for _, pattern in ipairs(excludes.cleanPatterns) do
                os.rm(path.join(outdir, pattern))
            end
        end

        -- excludeLibraries 对应的 DLL 在 windeployqt 后删除（windeployqt 不支持 --no-<lib>）
        -- 把模块名转成 DLL glob：quick3d → Qt6Quick3D*.dll, 3dcore → Qt63DCore*.dll 等
        if excludes.excludeLibraries then
            for _, lib in ipairs(excludes.excludeLibraries) do
                -- Qt DLL 命名规则：Qt6<LibName>.dll（首字母大写，去掉数字前缀的 3d → 3D）
                -- 例：quick3d → Qt6Quick3D*.dll, 3dcore → Qt63DCore*.dll, svg → Qt6Svg*.dll
                local dllName = lib:gsub("^%d", function(c) return c end):gsub("^(.)", function(c) return c:upper() end)
                -- 特殊处理 3d* 前缀（3dcore → 3DCore，不是 3dcore → 3DCore）
                dllName = lib:gsub("^3d", "3D"):gsub("^quick3d", "Quick3D")
                -- 首字母大写（除 3d 已处理）
                if not dllName:match("^3D") then
                    dllName = dllName:gsub("^(.)", function(c) return c:upper() end)
                end
                -- 尝试多种命名变体删除
                os.rm(path.join(outdir, "Qt6" .. dllName .. "*.dll"))
                os.rm(path.join(outdir, "Qt6" .. dllName .. "*.pdb"))
            end
        end

        -- === 阶段2裁剪：pack-excludes 加固（06-21-installer-packaging 子任务 2） ===
        -- main.cpp 已强制 QQuickStyle::setStyle("Basic")，非 Basic style 无用。

        -- A1. 删除非 Basic style
        local non_basic_styles = {
            "Fusion", "Imagine", "Material", "Universal",
            "FluentWinUI3", "Windows", "macOS", "iOS", "Android"
        }
        for _, s in ipairs(non_basic_styles) do
            os.rm(path.join(outdir, "qml/QtQuick/Controls/" .. s))
        end

        -- A2. 删除 NativeStyle（Basic style 下无用）
        os.rm(path.join(outdir, "qml/QtQuick/NativeStyle"))

        -- A3. imageformats 仅保留 qjpeg / qico / qsvg（png 走 Qt 内建）
        local keep_imgfmt = { jpeg = true, ico = true, svg = true }
        local imgfmt_dir = path.join(outdir, "imageformats")
        if os.isdir(imgfmt_dir) then
            for _, f in ipairs(os.files(path.join(imgfmt_dir, "*")) or {}) do
                local basename = path.basename(f):lower()
                local fmt = basename:gsub("^q", "")
                if not keep_imgfmt[fmt] then
                    os.rm(f)
                end
            end
        end

        -- A4. 补回 SVG 支持（pack-excludes.json 排除了 svg 库，但 QNote 标题栏
        -- 图标 note.svg 是 qrc 内嵌资源，运行时需要 Qt6Svg.dll + qsvg.dll 才能解码）
        os.cp(path.join(QT_BIN, "Qt6Svg.dll"), path.join(outdir, "Qt6Svg.dll"))
        os.cp(path.join(QT_PLUGINS, "imageformats", "qsvg.dll"),
              path.join(outdir, "imageformats", "qsvg.dll"))
        local qsvgicon = path.join(QT_PLUGINS, "iconengines", "qsvgicon.dll")
        if os.isfile(qsvgicon) then
            os.cp(qsvgicon, path.join(outdir, "iconengines", "qsvgicon.dll"))
        end

        -- A5. 删除非 Basic style 对应的 DLL（windeployqt 按 QML 依赖部署了全部 style）
        -- main.cpp 强制 Basic style，Fusion/Material/Universal/Imagine/FluentWinUI3 的 DLL 无用
        local non_basic_dlls = {
            "Qt6QuickControls2Fusion.dll",
            "Qt6QuickControls2Material.dll",
            "Qt6QuickControls2Universal.dll",
            "Qt6QuickControls2Imagine.dll",
            "Qt6QuickControls2FluentWinUI3.dll",
            "Qt6QuickControls2Windows.dll",
            "Qt6QuickControls2FusionStyleImpl.dll",
            "Qt6QuickControls2MaterialStyleImpl.dll",
            "Qt6QuickControls2UniversalStyleImpl.dll",
            "Qt6QuickControls2ImagineStyleImpl.dll",
            "Qt6QuickControls2FluentWinUI3StyleImpl.dll",
            "Qt6QuickControls2WindowsStyleImpl.dll",
        }
        for _, dll in ipairs(non_basic_dlls) do
            local f = path.join(outdir, dll)
            if os.isfile(f) then
                os.rm(f)
            end
        end
    end)

target("QNoteTest")
    set_kind("binary")
    set_default(false)
    set_targetdir("$(builddir)/$(plat)/$(arch)/$(mode)/out")

    add_rules("qt.console")

    add_files("tests/TestQNote.cpp", {rules = "qt.moc"})
    add_files("src/models/NoteModel.h", "src/controllers/EdgeHideController.h", "src/managers/TextFormatHelper.h", "src/managers/SearchManager.h", "src/managers/RebuildIndexWorker.h", {rules = "qt.moc"})
    add_files("src/database/*.cpp", "src/models/*.cpp", "src/controllers/EdgeHideController.cpp", "src/managers/TextFormatHelper.cpp", "src/managers/SearchManager.cpp", "src/managers/RebuildIndexWorker.cpp")
    add_includedirs("src")

    add_packages("sqlitecpp")
    add_packages("xapian")

    add_frameworks("QtCore", "QtGui", "QtQml", "QtQuick", "QtWidgets", "QtTest")

    add_cxxflags("/Zc:__cplusplus", "/permissive-", "/utf-8", {force = true})

    on_run(function(target)
        local old_path = os.getenv("PATH") or ""
        local new_path = QT_BIN .. ";" .. QT_PLUGINS .. ";" .. old_path
        os.setenv("PATH", new_path)
        -- 透传用户在 `xmake run QNoteTest <args>` 后传入的参数(如 -v2 / -o results.txt,txt)。
        -- xmake 的 on_run 只回传 target,不传 args(见 xmake actions/run/main.lua:147);
        -- 通过全局 option("arguments") 取命令行 runargs。
        import("core.base.option")
        local args = table.wrap(option.get("arguments") or {})
        os.execv(target:targetfile(), args)
    end)

-- ============================================================================
-- xpack：NSIS 安装包 + Portable zip 绿色版
-- ============================================================================
-- 用法：
--   xmake pack QNote -f nsis        # 生成 QNote-Setup-<version>-x64.exe
--   xmake pack QNote -f zip         # 生成 QNote-Portable-<version>-x64.zip
--   xmake pack QNote -f nsis,zip    # 一次出双包
--   xmake pack QNote                # 不加 -f 时按 set_formats 全部产出
--
-- 依赖：makensis (PATH 优先，缺失时 xpack 通过 xrepo 自动下载 NSIS)
-- 数据源：QNotePack target 的 pack/ 目录全部内容
xpack("QNote")
    set_formats("nsis", "zip")
    set_title("QNote")
    set_description("QNote 桌面便签应用")
    set_author("QNote")
    set_version("0.1.0")
    set_iconfile("src/assets/note.ico")

    -- 自定义 NSI specfile（基于 xpack 默认模板，追加 MUI_FINISHPAGE_RUN 完成页"运行"复选框）
    set_specfile("packaging/QNote.nsi")

    -- 按格式动态定制 basename：
    --   nsis → QNote-Setup-<version>-x64.exe（PRD AC2）
    --   zip  → QNote-Portable-<version>-x64.zip（PRD AC2b）
    on_load(function (package)
        local format = package:format()
        local ver = package:version()
        if format == "nsis" then
            package:set("basename", "QNote-Setup-" .. ver .. "-x64")
        elseif format == "zip" then
            package:set("basename", "QNote-Portable-" .. ver .. "-x64")
        end
    end)

    -- 完全自定义安装逻辑：直接把 QNotePack 的 pack/ 目录内容 cp 到 installdir。
    -- 绕过 add_installfiles 的 glob 解析（pack/ 是 phony target 产物，不在标准 targetfile 路径）。
    on_installcmd(function (package, batchcmds)
        local mode = is_mode("debug") and "debug" or "release"
        local packdir = path.join(os.projectdir(), "build", package:plat(), package:arch(), mode, "pack")
        local installdir = package:installdir()
        local srcfiles = os.files(path.join(packdir, "**"))
        for _, srcfile in ipairs(srcfiles or {}) do
            local relpath = path.relative(srcfile, packdir)
            batchcmds:cp(srcfile, path.join(installdir, relpath))
        end
    end)

    -- 完全自定义卸载逻辑：rm 整个 installdir。
    -- 因为 on_installcmd 是自定义的，默认 on_uninstallcmd 调 package:installfiles() 返回空，
    -- 不会删除任何文件，必须显式 rmdir。
    on_uninstallcmd(function (package, batchcmds)
        batchcmds:rmdir(package:installdir())
    end)

    -- 开始菜单快捷方式（仅 nsis；zip 格式 rawcmd("nsis") 会被忽略）
    -- 注意：rawcmd 里不能用 ${PACKAGE_NAME}（xpack 的 io.gsub 不会二次扫描嵌入内容），
    -- 直接用字面 "QNote"。
    after_installcmd(function (package, batchcmds)
        if package:format() == "nsis" then
            batchcmds:rawcmd("nsis", [[
  CreateDirectory "$SMPROGRAMS\QNote"
  CreateShortCut "$SMPROGRAMS\QNote\QNote.lnk" "$InstDir\QNote.exe"
  CreateShortCut "$SMPROGRAMS\QNote\Uninstall QNote.lnk" "$InstDir\uninstall.exe"
]])
        end
    end)

    after_uninstallcmd(function (package, batchcmds)
        if package:format() == "nsis" then
            batchcmds:rawcmd("nsis", [[
  RMDir /r "$SMPROGRAMS\QNote"
  Delete "$DESKTOP\QNote.lnk"
]])
        end
    end)
