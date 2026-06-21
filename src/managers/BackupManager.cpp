#include "BackupManager.h"

#include "database/NoteDatabase.h"
#include "managers/SettingsManager.h"
#include "managers/CategoryManager.h"
#include "models/Note.h"
#include "Constants.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QProcess>
#include <QSet>
#include <QStandardPaths>
#include <QThread>

namespace {

// 7za -bsp1 输出形如 " 23% 5 - file.db"，返回百分比或 -1
int parseProgress(const QString &line) {
    QString trimmed = line.trimmed();
    int pctIdx = trimmed.indexOf('%');
    if (pctIdx > 0) {
        QString numPart = trimmed.left(pctIdx).trimmed();
        bool ok = false;
        int pct = numPart.toInt(&ok);
        if (ok && pct >= 0 && pct <= 100) return pct;
    }
    return -1;
}

void emitProgressList(QProcess &proc, std::function<void(int,QString)> emitFn, int base, int span) {
    QString out = proc.readAllStandardOutput();
    for (const QString &line : out.split('\n')) {
        int pct = parseProgress(line);
        if (pct >= 0) {
            emitFn(base + pct * span / 100, QString());
        }
    }
}

} // namespace

BackupManager::BackupManager(QObject *parent)
    : QObject(parent)
{
}

void BackupManager::setNoteDatabase(NoteDatabase *db) { m_db = db; }
void BackupManager::setSettingsManager(SettingsManager *manager) { m_settings = manager; }
void BackupManager::setCategoryManager(CategoryManager *manager) { m_categoryManager = manager; }

QString BackupManager::sevenZipPath() const
{
    return QCoreApplication::applicationDirPath() + QStringLiteral("/7za.exe");
}

bool BackupManager::sevenZipAvailable() const
{
    return QFile::exists(sevenZipPath());
}

QString BackupManager::createTempRestoreDir() const
{
    QString ts = QDateTime::currentDateTime().toString(QStringLiteral("yyyyMMddHHmmss"));
    return QStandardPaths::writableLocation(QStandardPaths::TempLocation)
           + QStringLiteral("/qnote-restore-") + ts;
}

QString BackupManager::_appDataDir() const
{
    return QStandardPaths::writableLocation(QStandardPaths::AppDataLocation);
}

// ============ 同步内部实现（在 worker 线程执行） ============

QVariantMap _doCreateBackup(const QString &targetPath, const QString &password,
                            const QString &dbPath, const QString &appDataDir,
                            std::function<void(int,QString)> emitProgress)
{
    QVariantMap result;
    result["success"] = false;
    result["path"] = targetPath;

    QFileInfo fi(targetPath);
    QDir().mkpath(fi.absolutePath());

    QString staging = QStandardPaths::writableLocation(QStandardPaths::TempLocation)
                      + QStringLiteral("/qnote-backup-staging");
    QDir(staging).removeRecursively();
    QDir().mkpath(staging);

    emitProgress(5, QStringLiteral("准备数据..."));

    QString stagingDbPath = staging + QStringLiteral("/qnote.db");
    if (!QFile::copy(dbPath, stagingDbPath)) {
        result["error"] = QStringLiteral("复制数据库失败");
        result["errorCode"] = QStringLiteral("COPY_FAIL");
        QDir(staging).removeRecursively();
        return result;
    }

    QString srcImages = appDataDir + QStringLiteral("/images");
    if (QDir(srcImages).exists()) {
        QString stagingImages = staging + QStringLiteral("/images");
        QDir().mkpath(stagingImages);
        for (const auto &img : QDir(srcImages).entryInfoList(QDir::Files)) {
            QFile::copy(img.absoluteFilePath(), stagingImages + "/" + img.fileName());
        }
    }

    {
        QJsonObject obj;
#ifdef QNOTE_VERSION
        obj["appVersion"] = QString::fromUtf8(QNOTE_VERSION);
#else
        obj["appVersion"] = QStringLiteral("unknown");
#endif
        obj["createdAt"] = QDateTime::currentDateTime().toString(Qt::ISODateWithMs);
        obj["encrypted"] = !password.isEmpty();
        QFile f(staging + QStringLiteral("/manifest.json"));
        if (f.open(QIODevice::WriteOnly)) {
            f.write(QJsonDocument(obj).toJson(QJsonDocument::Indented));
        }
    }

    emitProgress(15, QStringLiteral("正在压缩..."));
    QFile::remove(targetPath);

    QProcess proc;
    QStringList args;
    args << QStringLiteral("a") << QStringLiteral("-t7z") << QStringLiteral("-mx=5")
         << QStringLiteral("-y") << QStringLiteral("-bso0") << QStringLiteral("-bsp1")
         << QStringLiteral("-bb0") << targetPath
         << (staging + QStringLiteral("/*"));
    if (!password.isEmpty()) {
        args << QStringLiteral("-p%1").arg(password) << QStringLiteral("-mhe=on");
    }
    proc.setProgram(QCoreApplication::applicationDirPath() + QStringLiteral("/7za.exe"));
    proc.setArguments(args);

    QObject::connect(&proc, &QProcess::readyReadStandardOutput, [&]() {
        emitProgressList(proc, emitProgress, 15, 80);
    });

    proc.start();
    if (!proc.waitForStarted()) {
        result["error"] = QStringLiteral("无法启动 7za.exe");
        result["errorCode"] = QStringLiteral("NO_7ZA");
        QDir(staging).removeRecursively();
        return result;
    }
    if (!proc.waitForFinished(300000)) {
        result["error"] = QStringLiteral("7z 压缩超时");
        result["errorCode"] = QStringLiteral("TIMEOUT");
        QDir(staging).removeRecursively();
        return result;
    }

    QDir(staging).removeRecursively();

    if (proc.exitCode() != 0) {
        result["error"] = QStringLiteral("7z 压缩失败 (exit=%1)").arg(proc.exitCode());
        result["errorCode"] = QStringLiteral("COMPRESS_FAIL");
        result["detail"] = proc.readAllStandardError();
        return result;
    }

    emitProgress(100, QStringLiteral("备份完成"));
    result["success"] = true;
    return result;
}

