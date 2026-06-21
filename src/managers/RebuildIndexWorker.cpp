#include "RebuildIndexWorker.h"

#include "database/NoteDatabase.h"
#include "managers/SearchManager.h"

#include <QDebug>

RebuildIndexWorker::RebuildIndexWorker(const QString &dbPath, const QString &tmpIndexPath, QObject *parent)
    : QObject(parent)
    , m_dbPath(dbPath)
    , m_tmpIndexPath(tmpIndexPath)
{
}

void RebuildIndexWorker::run()
{
    try {
        // 子线程独立创建 NoteDatabase（SQLite 连接绑定此线程，安全）
        NoteDatabase db(m_dbPath);
        if (!db.initSchema()) {
            qWarning() << "RebuildIndexWorker: initSchema failed for" << m_dbPath;
            emit finished(false);
            return;
        }
        QList<Note> notes = db.queryNotes();

        // 子线程独立创建 SearchManager，写入临时目录
        SearchManager tmpSearch;
        if (!tmpSearch.initialize(m_tmpIndexPath)) {
            qWarning() << "RebuildIndexWorker: initialize tmp index failed" << m_tmpIndexPath;
            emit finished(false);
            return;
        }
        for (const Note &note : notes) {
            tmpSearch.indexNote(note);
        }

        qDebug() << "RebuildIndexWorker: rebuilt index for" << notes.size() << "notes into" << m_tmpIndexPath;
        emit finished(true);
    } catch (const std::exception &e) {
        qWarning() << "RebuildIndexWorker failed:" << e.what();
        emit finished(false);
    }
}
