#include <QApplication>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQuickWindow>
#include <QSystemTrayIcon>
#include <QMenu>
#include <QIcon>
#include <QStyle>
#include <QSysInfo>
#include <QStandardPaths>
#include <QQuickStyle>

#include "CrashHandler.h"
#include "managers/Logger.h"
#include "controllers/NoteController.h"
#include "controllers/EdgeHideController.h"
#include "managers/SettingsManager.h"
#include "managers/CategoryManager.h"
#include "managers/ColorPaletteProvider.h"
#include "managers/TextFormatHelper.h"
#include "managers/ImageManager.h"
#include "managers/IconManager.h"
#include "managers/BackupManager.h"
#include "managers/TranslationManager.h"
#include "platform/WindowsTaskbarHelper.h"

#ifndef QNOTE_VERSION
#define QNOTE_VERSION "unknown"
#endif

#ifndef QNOTE_BUILD_DATE
#define QNOTE_BUILD_DATE __DATE__ " " __TIME__
#endif

namespace {

void _printEnvironmentInfo()
{
    qDebug() << "===== QNote startup self-check =====";
    qDebug() << "QNote version:" << QNOTE_VERSION;
    qDebug() << "Build date:" << QNOTE_BUILD_DATE;
    qDebug() << "Qt runtime version:" << qVersion();
    qDebug() << "Qt compile version:" << QT_VERSION_STR;
    qDebug() << "OS:" << QSysInfo::prettyProductName()
             << "(kernel" << QSysInfo::kernelVersion() << ")";
    qDebug() << "CPU architecture:" << QSysInfo::currentCpuArchitecture();
}

void _printPathInfo()
{
    qDebug() << "Application dir:" << QCoreApplication::applicationDirPath();
    qDebug() << "AppData dir:" << QStandardPaths::writableLocation(QStandardPaths::AppDataLocation);
    qDebug() << "Log file:" << Logger::currentLogFilePath();
    qDebug() << "===================================";
}

} // namespace