QVariantMap _doProbe(const QString &filePath)
{
    QVariantMap result;
    result["success"] = false;

    QProcess proc;
    proc.setProgram(QCoreApplication::applicationDirPath() + QStringLiteral("/7za.exe"));
    // 强制带空密码：避免加密文件时 7za 卡在 stdin
    proc.setArguments({ QStringLiteral("l"), QStringLiteral("-y"), QStringLiteral("-p"), filePath });
    proc.start();
    proc.closeWriteChannel();  // 关闭 stdin，防止 7za 等待密码输入
    proc.waitForFinished(30000);
    int code = proc.exitCode();
    QString combined = proc.readAllStandardError() + proc.readAllStandardOutput();

    if (code == 0) {
        result["success"] = true;
        result["encrypted"] = false;
    } else {
        QString lower = combined.toLower();
        if (lower.contains(QStringLiteral("wrong password"))
            || lower.contains(QStringLiteral("cannot open encrypted"))) {
            result["success"] = true;
            result["encrypted"] = true;
        } else {
            result["error"] = QStringLiteral("无法读取备份文件 (exit=%1)").arg(code);
            result["errorCode"] = QStringLiteral("CORRUPT");
            result["detail"] = combined;
        }
    }
    return result;
}

QVariantMap _doExtract(const QString &filePath, const QString &password, const QString &destDir,
                       std::function<void(int,QString)> emitProgress)
{
    QVariantMap result;
    result["success"] = false;

    QDir().mkpath(destDir);
    QDir(destDir).removeRecursively();
    QDir().mkpath(destDir);

    emitProgress(5, QStringLiteral("正在解压..."));

    QProcess proc;
    QStringList args;
    args << QStringLiteral("x") << QStringLiteral("-y")
         << QStringLiteral("-bso0") << QStringLiteral("-bsp1") << QStringLiteral("-bb0")
         << QStringLiteral("-o%1").arg(destDir) << filePath;
    if (!password.isEmpty()) args << QStringLiteral("-p%1").arg(password);
    else args << QStringLiteral("-p");
    proc.setProgram(QCoreApplication::applicationDirPath() + QStringLiteral("/7za.exe"));
    proc.setArguments(args);

    QObject::connect(&proc, &QProcess::readyReadStandardOutput, [&]() {
        emitProgressList(proc, emitProgress, 5, 85);
    });

    proc.start();
    if (!proc.waitForFinished(300000)) {
        result["error"] = QStringLiteral("解压超时");
        result["errorCode"] = QStringLiteral("TIMEOUT");
        return result;
    }

    int code = proc.exitCode();
    QString combined = proc.readAllStandardError() + proc.readAllStandardOutput();

    if (code != 0) {
        QString lower = combined.toLower();
        if (lower.contains(QStringLiteral("wrong password"))) {
            result["error"] = QStringLiteral("密码错误");
            result["errorCode"] = QStringLiteral("WRONG_PASSWORD");
        } else {
            result["error"] = QStringLiteral("解压失败 (exit=%1)").arg(code);
            result["errorCode"] = QStringLiteral("EXTRACT_FAIL");
            result["detail"] = combined;
        }
        return result;
    }

    if (!QFile::exists(destDir + QStringLiteral("/qnote.db"))) {
        result["error"] = QStringLiteral("备份文件格式不正确：缺少 qnote.db");
        result["errorCode"] = QStringLiteral("INVALID_FORMAT");
        return result;
    }

    emitProgress(100, QStringLiteral("解压完成"));
    result["success"] = true;
    result["destDir"] = destDir;
    return result;
}

