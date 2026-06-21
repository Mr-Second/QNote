#pragma once

#include <QObject>
#include <QVariantMap>

#include <functional>

class NoteDatabase;
class SettingsManager;
class CategoryManager;
class BackupWorker;

class BackupManager : public QObject {
    Q_OBJECT
public:
    explicit BackupManager(QObject *parent = nullptr);

    void setNoteDatabase(NoteDatabase *db);
    void setSettingsManager(SettingsManager *manager);
    void setCategoryManager(CategoryManager *manager);

    Q_INVOKABLE QString sevenZipPath() const;
    Q_INVOKABLE bool sevenZipAvailable() const;
    Q_INVOKABLE QString createTempRestoreDir() const;

public slots:
    void createBackupAsync(const QString &targetPath, const QString &password = QString());
    void probeBackupAsync(const QString &filePath);
    void extractBackupAsync(const QString &filePath, const QString &password,
                            const QString &destDir);
    void analyzeConflictsAsync(const QString &tempDir);
    void applyRestoreAsync(int strategy, const QString &tempDir);

signals:
    void backupFinished(QVariantMap result);
    void probeFinished(QVariantMap result);
    void extractFinished(QVariantMap result);
    void analyzeFinished(QVariantMap result);
    void restoreFinished(QVariantMap result);

    void progress(int percent, const QString &message);
    void dbReopened();

private:
    QString _appDataDir() const;

    template <typename InvokeFn>
    void _runAsync(InvokeFn invoke, std::function<void(QVariantMap)> finalize);

    NoteDatabase *m_db = nullptr;
    SettingsManager *m_settings = nullptr;
    CategoryManager *m_categoryManager = nullptr;
};
