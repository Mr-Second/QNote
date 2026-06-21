#include "SettingsManager.h"

#include "Constants.h"

#include <QDebug>
#include <QCoreApplication>
#include <QDir>
#include <QProcess>
#include <QSettings>

#include <SQLiteCpp/Database.h>
#include <SQLiteCpp/Statement.h>

SettingsManager::SettingsManager(QObject *parent)
    : QObject(parent)
    , m_uiFontFamily(QStringLiteral("Microsoft YaHei UI"))
    , m_editorFontFamily(QStringLiteral("Microsoft YaHei"))
    , m_picgoHost(Constants::defaultPicgoHost())
{
}

void SettingsManager::setDefaults(const QString &uiFont, const QString &editorFont)
{
    m_uiFontFamily = uiFont.isEmpty() ? m_uiFontFamily : uiFont;
    m_editorFontFamily = editorFont.isEmpty() ? m_editorFontFamily : editorFont;
}

void SettingsManager::setDatabase(SQLite::Database *db)
{
    m_db = db;
}

QString SettingsManager::getValue(const QString &key, const QString &defaultValue) const
{
    if (!m_db) return defaultValue;
    try {
        SQLite::Statement query(*m_db, "SELECT value FROM settings WHERE key = ?");
        query.bind(1, key.toStdString());
        if (query.executeStep())
            return QString::fromStdString(query.getColumn(0).getString());
    } catch (const std::exception &e) {
        qWarning() << "getValue failed:" << key << e.what();
    }
    return defaultValue;
}

int SettingsManager::getIntValue(const QString &key, int defaultValue) const
{
    bool ok = false;
    int val = getValue(key, QString::number(defaultValue)).toInt(&ok);
    return ok ? val : defaultValue;
}

void SettingsManager::setValue(const QString &key, const QString &value)
{
    if (!m_db) return;
    try {
        SQLite::Statement query(*m_db,
            "INSERT OR REPLACE INTO settings (key, value) VALUES (?, ?)");
        query.bind(1, key.toStdString());
        query.bind(2, value.toStdString());
        query.exec();
    } catch (const std::exception &e) {
        qWarning() << "setValue failed:" << key << e.what();
    }
}

void SettingsManager::load()
{
    if (!m_db) return;

    m_uiFontFamily = getValue(Constants::Settings::uiFontFamily, m_uiFontFamily);
    m_uiFontSize = getIntValue(Constants::Settings::uiFontSize, m_uiFontSize);
    m_editorFontFamily = getValue(Constants::Settings::editorFontFamily, m_editorFontFamily);
    m_editorFontSize = getIntValue(Constants::Settings::editorFontSize, m_editorFontSize);
    m_imageStorageMode = getIntValue(Constants::Settings::imageStorageMode, m_imageStorageMode);
    m_picgoHost = getValue(Constants::Settings::picgoHost, m_picgoHost);
    m_picgoPort = getIntValue(Constants::Settings::picgoPort, m_picgoPort);
    m_startMinimized = getIntValue(Constants::Settings::startMinimized, m_startMinimized ? 1 : 0) != 0;
    m_defaultThemeMode = getIntValue(Constants::Settings::defaultThemeMode, m_defaultThemeMode);
    m_autoSaveInterval = getIntValue(Constants::Settings::autoSaveInterval, m_autoSaveInterval);
    m_rememberWindowGeometry = getIntValue(Constants::Settings::rememberWindowGeometry, m_rememberWindowGeometry ? 1 : 0) != 0;
    m_stickOnTopDefault = getIntValue(Constants::Settings::stickOnTopDefault, m_stickOnTopDefault ? 1 : 0) != 0;
    m_windowX = getIntValue(Constants::Settings::windowX, m_windowX);
    m_windowY = getIntValue(Constants::Settings::windowY, m_windowY);
    m_windowWidth = getIntValue(Constants::Settings::windowWidth, m_windowWidth);
    m_windowHeight = getIntValue(Constants::Settings::windowHeight, m_windowHeight);
    m_listDensity = getIntValue(Constants::Settings::listDensity, m_listDensity);
    m_timeFormat = getIntValue(Constants::Settings::timeFormat, m_timeFormat);
    m_noteSortOrder = getIntValue(Constants::Settings::noteSortOrder, m_noteSortOrder);
    m_confirmBeforeDelete = getIntValue(Constants::Settings::confirmBeforeDelete, m_confirmBeforeDelete ? 1 : 0) != 0;
    m_launchAtStartup = getIntValue(Constants::Settings::launchAtStartup, m_launchAtStartup ? 1 : 0) != 0;
    m_language = getValue(Constants::Settings::language, m_language);

    emit settingsChanged();
}