QVariantMap _doAnalyzeConflicts(const QString &tempDir, const QString &currentDbPath,
                                std::function<void(int,QString)> emitProgress)
{
    QVariantMap result;
    result["success"] = false;

    QString tempDbPath = tempDir + QStringLiteral("/qnote.db");
    if (!QFile::exists(tempDbPath)) {
        result["error"] = QStringLiteral("临时目录无 qnote.db");
        result["errorCode"] = QStringLiteral("NO_TEMP_DB");
        return result;
    }

    emitProgress(30, QStringLiteral("分析冲突..."));

    QSet<QString> tempUuids;
    QSet<QString> currentUuids;
    try {
        SQLite::Database tempDb(tempDbPath.toUtf8().toStdString(), SQLite::OPEN_READONLY);
        SQLite::Statement q(tempDb, "SELECT uuid FROM notes WHERE uuid IS NOT NULL AND uuid != ''");
        while (q.executeStep()) {
            tempUuids.insert(QString::fromStdString(q.getColumn(0).getString()));
        }
    } catch (const std::exception &e) {
        result["error"] = QStringLiteral("读取备份 DB 失败：%1").arg(e.what());
        result["errorCode"] = QStringLiteral("TEMP_DB_FAIL");
        return result;
    }

    emitProgress(70, QStringLiteral("比对当前数据..."));
    try {
        SQLite::Database curDb(currentDbPath.toUtf8().toStdString(), SQLite::OPEN_READONLY);
        SQLite::Statement q(curDb, "SELECT uuid FROM notes WHERE uuid IS NOT NULL AND uuid != ''");
        while (q.executeStep()) {
            currentUuids.insert(QString::fromStdString(q.getColumn(0).getString()));
        }
    } catch (const std::exception &) {
        // 当前 DB 可能被占用，忽略
    }

    int conflict = 0, fresh = 0;
    for (const QString &u : tempUuids) {
        if (currentUuids.contains(u)) conflict++;
        else fresh++;
    }
    int currentOnly = currentUuids.count() - conflict;

    emitProgress(100, QStringLiteral("分析完成"));
    result["success"] = true;
    result["conflict"] = conflict;
    result["new"] = fresh;
    result["currentOnly"] = currentOnly;
    result["total"] = tempUuids.size();
    return result;
}

// 覆盖恢复：DB 文件替换 + 图片替换（主 DB 已由主线程关闭）
QVariantMap _doApplyRestoreOverwrite(const QString &tempDir, const QString &dbPath,
                                     const QString &appDataDir,
                                     std::function<void(int,QString)> emitProgress)
{
    QVariantMap result;
    result["success"] = false;

    QString tempDbPath = tempDir + QStringLiteral("/qnote.db");
    if (!QFile::exists(tempDbPath)) {
        result["error"] = QStringLiteral("临时目录无 qnote.db");
        result["errorCode"] = QStringLiteral("NO_TEMP_DB");
        return result;
    }

    emitProgress(5, QStringLiteral("恢复前自动备份..."));
    QString autoBackupDir = appDataDir + QStringLiteral("/auto-backup");
    QDir().mkpath(autoBackupDir);
    QString ts = QDateTime::currentDateTime().toString(QStringLiteral("yyyyMMdd-HHmmss"));
    QString autoPath = autoBackupDir + QStringLiteral("/pre-restore-") + ts + QStringLiteral(".7z");

    QVariantMap preResult = _doCreateBackup(autoPath, QString(), dbPath, appDataDir, [](int, QString){});
    if (!preResult.value("success").toBool()) {
        result["error"] = QStringLiteral("恢复前自动备份失败：%1").arg(preResult.value("error").toString());
        result["errorCode"] = QStringLiteral("PRE_BACKUP_FAIL");
        return result;
    }
    result["autoBackupPath"] = autoPath;

    emitProgress(60, QStringLiteral("替换数据库..."));
    QFile::remove(dbPath);
    if (!QFile::copy(tempDbPath, dbPath)) {
        result["error"] = QStringLiteral("覆盖 DB 文件失败");
        result["errorCode"] = QStringLiteral("COPY_FAIL");
        return result;
    }

    QString currentImages = appDataDir + QStringLiteral("/images");
    QDir(currentImages).removeRecursively();
    QString tempImages = tempDir + QStringLiteral("/images");
    if (QDir(tempImages).exists()) {
        QDir().mkpath(currentImages);
        for (const auto &img : QDir(tempImages).entryInfoList(QDir::Files)) {
            QFile::copy(img.absoluteFilePath(), currentImages + "/" + img.fileName());
        }
    }

    emitProgress(95, QStringLiteral("完成"));
    result["success"] = true;
    return result;
}

