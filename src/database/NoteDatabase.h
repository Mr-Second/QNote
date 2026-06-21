#pragma once

#include "Constants.h"
#include "models/Note.h"

#include <QList>
#include <QString>

#include <SQLiteCpp/Database.h>

class NoteDatabase {
public:
    explicit NoteDatabase(const QString &dbPath);
    ~NoteDatabase();

    bool initSchema();

    Note insertNote(const Note &note);
    bool updateNote(const Note &note);
    bool updateNoteCategory(qint64 id, const QString &category);
    bool deleteNote(qint64 id);
    // 删除指定分类下的所有便签(分类删除时调用)
    bool deleteNotesByCategory(const QString &category);
    // 批量改便签的 category 字段(分类改名时调用)
    bool renameNotesCategory(const QString &oldName, const QString &newName);
    QList<Note> queryNotes(const QString &categoryDisplayName = QString(), int sortOrder = 0);
    Note getNoteById(qint64 id);

    QString dbPath() const { return m_dbPath; }
    void close();
    bool reopen(const QString &path = QString());

    // 备份/恢复支持
    QStringList queryAllUuids();
    bool importNotes(const QList<Note> &notes, int strategy);
    bool mergeCategories(const QList<QVariantMap> &categories);

    SQLite::Database &rawDb() { return *m_db; }

private:
    Note noteFromStatement(SQLite::Statement &query);
    void migrateUuids();
    std::unique_ptr<SQLite::Database> m_db;
    QString m_dbPath;
};