int main(int argc, char *argv[])
{
    CrashHandler::install();
    Logger::install();
    qDebug() << "main() start";
    _printEnvironmentInfo();

    QQuickWindow::setGraphicsApi(QSGRendererInterface::OpenGL);
    QQuickWindow::setDefaultAlphaBuffer(true);

    QApplication app(argc, argv);
    app.setOrganizationName("QNote");
    app.setApplicationName("QNote");
    app.setApplicationVersion(QStringLiteral(QNOTE_VERSION));
    QApplication::setQuitOnLastWindowClosed(false);

    // 切换 Qt Quick Controls 到非原生 Basic style，让自定义控件的 background/contentItem 合法化
    // （原生 style 下对 TextField/ScrollBar 等自定义会触发运行时警告）。
    QQuickStyle::setStyle("Basic");

    _printPathInfo();

    // i18n: 翻译必须在 QML 引擎加载之前安装，否则 qsTr 首次求值时拿不到译文。
    // 语言来源：SettingsManager.load() 之后读 settingsManager.language()，
    // 空则探测系统语言（QLocale::system()）。
    TranslationManager translationManager;

    // 字体策略：默认使用 Windows 系统字体（微软雅黑），用户可在设置页改。
    // SettingsManager 构造时已有默认值，setDefaults 传空表示不覆盖。
    // qrc 不再内嵌字体文件（曾含思源黑体 30MB + 霞鹜文楷 24MB，移除后 exe 从 57MB 降到约 2MB）。

    NoteController noteController;
    EdgeHideController edgeHideController;
    SettingsManager settingsManager;
    CategoryManager categoryManager;
    ColorPaletteProvider colorPaletteProvider;
    TextFormatHelper textFormatHelper;
    ImageManager imageManager;
    IconManager iconManager;
    BackupManager backupManager;
    WindowsTaskbarHelper windowsTaskbarHelper;
    backupManager.setNoteDatabase(&noteController.database());
    backupManager.setSettingsManager(&settingsManager);
    backupManager.setCategoryManager(&categoryManager);

    // 覆盖恢复后 SQLite::Database 指针失效，需重新注入所有依赖者
    QObject::connect(&backupManager, &BackupManager::dbReopened, [&]() {
        qDebug() << "dbReopened: re-injecting database pointer to all managers";
        settingsManager.setDatabase(&noteController.database().rawDb());
        categoryManager.setDatabase(&noteController.database().rawDb());
        settingsManager.load();
        categoryManager.loadCategories();
        // 异步重建搜索索引：DB 内容已全换，旧索引与新 DB 不一致
        noteController.rebuildSearchIndex();
    });
    settingsManager.setDefaults(QString(), QString());
    settingsManager.setDatabase(&noteController.database().rawDb());
    categoryManager.setDatabase(&noteController.database().rawDb());
    settingsManager.load();
    // 应用 UI 字体到全局（重启生效；HusWindow 不支持运行时 font 属性覆盖）
    QFont appFont(settingsManager.uiFontFamily(), settingsManager.uiFontSize());
    QApplication::setFont(appFont);
    qDebug() << "SettingsManager loaded: uiFont=" << settingsManager.uiFontFamily()
             << "editorFont=" << settingsManager.editorFontFamily()
             << "imageMode=" << settingsManager.imageStorageMode();
    // i18n 初始化：必须在 QML 引擎创建前、且在 CategoryManager.loadCategories() 前
    // （loadCategories 内部调 Constants::xxxCategory() 取翻译后显示名，translator 必须先装好）
    translationManager.initialize(settingsManager.language());
    qDebug() << "TranslationManager initialized:" << translationManager.currentLanguage();

    categoryManager.loadCategories();
    qDebug() << "CategoryManager loaded";

    colorPaletteProvider.load();
    qDebug() << "ColorPaletteProvider loaded";
    imageManager.setSettingsManager(&settingsManager);
    qDebug() << "ImageManager ready: storageMode=" << (settingsManager.imageStorageMode() == 0 ? "local" : "picgo");

    QQmlApplicationEngine engine;

    translationManager.setEngine(&engine);

    engine.rootContext()->setContextProperty("noteController", &noteController);
    engine.rootContext()->setContextProperty("edgeHideController", &edgeHideController);
    engine.rootContext()->setContextProperty("settingsManager", &settingsManager);
    engine.rootContext()->setContextProperty("categoryManager", &categoryManager);
    engine.rootContext()->setContextProperty("colorPaletteProvider", &colorPaletteProvider);
    engine.rootContext()->setContextProperty("textFormatHelper", &textFormatHelper);
    engine.rootContext()->setContextProperty("imageManager", &imageManager);
    engine.rootContext()->setContextProperty("iconManager", &iconManager);
    engine.rootContext()->setContextProperty("backupManager", &backupManager);
    engine.rootContext()->setContextProperty("translationManager", &translationManager);
    engine.rootContext()->setContextProperty("windowsTaskbarHelper", &windowsTaskbarHelper);

    QString exeDir = QCoreApplication::applicationDirPath();
    engine.addImportPath(exeDir);
    engine.addImportPath(exeDir + "/qml");

    engine.singletonInstance<QJSValue>("HuskarUI.Basic", "HusApp");

    const QUrl url(QStringLiteral("qrc:/qml/Main.qml"));
    QObject::connect(
        &engine, &QQmlApplicationEngine::objectCreated, &app,
        [url](QObject *obj, const QUrl &objUrl) {
            if (!obj && url == objUrl) {
                qCritical() << "QML engine failed to create root object:" << objUrl.toString();
                QCoreApplication::exit(-1);
            }
        },
        Qt::QueuedConnection);
    engine.load(url);

    qDebug() << "QML loaded";

    auto windows = engine.rootObjects();
    if (windows.isEmpty())
        return -1;

    auto rootWindow = qobject_cast<QQuickWindow *>(windows.first());

    // 启动最小化：主窗口不显示，仅托盘驻留
    if (settingsManager.startMinimized())
        rootWindow->hide();

    QIcon trayIcon(":/assets/note.ico");
    if (trayIcon.isNull())
        trayIcon = app.style()->standardIcon(QStyle::SP_ComputerIcon);

    rootWindow->setIcon(trayIcon);

    QSystemTrayIcon tray;
    tray.setIcon(trayIcon);
    tray.setToolTip(QCoreApplication::translate("main", "QNote"));

    QMenu trayMenu;
    // 托盘菜单继承 QApplication::setFont(会被用户 uiFontSize 放大),单独覆盖为系统默认大小
    QFont trayFont = trayMenu.font();
    trayFont.setPointSize(9);
    trayMenu.setFont(trayFont);

    // 语言切换后刷新 CategoryManager 内置分类（"全部"的 displayName 随翻译变化）
    // CategoryManager 的 categories 列表是 QML 的数据源，不刷新会残留旧语言的显示名。
    QObject::connect(&translationManager, &TranslationManager::languageChanged,
        &categoryManager, [&categoryManager]() { categoryManager.loadCategories(); });

    // 托盘菜单构建器：切语言时由 TranslationManager 调用重建菜单文字
    QAction *showAction = nullptr;
    QAction *quitAction = nullptr;
    auto rebuildTrayMenu = [&]() {
        trayMenu.clear();
        showAction = trayMenu.addAction(QCoreApplication::translate("main", "显示窗口"));
        trayMenu.addSeparator();
        quitAction = trayMenu.addAction(QCoreApplication::translate("main", "退出"));
        // 重新绑定（clear 已销毁旧 QAction，旧连接自动断开）
        QObject::connect(showAction, &QAction::triggered, rootWindow, [rootWindow]() {
            rootWindow->show();
            rootWindow->raise();
            rootWindow->requestActivate();
        });
        QObject::connect(quitAction, &QAction::triggered, &app, [&app]() {
            app.quit();
        });
    };
    rebuildTrayMenu();
    translationManager.setTrayMenuBuilder(rebuildTrayMenu);
    tray.setContextMenu(&trayMenu);

    QObject::connect(&tray, &QSystemTrayIcon::activated,
        [rootWindow](QSystemTrayIcon::ActivationReason reason) {
            if (reason == QSystemTrayIcon::DoubleClick) {
                rootWindow->show();
                rootWindow->raise();
                rootWindow->requestActivate();
            }
        });

    tray.show();

    return app.exec();
}