// 合并/仅导入新增：读取备份 notes/categories 回传给主线程执行 DB 写入
QVariantMap _doApplyRestoreMerge(const QString &tempDir, int strategy,
                                 std::function<void(int,QString)> emitProgress)
{
    QVariantMap result;
    result["success"] = false;

    QString tempDbPath = tempDir + QStringLiteral("/qnote.db");
    if (!QFile::exists(tempDbPath)) {
        result["error"] = QStringLiteral("临时目录无 qnote.db");
        result["errorCode"] = QStringLiteral("NO_TEMP_DB");
        return result;
    }

    emitProgress(20, QStringLiteral("读取备份数据..."));

    QVariantList notesVar;
    QVariantList catsVar;
    try {
        SQLite::Database tempDb(tempDbPath.toUtf8().toStdString(), SQLite::OPEN_READONLY);
        SQLite::Statement q(tempDb,
            "SELECT uuid, title, content, category, createdAt, updatedAt, titleManual, sortOrder "
            "FROM notes WHERE uuid IS NOT NULL AND uuid != ''");
        while (q.executeStep()) {
            QVariantMap m;
            m["uuid"] = QString::fromStdString(q.getColumn(0).getString());
            m["title"] = QString::fromStdString(q.getColumn(1).getString());
            m["content"] = QString::fromStdString(q.getColumn(2).getString());
            m["category"] = QString::fromStdString(q.getColumn(3).getString());
            m["createdAt"] = QString::fromStdString(q.getColumn(4).getString());
            m["updatedAt"] = QString::fromStdString(q.getColumn(5).getString());
            m["titleManual"] = q.getColumn(6).getInt() != 0;
            m["sortOrder"] = q.getColumn(7).getInt();
            notesVar.append(m);
        }

        SQLite::Statement cq(tempDb, "SELECT name, color, sortOrder, createdAt FROM categories");
        while (cq.executeStep()) {
            QVariantMap m;
            m["name"] = QString::fromStdString(cq.getColumn(0).getString());
            m["color"] = QString::fromStdString(cq.getColumn(1).getString());
            m["sortOrder"] = cq.getColumn(2).getInt();
            m["createdAt"] = QString::fromStdString(cq.getColumn(3).getString());
            catsVar.append(m);
        }
    } catch (const std::exception &e) {
        result["error"] = QStringLiteral("读取备份 DB 失败：%1").arg(e.what());
        result["errorCode"] = QStringLiteral("TEMP_DB_FAIL");
        return result;
    }

    emitProgress(60, QStringLiteral("等待导入..."));
    result["success"] = true;
    result["strategy"] = strategy;
    result["notes"] = notesVar;
    result["categories"] = catsVar;
    return result;
}

// ============ Worker 对象（在工作线程中调用同步实现） ============

class BackupWorker : public QObject {
public:
    QString dbPath;
    QString appDataDir;
    std::function<void(int,QString)> progressFn;
    std::function<void(QVariantMap)> doneFn;

public:
    // 在工作线程被 invokeMethod 调用，需 Q_INVOKABLE 或 slot
};


// ============ 异步调度 ============

template <typename InvokeFn>
void BackupManager::_runAsync(InvokeFn invoke, std::function<void(QVariantMap)> finalize)
{
    QThread *thread = new QThread;
    BackupWorker *worker = new BackupWorker;
    worker->dbPath = m_db->dbPath();
    worker->appDataDir = _appDataDir();
    worker->progressFn = [this](int pct, QString msg) { emit progress(pct, msg); };
    worker->doneFn = [this, thread, worker, finalize](QVariantMap r) {
        // 切回主线程执行 finalize
        QMetaObject::invokeMethod(this, [this, r, thread, worker, finalize]() {
            finalize(r);
            thread->quit();
        }, Qt::QueuedConnection);
    };
    worker->moveToThread(thread);

    QObject::connect(thread, &QThread::finished, worker, &QObject::deleteLater);
    QObject::connect(thread, &QThread::finished, thread, &QObject::deleteLater);

    thread->start();
    invoke(worker);
}