void SettingsManager::save()
{
    if (!m_db) return;

    setValue(Constants::Settings::uiFontFamily, m_uiFontFamily);
    setValue(Constants::Settings::uiFontSize, QString::number(m_uiFontSize));
    setValue(Constants::Settings::editorFontFamily, m_editorFontFamily);
    setValue(Constants::Settings::editorFontSize, QString::number(m_editorFontSize));
    setValue(Constants::Settings::imageStorageMode, QString::number(m_imageStorageMode));
    setValue(Constants::Settings::picgoHost, m_picgoHost);
    setValue(Constants::Settings::picgoPort, QString::number(m_picgoPort));
    setValue(Constants::Settings::startMinimized, QString::number(m_startMinimized ? 1 : 0));
    setValue(Constants::Settings::defaultThemeMode, QString::number(m_defaultThemeMode));
    setValue(Constants::Settings::autoSaveInterval, QString::number(m_autoSaveInterval));
    setValue(Constants::Settings::rememberWindowGeometry, QString::number(m_rememberWindowGeometry ? 1 : 0));
    setValue(Constants::Settings::stickOnTopDefault, QString::number(m_stickOnTopDefault ? 1 : 0));
    setValue(Constants::Settings::windowX, QString::number(m_windowX));
    setValue(Constants::Settings::windowY, QString::number(m_windowY));
    setValue(Constants::Settings::windowWidth, QString::number(m_windowWidth));
    setValue(Constants::Settings::windowHeight, QString::number(m_windowHeight));
    setValue(Constants::Settings::listDensity, QString::number(m_listDensity));
    setValue(Constants::Settings::timeFormat, QString::number(m_timeFormat));
    setValue(Constants::Settings::noteSortOrder, QString::number(m_noteSortOrder));
    setValue(Constants::Settings::confirmBeforeDelete, QString::number(m_confirmBeforeDelete ? 1 : 0));
    setValue(Constants::Settings::launchAtStartup, QString::number(m_launchAtStartup ? 1 : 0));
    setValue(Constants::Settings::language, m_language);
}

QString SettingsManager::uiFontFamily() const { return m_uiFontFamily; }
void SettingsManager::setUiFontFamily(const QString &family) {
    if (m_uiFontFamily == family) return;
    m_uiFontFamily = family;
    emit settingsChanged();
}

int SettingsManager::uiFontSize() const { return m_uiFontSize; }
void SettingsManager::setUiFontSize(int size) {
    if (m_uiFontSize == size) return;
    m_uiFontSize = size;
    emit settingsChanged();
}

QString SettingsManager::editorFontFamily() const { return m_editorFontFamily; }
void SettingsManager::setEditorFontFamily(const QString &family) {
    if (m_editorFontFamily == family) return;
    m_editorFontFamily = family;
    emit settingsChanged();
}

int SettingsManager::editorFontSize() const { return m_editorFontSize; }
void SettingsManager::setEditorFontSize(int size) {
    if (m_editorFontSize == size) return;
    m_editorFontSize = size;
    emit settingsChanged();
}

int SettingsManager::imageStorageMode() const { return m_imageStorageMode; }
void SettingsManager::setImageStorageMode(int mode) {
    if (m_imageStorageMode == mode) return;
    m_imageStorageMode = mode;
    emit settingsChanged();
}

QString SettingsManager::picgoHost() const { return m_picgoHost; }
void SettingsManager::setPicgoHost(const QString &host) {
    if (m_picgoHost == host) return;
    m_picgoHost = host;
    emit settingsChanged();
}

int SettingsManager::picgoPort() const { return m_picgoPort; }
void SettingsManager::setPicgoPort(int port) {
    if (m_picgoPort == port) return;
    m_picgoPort = port;
    emit settingsChanged();
}

bool SettingsManager::startMinimized() const { return m_startMinimized; }
void SettingsManager::setStartMinimized(bool enabled) {
    if (m_startMinimized == enabled) return;
    m_startMinimized = enabled;
    emit settingsChanged();
}

int SettingsManager::defaultThemeMode() const { return m_defaultThemeMode; }
void SettingsManager::setDefaultThemeMode(int mode) {
    if (m_defaultThemeMode == mode) return;
    m_defaultThemeMode = mode;
    emit settingsChanged();
}

int SettingsManager::autoSaveInterval() const { return m_autoSaveInterval; }
void SettingsManager::setAutoSaveInterval(int ms) {
    if (m_autoSaveInterval == ms) return;
    m_autoSaveInterval = ms;
    emit settingsChanged();
}

bool SettingsManager::rememberWindowGeometry() const { return m_rememberWindowGeometry; }
void SettingsManager::setRememberWindowGeometry(bool enabled) {
    if (m_rememberWindowGeometry == enabled) return;
    m_rememberWindowGeometry = enabled;
    emit settingsChanged();
}

bool SettingsManager::stickOnTopDefault() const { return m_stickOnTopDefault; }
void SettingsManager::setStickOnTopDefault(bool enabled) {
    if (m_stickOnTopDefault == enabled) return;
    m_stickOnTopDefault = enabled;
    emit settingsChanged();
}

int SettingsManager::windowX() const { return m_windowX; }
void SettingsManager::setWindowX(int x) {
    if (m_windowX == x) return;
    m_windowX = x;
    emit settingsChanged();
}

