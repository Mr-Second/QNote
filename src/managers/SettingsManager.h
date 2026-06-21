#pragma once

#include <QObject>

namespace SQLite { class Database; }

class SettingsManager : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString uiFontFamily READ uiFontFamily WRITE setUiFontFamily NOTIFY settingsChanged)
    Q_PROPERTY(int uiFontSize READ uiFontSize WRITE setUiFontSize NOTIFY settingsChanged)
    Q_PROPERTY(QString editorFontFamily READ editorFontFamily WRITE setEditorFontFamily NOTIFY settingsChanged)
    Q_PROPERTY(int editorFontSize READ editorFontSize WRITE setEditorFontSize NOTIFY settingsChanged)
    Q_PROPERTY(int imageStorageMode READ imageStorageMode WRITE setImageStorageMode NOTIFY settingsChanged)
    Q_PROPERTY(QString picgoHost READ picgoHost WRITE setPicgoHost NOTIFY settingsChanged)
    Q_PROPERTY(int picgoPort READ picgoPort WRITE setPicgoPort NOTIFY settingsChanged)
    Q_PROPERTY(bool startMinimized READ startMinimized WRITE setStartMinimized NOTIFY settingsChanged)
    Q_PROPERTY(int defaultThemeMode READ defaultThemeMode WRITE setDefaultThemeMode NOTIFY settingsChanged)
    Q_PROPERTY(int autoSaveInterval READ autoSaveInterval WRITE setAutoSaveInterval NOTIFY settingsChanged)
    Q_PROPERTY(bool rememberWindowGeometry READ rememberWindowGeometry WRITE setRememberWindowGeometry NOTIFY settingsChanged)
    Q_PROPERTY(bool stickOnTopDefault READ stickOnTopDefault WRITE setStickOnTopDefault NOTIFY settingsChanged)
    Q_PROPERTY(int windowX READ windowX WRITE setWindowX NOTIFY settingsChanged)
    Q_PROPERTY(int windowY READ windowY WRITE setWindowY NOTIFY settingsChanged)
    Q_PROPERTY(int windowWidth READ windowWidth WRITE setWindowWidth NOTIFY settingsChanged)
    Q_PROPERTY(int windowHeight READ windowHeight WRITE setWindowHeight NOTIFY settingsChanged)
    Q_PROPERTY(int listDensity READ listDensity WRITE setListDensity NOTIFY settingsChanged)
    Q_PROPERTY(int timeFormat READ timeFormat WRITE setTimeFormat NOTIFY settingsChanged)
    Q_PROPERTY(int noteSortOrder READ noteSortOrder WRITE setNoteSortOrder NOTIFY settingsChanged)
    Q_PROPERTY(bool confirmBeforeDelete READ confirmBeforeDelete WRITE setConfirmBeforeDelete NOTIFY settingsChanged)
    Q_PROPERTY(bool launchAtStartup READ launchAtStartup WRITE setLaunchAtStartup NOTIFY settingsChanged)
    Q_PROPERTY(QString language READ language WRITE setLanguage NOTIFY settingsChanged)
public:
    explicit SettingsManager(QObject *parent = nullptr);

    void setDefaults(const QString &uiFont, const QString &editorFont);
    void setDatabase(SQLite::Database *db);
    void load();
    Q_INVOKABLE void save();

    QString uiFontFamily() const;
    void setUiFontFamily(const QString &family);
    int uiFontSize() const;
    void setUiFontSize(int size);
    QString editorFontFamily() const;
    void setEditorFontFamily(const QString &family);
    int editorFontSize() const;
    void setEditorFontSize(int size);
    int imageStorageMode() const;
    void setImageStorageMode(int mode);
    QString picgoHost() const;
    void setPicgoHost(const QString &host);
    int picgoPort() const;
    void setPicgoPort(int port);

    bool startMinimized() const;
    void setStartMinimized(bool enabled);
    int defaultThemeMode() const;
    void setDefaultThemeMode(int mode);

    int autoSaveInterval() const;
    void setAutoSaveInterval(int ms);
    bool rememberWindowGeometry() const;
    void setRememberWindowGeometry(bool enabled);
    bool stickOnTopDefault() const;
    void setStickOnTopDefault(bool enabled);
    int windowX() const;
    void setWindowX(int x);
    int windowY() const;
    void setWindowY(int y);
    int windowWidth() const;
    void setWindowWidth(int w);
    int windowHeight() const;
    void setWindowHeight(int h);

    int listDensity() const;
    void setListDensity(int density);
    int timeFormat() const;
    void setTimeFormat(int format);

    int noteSortOrder() const;
    void setNoteSortOrder(int order);
    bool confirmBeforeDelete() const;
    void setConfirmBeforeDelete(bool enabled);

    bool launchAtStartup() const;
    void setLaunchAtStartup(bool enabled);

    QString language() const;
    void setLanguage(const QString &language);

    // 重启应用（字体改动等需重启生效的场景）。成功启动新进程后 exit(0)。
    Q_INVOKABLE bool restartApplication();

signals:
    void settingsChanged();

private:
    SQLite::Database *m_db = nullptr;
    QString m_uiFontFamily;
    int m_uiFontSize = 14;
    QString m_editorFontFamily;
    int m_editorFontSize = 16;
    int m_imageStorageMode = 0;
    QString m_picgoHost;
    int m_picgoPort = 36677;
    bool m_startMinimized = false;
    int m_defaultThemeMode = 0;
    int m_autoSaveInterval = 500;
    bool m_rememberWindowGeometry = false;
    bool m_stickOnTopDefault = false;
    int m_windowX = -1;
    int m_windowY = -1;
    int m_windowWidth = 940;
    int m_windowHeight = 620;
    int m_listDensity = 1;
    int m_timeFormat = 1;
    int m_noteSortOrder = 0;
    bool m_confirmBeforeDelete = true;
    bool m_launchAtStartup = false;
    QString m_language;

    QString getValue(const QString &key, const QString &defaultValue = QString()) const;
    int getIntValue(const QString &key, int defaultValue = 0) const;
    void setValue(const QString &key, const QString &value);
    void syncLaunchAtStartupRegistry(bool enabled);
};
