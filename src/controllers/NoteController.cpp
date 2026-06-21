#include "NoteController.h"

#include "Constants.h"
#include "managers/RebuildIndexWorker.h"
#include "managers/SearchManager.h"

#include <QDebug>
#include <QDir>
#include <QStandardPaths>
#include <QThread>

#include <SQLiteCpp/Statement.h>

NoteController::NoteController(QObject *parent)
    : QObject(parent)
    , m_currentCategoryKey(Constants::defaultCategoryKey())
    , m_search(new SearchManager(this))
{
    auto dataDir = QStandardPaths::writableLocation(QStandardPaths::AppDataLocation);
    QDir().mkpath(dataDir);
    auto dbPath = dataDir + "/qnote.db";

    m_db = new NoteDatabase(dbPath);
    m_db->initSchema();

    m_model = new NoteModel(this);

    // 初始化搜索索引：打开或创建空索引目录
    auto indexPath = dataDir + "/search_index";
    if (!m_search->initialize(indexPath)) {
        qWarning() << "SearchManager init failed, search disabled";
    }

    // 清理可能残留的临时索引目录（上次重建崩溃留下的）
    QDir(indexPath + ".tmp").removeRecursively();

    // 启动时按需重建：索引 doc 数 != DB note 数才异步重建。
    // 避免每次启动都全量重建，同时保证首次升级/异常中断后的自愈。
    int dbCount = m_db->queryAllUuids().size();
    int idxCount = m_search->docCount();
    if (dbCount != idxCount) {
        qDebug() << "Startup index check: db=" << dbCount << "index=" << idxCount << "-> rebuild";
        rebuildSearchIndex();
    } else {
        qDebug() << "Startup index check: db=" << dbCount << "index=" << idxCount << "-> skip rebuild";
    }
}

NoteController::~NoteController()
{
    // 若有异步重建线程在跑，等待其优雅退出，避免 worker 通过 QueuedConnection
    // 访问已析构的 this（应用退出场景）。
    if (m_rebuildThread) {
        m_rebuildThread->quit();
        m_rebuildThread->wait(3000);  // 最多等 3 秒，避免永久卡住
    }
}

void NoteController::reloadCurrentView()
{
    if (m_isSearching) {
        searchNotes(m_searchKeyword);
    } else {
        loadNotesSorted(m_currentCategoryKey, m_currentSortOrder, m_currentCategoryDisplayName);
    }
}

void NoteController::loadNotes(const QString &categoryKey, const QString &categoryDisplayName)
{
    m_currentCategoryKey = categoryKey;
    m_currentCategoryDisplayName = categoryDisplayName;
    // key="all" 表示全部，传空让 DB 不过滤
    QString dbFilter = (categoryKey == Constants::defaultCategoryKey()) ? QString() : categoryDisplayName;
    auto notes = m_db->queryNotes(dbFilter);
    m_model->setNotes(notes);
}

void NoteController::loadNotesSorted(const QString &categoryKey, int sortOrder, const QString &categoryDisplayName)
{
    m_currentCategoryKey = categoryKey;
    m_currentCategoryDisplayName = categoryDisplayName;
    m_currentSortOrder = sortOrder;
    QString dbFilter = (categoryKey == Constants::defaultCategoryKey()) ? QString() : categoryDisplayName;
    auto notes = m_db->queryNotes(dbFilter, sortOrder);
    m_model->setNotes(notes);
}

qint64 NoteController::addNote(const QString &category)
{
    Note note;
    // category 为显示名（写入 DB notes.category 列）
    note.category = category.isEmpty() ? Constants::workCategory() : category;
    auto inserted = m_db->insertNote(note);
    if (inserted.id > 0) {
        m_search->indexNote(inserted);
        reloadCurrentView();
        emit noteCreated(inserted.id);
    }
    return inserted.id;
}

bool NoteController::updateNote(qint64 id, const QString &content)
{
    Note note;
    note.id = id;
    note.content = content;
    note.titleManual = m_model->getNoteTitleManual(id);
    if (note.titleManual)
        note.title = m_model->getNoteTitle(id);
    note.category = m_db->getNoteById(id).category;
    DBG("updateNote: id=" << id << " titleManual=" << note.titleManual << " title=" << note.title);
    if (m_db->updateNote(note)) {
        m_search->indexNote(note);
        reloadCurrentView();
        return true;
    }
    return false;
}

bool NoteController::updateTitle(qint64 id, const QString &title)
{
    if (!m_db) return false;

    try {
        SQLite::Statement query(m_db->rawDb(),
            "UPDATE notes SET title = ?, titleManual = 1 WHERE id = ?");
        query.bind(1, title.toStdString());
        query.bind(2, id);
        query.executeStep();
        bool ok = m_db->rawDb().getChanges() > 0;
        if (ok) {
            // 同步搜索索引（用 DB 中的完整 Note）
            Note note = m_db->getNoteById(id);
            if (note.id > 0) {
                m_search->indexNote(note);
            }
            reloadCurrentView();
        }
        return ok;
    } catch (const std::exception &e) {
        qWarning() << "updateTitle failed:" << e.what();
        return false;
    }
}

