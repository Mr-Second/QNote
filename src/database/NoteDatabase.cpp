#include "NoteDatabase.h"

#include "Constants.h"

#include <QDebug>
#include <QDateTime>
#include <QUuid>
#include <QVariantMap>

#include <SQLiteCpp/Statement.h>

NoteDatabase::NoteDatabase(const QString &dbPath)
    : m_dbPath(dbPath)
{
    try {
        m_db = std::make_unique<SQLite::Database>(
            dbPath.toUtf8().toStdString(), SQLite::OPEN_READWRITE | SQLite::OPEN_CREATE);
    } catch (const std::exception &e) {
        qWarning() << "Failed to open database:" << dbPath << e.what();
        m_db.reset();
    }
}

NoteDatabase::~NoteDatabase() = default;

bool NoteDatabase::initSchema()
{
    if (!m_db) return false;

    try {
        m_db->exec(
            "CREATE TABLE IF NOT EXISTS notes ("
            "  id          INTEGER PRIMARY KEY AUTOINCREMENT,"
            "  title       TEXT    NOT NULL DEFAULT '',"
            "  content     TEXT    NOT NULL DEFAULT '',"
            "  category    TEXT    NOT NULL DEFAULT '工作',"
            "  createdAt   TEXT    NOT NULL,"
            "  updatedAt   TEXT    NOT NULL,"
            "  titleManual INTEGER NOT NULL DEFAULT 0,"
            "  sortOrder   INTEGER NOT NULL DEFAULT 0,"
            "  uuid        TEXT"
            ")");

        // 老库兼容：列不存在则添加（SQLite ALTER TABLE ADD COLUMN 失败时抛异常）
        try {
            m_db->exec("ALTER TABLE notes ADD COLUMN uuid TEXT");
        } catch (const std::exception &) {
            // 列已存在，忽略
        }

        m_db->exec(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_uuid ON notes(uuid) WHERE uuid IS NOT NULL");
        m_db->exec(
            "CREATE TABLE IF NOT EXISTS categories ("
            "  id        INTEGER PRIMARY KEY AUTOINCREMENT,"
            "  name      TEXT    NOT NULL UNIQUE,"
            "  color     TEXT    NOT NULL,"
            "  iconKey   TEXT    NOT NULL DEFAULT '',"
            "  sortOrder INTEGER NOT NULL DEFAULT 0,"
            "  createdAt TEXT    NOT NULL"
            ")");

        // 老库兼容：categories 表加 iconKey 列
        try {
            m_db->exec("ALTER TABLE categories ADD COLUMN iconKey TEXT NOT NULL DEFAULT ''");
        } catch (const std::exception &) {
            // 列已存在，忽略
        }

        // 老库兼容：categories 表加 key 列（内置分类的逻辑标识，如 work/life/important）
        try {
            m_db->exec("ALTER TABLE categories ADD COLUMN key TEXT");
            // 回填内置分类的 key（按中文显示名识别；用户改过名的不回填，保留为自定义分类）
            {
                SQLite::Statement upd(*m_db, "UPDATE categories SET key = ? WHERE name = ? AND (key IS NULL OR key = '')");
                upd.bind(1, Constants::workCategoryKey().toStdString());
                upd.bind(2, QStringLiteral("工作").toStdString());
                upd.exec();
                upd.reset();
                upd.bind(1, Constants::lifeCategoryKey().toStdString());
                upd.bind(2, QStringLiteral("生活").toStdString());
                upd.exec();
                upd.reset();
                upd.bind(1, Constants::importantCategoryKey().toStdString());
                upd.bind(2, QStringLiteral("重要").toStdString());
                upd.exec();
            }
        } catch (const std::exception &) {
            // 列已存在，忽略
        }

        m_db->exec(
            "CREATE TABLE IF NOT EXISTS settings ("
            "  key   TEXT PRIMARY KEY NOT NULL,"
            "  value TEXT NOT NULL"
            ")");

        {
            SQLite::Statement check(*m_db, "SELECT COUNT(*) FROM categories");
            if (check.executeStep() && check.getColumn(0).getInt() == 0) {
                auto now = QDateTime::currentDateTime().toString(Qt::ISODateWithMs);
                SQLite::Statement insert(*m_db,
                    "INSERT INTO categories (name, color, sortOrder, createdAt, key) VALUES (?, ?, ?, ?, ?)");
                insert.bind(1, Constants::workCategory().toStdString());
                insert.bind(2, Constants::colorWork().toStdString());
                insert.bind(3, 1);
                insert.bind(4, now.toStdString());
                insert.bind(5, Constants::workCategoryKey().toStdString());
                insert.exec();

                insert.reset();
                insert.bind(1, Constants::lifeCategory().toStdString());
                insert.bind(2, Constants::colorLife().toStdString());
                insert.bind(3, 2);
                insert.bind(4, now.toStdString());
                insert.bind(5, Constants::lifeCategoryKey().toStdString());
                insert.exec();

                insert.reset();
                insert.bind(1, Constants::importantCategory().toStdString());
                insert.bind(2, Constants::colorImportant().toStdString());
                insert.bind(3, 3);
                insert.bind(4, now.toStdString());
                insert.bind(5, Constants::importantCategoryKey().toStdString());
                insert.exec();
            }
        }

        migrateUuids();

        return true;
    } catch (const std::exception &e) {
        qWarning() << "initSchema failed:" << e.what();
        return false;
    }
}

Note NoteDatabase::insertNote(const Note &note)
{
    if (!m_db) return Note{};

    try {
        auto now = QDateTime::currentDateTime().toString(Qt::ISODateWithMs);
        QString title = note.title.isEmpty() ? Note::generateTitle(note.content) : note.title;
        QString category = note.category.isEmpty()
            ? Constants::workCategory()
            : note.category;
        QString uuid = note.uuid.isEmpty()
            ? QUuid::createUuid().toString(QUuid::WithoutBraces)
            : note.uuid;

        int maxSortOrder = 0;
        {
            SQLite::Statement maxQuery(*m_db, "SELECT COALESCE(MAX(sortOrder), 0) FROM notes");
            if (maxQuery.executeStep())
                maxSortOrder = maxQuery.getColumn(0).getInt();
        }

        SQLite::Statement query(*m_db,
            "INSERT INTO notes (title, content, category, createdAt, updatedAt, titleManual, sortOrder, uuid) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)");
        query.bind(1, title.toStdString());
        query.bind(2, note.content.toStdString());
        query.bind(3, category.toStdString());
        query.bind(4, now.toStdString());
        query.bind(5, now.toStdString());
        query.bind(6, note.titleManual ? 1 : 0);
        query.bind(7, maxSortOrder + 1);
        query.bind(8, uuid.toStdString());
        query.exec();

        Note result = note;
        result.id = static_cast<qint64>(m_db->getLastInsertRowid());
        result.title = title;
        result.category = category;
        result.uuid = uuid;
        result.createdAt = QDateTime::fromString(now, Qt::ISODateWithMs);
        result.updatedAt = result.createdAt;
        result.sortOrder = maxSortOrder + 1;
        return result;
    } catch (const std::exception &e) {
        qWarning() << "insertNote failed:" << e.what();
        return Note{};
    }
}

bool NoteDatabase::updateNote(const Note &note)
{
    if (!m_db) return false;

    try {
        auto now = QDateTime::currentDateTime().toString(Qt::ISODateWithMs);

        QString title = note.title;
        bool titleManual = note.titleManual;
        DBG("DB updateNote: titleManual=" << titleManual << " note.title=" << note.title);
        if (!titleManual)
            title = Note::generateTitle(note.content);
        DBG("DB updateNote: final title=" << title);

        SQLite::Statement query(*m_db,
            "UPDATE notes SET title = ?, content = ?, updatedAt = ?, titleManual = ? WHERE id = ?");
        query.bind(1, title.toStdString());
        query.bind(2, note.content.toStdString());
        query.bind(3, now.toStdString());
        query.bind(4, titleManual ? 1 : 0);
        query.bind(5, note.id);
        query.executeStep();

        return m_db->getChanges() > 0;
    } catch (const std::exception &e) {
        qWarning() << "updateNote failed:" << e.what();
        return false;
    }
}

bool NoteDatabase::updateNoteCategory(qint64 id, const QString &category)
{
    if (!m_db) return false;

    try {
        SQLite::Statement query(*m_db, "UPDATE notes SET category = ? WHERE id = ?");
        query.bind(1, category.toStdString());
        query.bind(2, id);
        query.executeStep();
        return m_db->getChanges() > 0;
    } catch (const std::exception &e) {
        qWarning() << "updateNoteCategory failed:" << e.what();
        return false;
    }
}

bool NoteDatabase::deleteNote(qint64 id)
{
    if (!m_db) return false;

    try {
        SQLite::Statement query(*m_db, "DELETE FROM notes WHERE id = ?");
        query.bind(1, id);
        query.executeStep();
        return m_db->getChanges() > 0;
    } catch (const std::exception &e) {
        qWarning() << "deleteNote failed:" << e.what();
        return false;
    }
}

bool NoteDatabase::deleteNotesByCategory(const QString &category)
{
    if (!m_db) return false;

    try {
        SQLite::Statement query(*m_db, "DELETE FROM notes WHERE category = ?");
        query.bind(1, category.toStdString());
        query.executeStep();
        return m_db->getChanges() > 0;
    } catch (const std::exception &e) {
        qWarning() << "deleteNotesByCategory failed:" << e.what();
        return false;
    }
}

bool NoteDatabase::renameNotesCategory(const QString &oldName, const QString &newName)
{
    if (!m_db) return false;

    try {
        SQLite::Statement query(*m_db, "UPDATE notes SET category = ? WHERE category = ?");
        query.bind(1, newName.toStdString());
        query.bind(2, oldName.toStdString());
        query.executeStep();
        return m_db->getChanges() > 0;
    } catch (const std::exception &e) {
        qWarning() << "renameNotesCategory failed:" << e.what();
        return false;
    }
}

QList<Note> NoteDatabase::queryNotes(const QString &categoryDisplayName, int sortOrder)
{
    QList<Note> notes;
    if (!m_db) return notes;

    // sortOrder: 0=更新时间 DESC, 1=创建时间 DESC, 2=标题 ASC
    QString orderClause = QStringLiteral("updatedAt DESC");
    if (sortOrder == 1) orderClause = QStringLiteral("createdAt DESC");
    else if (sortOrder == 2) orderClause = QStringLiteral("title ASC");

    try {
        std::unique_ptr<SQLite::Statement> query;

        // 空分类名表示"全部"（不过滤），兼容历史调用方
        if (categoryDisplayName.isEmpty()) {
            query = std::make_unique<SQLite::Statement>(*m_db,
                ("SELECT id, uuid, title, content, category, createdAt, updatedAt, titleManual, sortOrder "
                 "FROM notes ORDER BY " + orderClause).toStdString());
        } else {
            query = std::make_unique<SQLite::Statement>(*m_db,
                ("SELECT id, uuid, title, content, category, createdAt, updatedAt, titleManual, sortOrder "
                 "FROM notes WHERE category = ? ORDER BY " + orderClause).toStdString());
            query->bind(1, categoryDisplayName.toStdString());
        }

        while (query->executeStep()) {
            notes.append(noteFromStatement(*query));
        }
    } catch (const std::exception &e) {
        qWarning() << "queryNotes failed:" << e.what();
    }

    return notes;
}

Note NoteDatabase::getNoteById(qint64 id)
{
    Note note;
    if (!m_db) return note;

    try {
        SQLite::Statement query(*m_db,
            "SELECT id, uuid, title, content, category, createdAt, updatedAt, titleManual, sortOrder "
            "FROM notes WHERE id = ?");
        query.bind(1, id);
        if (query.executeStep()) {
            note = noteFromStatement(query);
        }
    } catch (const std::exception &e) {
        qWarning() << "getNoteById failed:" << e.what();
    }

    return note;
}

Note NoteDatabase::noteFromStatement(SQLite::Statement &query)
{
    Note note;
    note.id = query.getColumn(0).getInt64();
    note.uuid = QString::fromStdString(query.getColumn(1).getString());
    note.title = QString::fromStdString(query.getColumn(2).getString());
    note.content = QString::fromStdString(query.getColumn(3).getString());
    note.category = QString::fromStdString(query.getColumn(4).getString());
    note.createdAt = QDateTime::fromString(
        QString::fromStdString(query.getColumn(5).getString()), Qt::ISODateWithMs);
    note.updatedAt = QDateTime::fromString(
        QString::fromStdString(query.getColumn(6).getString()), Qt::ISODateWithMs);
    note.titleManual = query.getColumn(7).getInt() != 0;
    note.sortOrder = query.getColumn(8).getInt();
    return note;
}

void NoteDatabase::migrateUuids()
{
    if (!m_db) return;

    try {
        SQLite::Statement query(*m_db, "SELECT id FROM notes WHERE uuid IS NULL OR uuid = ''");
        QList<qint64> ids;
        while (query.executeStep()) {
            ids.append(query.getColumn(0).getInt64());
        }
        if (ids.isEmpty()) return;

        SQLite::Statement update(*m_db, "UPDATE notes SET uuid = ? WHERE id = ?");
        m_db->exec("BEGIN");
        for (qint64 id : ids) {
            QString uuid = QUuid::createUuid().toString(QUuid::WithoutBraces);
            update.bind(1, uuid.toStdString());
            update.bind(2, id);
            update.exec();
            update.reset();
        }
        m_db->exec("COMMIT");
        qDebug() << "migrateUuids: filled" << ids.size() << "notes with uuid";
    } catch (const std::exception &e) {
        qWarning() << "migrateUuids failed:" << e.what();
        try { m_db->exec("ROLLBACK"); } catch (...) {}
    }
}

void NoteDatabase::close()
{
    m_db.reset();
}

bool NoteDatabase::reopen(const QString &path)
{
    QString openPath = path.isEmpty() ? m_dbPath : path;
    if (!path.isEmpty()) m_dbPath = path;
    try {
        m_db = std::make_unique<SQLite::Database>(
            openPath.toUtf8().toStdString(), SQLite::OPEN_READWRITE | SQLite::OPEN_CREATE);
        return initSchema();
    } catch (const std::exception &e) {
        qWarning() << "reopen failed:" << openPath << e.what();
        m_db.reset();
        return false;
    }
}

QStringList NoteDatabase::queryAllUuids()
{
    QStringList uuids;
    if (!m_db) return uuids;

    try {
        SQLite::Statement query(*m_db, "SELECT uuid FROM notes WHERE uuid IS NOT NULL AND uuid != ''");
        while (query.executeStep()) {
            uuids.append(QString::fromStdString(query.getColumn(0).getString()));
        }
    } catch (const std::exception &e) {
        qWarning() << "queryAllUuids failed:" << e.what();
    }
    return uuids;
}

bool NoteDatabase::importNotes(const QList<Note> &notes, int strategy)
{
    // strategy: 1=合并(uuid 冲突留较新 updatedAt), 2=仅导入新增(uuid 已存在跳过)
    if (!m_db) return false;

    try {
        m_db->exec("BEGIN");

        SQLite::Statement findExisting(*m_db,
            "SELECT id, updatedAt FROM notes WHERE uuid = ?");
        SQLite::Statement insertNote(*m_db,
            "INSERT INTO notes (title, content, category, createdAt, updatedAt, titleManual, sortOrder, uuid) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)");
        SQLite::Statement updateNote(*m_db,
            "UPDATE notes SET title = ?, content = ?, category = ?, updatedAt = ?, titleManual = ? WHERE id = ?");

        int imported = 0, skipped = 0, updated = 0;

        for (const Note &note : notes) {
            if (note.uuid.isEmpty()) {
                skipped++;
                continue;
            }

            findExisting.bind(1, note.uuid.toStdString());
            bool exists = findExisting.executeStep();

            if (!exists) {
                // 新增
                insertNote.bind(1, note.title.toStdString());
                insertNote.bind(2, note.content.toStdString());
                insertNote.bind(3, note.category.toStdString());
                insertNote.bind(4, note.createdAt.toString(Qt::ISODateWithMs).toStdString());
                insertNote.bind(5, note.updatedAt.toString(Qt::ISODateWithMs).toStdString());
                insertNote.bind(6, note.titleManual ? 1 : 0);
                insertNote.bind(7, note.sortOrder);
                insertNote.bind(8, note.uuid.toStdString());
                insertNote.exec();
                insertNote.reset();
                imported++;
            } else if (strategy == 1) {
                // 合并：比较 updatedAt
                qint64 existingId = findExisting.getColumn(0).getInt64();
                QDateTime existingUpdated = QDateTime::fromString(
                    QString::fromStdString(findExisting.getColumn(1).getString()), Qt::ISODateWithMs);
                if (note.updatedAt > existingUpdated) {
                    updateNote.bind(1, note.title.toStdString());
                    updateNote.bind(2, note.content.toStdString());
                    updateNote.bind(3, note.category.toStdString());
                    updateNote.bind(4, note.updatedAt.toString(Qt::ISODateWithMs).toStdString());
                    updateNote.bind(5, note.titleManual ? 1 : 0);
                    updateNote.bind(6, existingId);
                    updateNote.exec();
                    updateNote.reset();
                    updated++;
                } else {
                    skipped++;
                }
            } else {
                // strategy==2 仅导入新增：已存在跳过
                skipped++;
            }

            findExisting.reset();
        }

        m_db->exec("COMMIT");
        qDebug() << "importNotes: imported=" << imported << "updated=" << updated << "skipped=" << skipped;
        return true;
    } catch (const std::exception &e) {
        qWarning() << "importNotes failed:" << e.what();
        try { m_db->exec("ROLLBACK"); } catch (...) {}
        return false;
    }
}

bool NoteDatabase::mergeCategories(const QList<QVariantMap> &categories)
{
    if (!m_db) return false;

    try {
        m_db->exec("BEGIN");
        SQLite::Statement findExisting(*m_db, "SELECT COUNT(*) FROM categories WHERE name = ?");
        SQLite::Statement insert(*m_db,
            "INSERT INTO categories (name, color, iconKey, sortOrder, createdAt) VALUES (?, ?, ?, ?, ?)");

        int imported = 0;
        for (const QVariantMap &cat : categories) {
            QString name = cat.value("name").toString();
            if (name.isEmpty()) continue;

            findExisting.bind(1, name.toStdString());
            findExisting.executeStep();
            int count = findExisting.getColumn(0).getInt();
            findExisting.reset();

            if (count == 0) {
                insert.bind(1, name.toStdString());
                insert.bind(2, cat.value("color").toString().toStdString());
                insert.bind(3, cat.value("icon", "").toString().toStdString());
                insert.bind(4, cat.value("sortOrder", 0).toInt());
                insert.bind(5, cat.value("createdAt").toString().toStdString());
                insert.exec();
                insert.reset();
                imported++;
            }
        }

        m_db->exec("COMMIT");
        qDebug() << "mergeCategories: imported" << imported << "categories";
        return true;
    } catch (const std::exception &e) {
        qWarning() << "mergeCategories failed:" << e.what();
        try { m_db->exec("ROLLBACK"); } catch (...) {}
        return false;
    }
}
