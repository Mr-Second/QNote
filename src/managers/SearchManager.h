#pragma once

#include "models/Note.h"

#include <QObject>
#include <QString>
#include <QList>

#include <memory>
#include <string>

namespace Xapian {
class WritableDatabase;
class TermGenerator;
}
struct SearchResult {
    qint64 id = -1;
    int relevanceScore = 1;  // 0=标题命中, 1=正文命中

    bool operator==(const SearchResult &other) const
    { return id == other.id; }
};

class SearchManager : public QObject {
    Q_OBJECT
public:
    explicit SearchManager(QObject *parent = nullptr);
    ~SearchManager() override;

    bool initialize(const QString &indexPath);
    void indexNote(const Note &note);
    void removeNote(qint64 id);
    void rebuildAll(const QList<Note> &notes);
    QList<SearchResult> search(const QString &keyword, bool globalScope) const;

    // 索引当前文档数（用于启动时与 DB 条目数对比决定是否需要重建）
    int docCount() const;
    // 当前索引目录路径（NoteController 用于构造临时路径）
    QString indexPath() const { return m_indexPath; }

    // 子线程写好临时目录后，主线程执行 swap：
    // 释放当前连接 → 删正式目录 → rename 临时目录 → 重新 initialize
    void rebuildAllSwap(const QString &finalPath, const QString &tmpPath);

    static QString stripHtml(const QString &html);

private:
    static std::string idTerm(qint64 id);
    static std::string numberValue(qint64 id);

    QString m_indexPath;
    std::unique_ptr<Xapian::WritableDatabase> m_db;
    std::unique_ptr<Xapian::TermGenerator> m_indexer;
};