int SettingsManager::windowY() const { return m_windowY; }
void SettingsManager::setWindowY(int y) {
    if (m_windowY == y) return;
    m_windowY = y;
    emit settingsChanged();
}

int SettingsManager::windowWidth() const { return m_windowWidth; }
void SettingsManager::setWindowWidth(int w) {
    if (m_windowWidth == w) return;
    m_windowWidth = w;
    emit settingsChanged();
}

int SettingsManager::windowHeight() const { return m_windowHeight; }
void SettingsManager::setWindowHeight(int h) {
    if (m_windowHeight == h) return;
    m_windowHeight = h;
    emit settingsChanged();
}

int SettingsManager::listDensity() const { return m_listDensity; }
void SettingsManager::setListDensity(int density) {
    if (m_listDensity == density) return;
    m_listDensity = density;
    emit settingsChanged();
}

int SettingsManager::timeFormat() const { return m_timeFormat; }
void SettingsManager::setTimeFormat(int format) {
    if (m_timeFormat == format) return;
    m_timeFormat = format;
    emit settingsChanged();
}

int SettingsManager::noteSortOrder() const { return m_noteSortOrder; }
void SettingsManager::setNoteSortOrder(int order) {
    if (m_noteSortOrder == order) return;
    m_noteSortOrder = order;
    emit settingsChanged();
}

bool SettingsManager::confirmBeforeDelete() const { return m_confirmBeforeDelete; }
void SettingsManager::setConfirmBeforeDelete(bool enabled) {
    if (m_confirmBeforeDelete == enabled) return;
    m_confirmBeforeDelete = enabled;
    emit settingsChanged();
}

bool SettingsManager::launchAtStartup() const { return m_launchAtStartup; }
void SettingsManager::setLaunchAtStartup(bool enabled) {
    if (m_launchAtStartup == enabled) return;
    m_launchAtStartup = enabled;
    syncLaunchAtStartupRegistry(enabled);
    emit settingsChanged();
}

QString SettingsManager::language() const { return m_language; }
void SettingsManager::setLanguage(const QString &language) {
    if (m_language == language) return;
    m_language = language;
    emit settingsChanged();
}

void SettingsManager::syncLaunchAtStartupRegistry(bool enabled)
{
#ifdef Q_OS_WIN
    QSettings reg(QStringLiteral("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"),
                  QSettings::NativeFormat);
    const QString key = QStringLiteral("QNote");
    if (enabled) {
        const QString exePath = QCoreApplication::applicationFilePath();
        reg.setValue(key, exePath);
    } else {
        reg.remove(key);
    }
    reg.sync();
    if (reg.status() != QSettings::NoError) {
        qWarning() << "LaunchAtStartup registry op failed, status=" << reg.status()
                   << "enabled=" << enabled;
    }
#else
    qInfo() << "LaunchAtStartup not implemented on this platform, enabled=" << enabled;
#endif
}

bool SettingsManager::restartApplication()
{
    // 重启策略 B2（临时 launcher 模式，参考 VSCode squirrel / Electron updater）：
    //   1. 启动 QNoteRelauncher.exe（独立小进程），传入当前 PID 和主程序路径
    //   2. 主程序立即 exit(0)
    //   3. Relauncher 用 WaitForSingleObject 等待旧 PID 退出（确保所有锁释放）
    //   4. Relauncher 启动新 QNote.exe 实例后自己退出（非常驻）
    //
    // 相比直接 startDetached+exit（Telegram 式）：
    //   - relauncher 在独立进程等待，主进程可以正常析构，时序更可控
    //   - 未来扩展热更新：relauncher 负责替换 exe 后再启动，主程序运行中也能更新
    //
    // 相比常驻 launcher（Chrome 模式）：
    //   - 平时只有 QNote.exe 一个进程，无额外开销
    //   - 仅重启/更新瞬间存在 relauncher，更轻量
    const QString exeDir = QCoreApplication::applicationDirPath();
    const QString relauncher = QDir(exeDir).absoluteFilePath(QStringLiteral("QNoteRelauncher.exe"));
    const QString mainExe = QCoreApplication::applicationFilePath();
    const qint64 pid = QCoreApplication::applicationPid();

    qDebug() << "Restart requested: launching relauncher. pid=" << pid
             << "relauncher=" << relauncher << "mainExe=" << mainExe;

    QStringList relauncherArgs;
    relauncherArgs << QStringLiteral("--wait-pid") << QString::number(pid)
                   << QStringLiteral("--timeout") << QStringLiteral("30")
                   << QStringLiteral("--launch") << mainExe;

    if (!QProcess::startDetached(relauncher, relauncherArgs)) {
        qWarning() << "restartApplication: failed to start relauncher" << relauncher;
        return false;
    }
    // relauncher 已接管，主程序正常退出（触发所有 Manager 析构，释放锁）
    QCoreApplication::exit(0);
    return true;
}
