#pragma once

#include "Constants.h"
#include "database/NoteDatabase.h"
#include "models/NoteModel.h"

#include <QObject>

class SearchManager;
class QThread;

class NoteController : public QObject {
    Q_OBJECT
    Q_PROPERTY(NoteModel *noteModel READ noteModel CONSTANT)
    Q_PROPERTY(bool isSearching READ isSearching NOTIFY isSearchingChanged)
    Q_PROPERTY(QString searchScope READ searchScope NOTIFY searchScopeChanged)
    Q_PROPERTY(QString currentSearchKeyword READ currentSearchKeyword NOTIFY isSearchingChanged)
    Q_PROPERTY(bool isRebuildingIndex READ isRebuildingIndex NOTIFY isRebuildingIndexChanged)
public:
    explicit NoteController(QObject *parent = nullptr);
    ~NoteController() override;

    Q_INVOKABLE void loadNotes(const QString &categoryKey = Constants::defaultCategoryKey(), const QString &categoryDisplayName = QString());
    Q_INVOKABLE void loadNotesSorted(const QString &categoryKey, int sortOrder, const QString &categoryDisplayName = QString());
    Q_INVOKABLE qint64 addNote(const QString &category = QString());
    Q_INVOKABLE bool updateNote(qint64 id, const QString &content);
    Q_INVOKABLE bool updateTitle(qint64 id, const QString &title);
    Q_INVOKABLE bool deleteNote(qint64 id);
    Q_INVOKABLE bool moveNoteToCategory(qint64 id, const QString &category);

    // 搜索相关
    Q_INVOKABLE void searchNotes(const QString &keyword);
    Q_INVOKABLE void clearSearch();
    Q_INVOKABLE void setSearchScope(const QString &scope);  // "global" / "category"
    // 异步重建搜索索引：子线程写临时索引目录 → 主线程 swap。
    // 用于备份恢复后 + 启动时索引与 DB 不一致时调用。
    Q_INVOKABLE void rebuildSearchIndex();

    // 应用新的排序规则（设置页保存后调用，立即用新 sortOrder 重新加载当前视图）
    Q_INVOKABLE void applySortOrder(int sortOrder);

    NoteModel *noteModel() const;
    NoteDatabase &database();

    bool isSearching() const { return m_isSearching; }
    QString searchScope() const { return m_searchScope; }
    QString currentSearchKeyword() const { return m_searchKeyword; }
    bool isRebuildingIndex() const { return m_rebuilding; }

signals:
    void noteCreated(qint64 id);
    void noteDeleted(qint64 id);
    void isSearchingChanged();
    void searchScopeChanged();
    void isRebuildingIndexChanged();

private:
    NoteDatabase *m_db;
    NoteModel *m_model;
    SearchManager *m_search;
    QString m_currentCategoryKey;        // 逻辑标识："all"/"work"/...
    QString m_currentCategoryDisplayName; // DB 中存的显示名（用于 WHERE 过滤）
    int m_currentSortOrder = 0;

    bool m_isSearching = false;
    QString m_searchScope = QStringLiteral("global");
    QString m_searchKeyword;

    // 搜索索引异步重建状态
    bool m_rebuilding = false;
    QThread *m_rebuildThread = nullptr;

    void reloadCurrentView();
};