void BackupManager::createBackupAsync(const QString &targetPath, const QString &password)
{
    _runAsync([targetPath, password](BackupWorker *w) {
        QMetaObject::invokeMethod(QCoreApplication::instance(), [w, targetPath, password]() {
            QVariantMap r = _doCreateBackup(targetPath, password, w->dbPath, w->appDataDir, w->progressFn);
            w->doneFn(r);
        }, Qt::QueuedConnection);
    },
    [this](QVariantMap r) { emit backupFinished(r); });
}

void BackupManager::probeBackupAsync(const QString &filePath)
{
    _runAsync([filePath](BackupWorker *w) {
        QMetaObject::invokeMethod(QCoreApplication::instance(), [w, filePath]() {
            QVariantMap r = _doProbe(filePath);
            w->doneFn(r);
        }, Qt::QueuedConnection);
    },
    [this](QVariantMap r) { emit probeFinished(r); });
}

void BackupManager::extractBackupAsync(const QString &filePath, const QString &password, const QString &destDir)
{
    _runAsync([filePath, password, destDir](BackupWorker *w) {
        QMetaObject::invokeMethod(QCoreApplication::instance(), [w, filePath, password, destDir]() {
            QVariantMap r = _doExtract(filePath, password, destDir, w->progressFn);
            w->doneFn(r);
        }, Qt::QueuedConnection);
    },
    [this](QVariantMap r) { emit extractFinished(r); });
}

void BackupManager::analyzeConflictsAsync(const QString &tempDir)
{
    _runAsync([tempDir](BackupWorker *w) {
        QMetaObject::invokeMethod(QCoreApplication::instance(), [w, tempDir]() {
            QVariantMap r = _doAnalyzeConflicts(tempDir, w->dbPath, w->progressFn);
            w->doneFn(r);
        }, Qt::QueuedConnection);
    },
    [this](QVariantMap r) { emit analyzeFinished(r); });
}

void BackupManager::applyRestoreAsync(int strategy, const QString &tempDir)
{
    if (strategy == 0) {
        m_db->close();
        _runAsync([tempDir](BackupWorker *w) {
            QMetaObject::invokeMethod(QCoreApplication::instance(), [w, tempDir]() {
                QVariantMap r = _doApplyRestoreOverwrite(tempDir, w->dbPath, w->appDataDir, w->progressFn);
                w->doneFn(r);
            }, Qt::QueuedConnection);
        },
        [this](QVariantMap r) {
            if (r.value("success").toBool()) {
                if (m_db->reopen()) {
                    emit dbReopened();
                } else {
                    r["success"] = false;
                    r["error"] = QStringLiteral("重新打开数据库失败");
                    r["errorCode"] = QStringLiteral("REOPEN_FAIL");
                }
            }
            emit restoreFinished(r);
        });
    } else {
        _runAsync([tempDir, strategy](BackupWorker *w) {
            QMetaObject::invokeMethod(QCoreApplication::instance(), [w, tempDir, strategy]() {
                QVariantMap r = _doApplyRestoreMerge(tempDir, strategy, w->progressFn);
                w->doneFn(r);
            }, Qt::QueuedConnection);
        },
        [this, strategy](QVariantMap r) {
            if (!r.value("success").toBool()) {
                emit restoreFinished(r);
                return;
            }
            QVariantList notesVar = r.value("notes").toList();
            QVariantList catsVar = r.value("categories").toList();

            QList<Note> notes;
            for (const QVariant &v : notesVar) {
                QVariantMap m = v.toMap();
                Note n;
                n.uuid = m.value("uuid").toString();
                n.title = m.value("title").toString();
                n.content = m.value("content").toString();
                n.category = m.value("category").toString();
                n.createdAt = QDateTime::fromString(m.value("createdAt").toString(), Qt::ISODateWithMs);
                n.updatedAt = QDateTime::fromString(m.value("updatedAt").toString(), Qt::ISODateWithMs);
                n.titleManual = m.value("titleManual").toBool();
                n.sortOrder = m.value("sortOrder").toInt();
                notes.append(n);
            }
            QList<QVariantMap> cats;
            for (const QVariant &v : catsVar) cats.append(v.toMap());

            if (!m_db->importNotes(notes, strategy)) {
                r["success"] = false;
                r["error"] = QStringLiteral("导入便签失败");
                r["errorCode"] = QStringLiteral("IMPORT_FAIL");
            } else {
                m_db->mergeCategories(cats);
            }
            r.remove("notes");
            r.remove("categories");
            r.remove("strategy");
            emit restoreFinished(r);
        });
    }
}