bool NoteController::deleteNote(qint64 id)
{
    if (m_db->deleteNote(id)) {
        m_search->removeNote(id);
        reloadCurrentView();
        emit noteDeleted(id);
        return true;
    }
    return false;
}

bool NoteController::moveNoteToCategory(qint64 id, const QString &category)
{
    if (m_db->updateNoteCategory(id, category)) {
        // 分类变化不影响搜索内容索引，但需更新 model
        reloadCurrentView();
        return true;
    }
    return false;
}

NoteModel *NoteController::noteModel() const
{
    return m_model;
}

NoteDatabase &NoteController::database()
{
    return *m_db;
}

void NoteController::searchNotes(const QString &keyword)
{
    QString trimmed = keyword.trimmed();

    if (trimmed.isEmpty()) {
        clearSearch();
        return;
    }

    m_searchKeyword = trimmed;

    bool wasSearching = m_isSearching;
    m_isSearching = true;

    // 搜索范围：
    // - 当前分类非"全部"：强制限定该分类（scope 按钮无效）
    // - 当前分类是"全部" + scope=category：按"全部"过滤（等价全局）
    // - 当前分类是"全部" + scope=global：全局搜索
    bool isAll = (m_currentCategoryKey == Constants::defaultCategoryKey());
    bool limitToCategory = !isAll || (m_searchScope == QStringLiteral("category"));

    auto results = m_search->search(trimmed, true);

    QList<Note> notes;
    for (const auto &sr : results) {
        Note note = m_db->getNoteById(sr.id);
        if (note.id <= 0) continue;

        if (limitToCategory
            && !isAll
            && note.category != m_currentCategoryDisplayName) {
            continue;
        }
        notes.append(note);
    }

    m_model->setNotes(notes);

    if (!wasSearching) {
        emit isSearchingChanged();
    }
}

void NoteController::clearSearch()
{
    if (!m_isSearching && m_searchKeyword.isEmpty()) return;

    m_isSearching = false;
    m_searchKeyword.clear();
    loadNotesSorted(m_currentCategoryKey, m_currentSortOrder, m_currentCategoryDisplayName);
    emit isSearchingChanged();
}

void NoteController::setSearchScope(const QString &scope)
{
    if (m_searchScope == scope) return;
    m_searchScope = scope;
    emit searchScopeChanged();

    // 若正在搜索，重新执行以应用新范围
    if (m_isSearching) {
        searchNotes(m_searchKeyword);
    }
}

void NoteController::applySortOrder(int sortOrder)
{
    m_currentSortOrder = sortOrder;
    reloadCurrentView();
}

void NoteController::rebuildSearchIndex()
{
    // 防并发：重建期间再次触发直接忽略
    if (m_rebuilding) {
        qWarning() << "rebuildSearchIndex: already in progress, skipped";
        return;
    }

    QString indexPath = m_search->indexPath();
    if (indexPath.isEmpty()) {
        qWarning() << "rebuildSearchIndex: index path empty, aborted";
        return;
    }
    QString tmpPath = indexPath + ".tmp";
    QString dbPath = m_db->dbPath();

    m_rebuilding = true;
    emit isRebuildingIndexChanged();

    // 清空搜索状态：重建期间索引不一致，避免读到旧结果
    if (m_isSearching) {
        clearSearch();
    }

    // 清理可能残留的临时目录
    QDir(tmpPath).removeRecursively();

    qDebug() << "rebuildSearchIndex: starting async rebuild ->" << tmpPath;

    QThread *thread = new QThread;
    auto *worker = new RebuildIndexWorker(dbPath, tmpPath);
    worker->moveToThread(thread);

    // 子线程启动后执行 worker->run()
    connect(thread, &QThread::started, worker, &RebuildIndexWorker::run);

    // worker 完成（成功/失败）：在主线程执行 swap + 收尾
    connect(worker, &RebuildIndexWorker::finished, this, [this, indexPath, tmpPath, thread](bool success) {
        if (success) {
            m_search->rebuildAllSwap(indexPath, tmpPath);
            qDebug() << "rebuildSearchIndex: swap done";
        } else {
            qWarning() << "Search index rebuild failed, search disabled until next rebuild";
        }
        thread->quit();
    });

    // 线程结束后清理：worker/thread deleteLater + 重置 m_rebuilding
    connect(thread, &QThread::finished, worker, &QObject::deleteLater);
    connect(thread, &QThread::finished, thread, &QObject::deleteLater);
    connect(thread, &QThread::finished, this, [this]() {
        m_rebuilding = false;
        m_rebuildThread = nullptr;
        emit isRebuildingIndexChanged();
    });

    m_rebuildThread = thread;
    thread->start();
}
