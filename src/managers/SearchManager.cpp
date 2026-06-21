// xapian.h 必须在 Qt 头文件之前 include，否则 emit/signals 宏冲突
#include <xapian.h>

#include "SearchManager.h"

#include <QDebug>
#include <QDir>
#include <QFileInfo>
#include <QSet>

namespace {
constexpr int kTitleWeight = 10;   // 标题命中加权
constexpr int kBodyWeight = 1;     // 正文命中加权
constexpr char kTitlePrefix[] = "T";
constexpr char kBodyPrefix[] = "B";
constexpr char kIdPrefix[] = "Q";  // 唯一标识 term，用于删/改
}

SearchManager::SearchManager(QObject *parent)
    : QObject(parent)
{
}

SearchManager::~SearchManager() = default;

bool SearchManager::initialize(const QString &indexPath)
{
    m_indexPath = indexPath;

    // 先释放旧的数据库连接（否则 Windows 下文件锁残留导致重开失败）
    m_indexer.reset();
    m_db.reset();

    QDir().mkpath(indexPath);

    try {
        m_db = std::make_unique<Xapian::WritableDatabase>(
            indexPath.toStdString(),
            Xapian::DB_CREATE_OR_OPEN);

        m_indexer = std::make_unique<Xapian::TermGenerator>();
        m_indexer->set_flags(Xapian::TermGenerator::FLAG_CJK_NGRAM);

        return true;
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::initialize failed:" << e.get_msg().c_str();
        m_db.reset();
        m_indexer.reset();
        return false;
    }
}

std::string SearchManager::idTerm(qint64 id)
{
    return std::string(kIdPrefix) + std::to_string(id);
}

std::string SearchManager::numberValue(qint64 id)
{
    // value(0) 仅用于结果反查 note id，不做排序，直接存十进制字符串
    return std::to_string(id);
}

void SearchManager::indexNote(const Note &note)
{
    if (!m_db || !m_indexer) return;

    try {
        const std::string idt = idTerm(note.id);

        m_db->delete_document(idt);

        Xapian::Document doc;
        doc.add_boolean_term(idt);
        doc.add_value(0, numberValue(note.id));

        m_indexer->set_document(doc);

        const std::string title = note.title.toStdString();
        const std::string body = stripHtml(note.content).toStdString();

        if (!title.empty()) {
            m_indexer->index_text(title, kTitleWeight, kTitlePrefix);
        }
        if (!body.empty()) {
            m_indexer->index_text(body, kBodyWeight, kBodyPrefix);
        }

        m_db->add_document(doc);
        m_db->commit();
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::indexNote failed for id" << note.id
                   << ":" << e.get_msg().c_str();
    }
}

void SearchManager::removeNote(qint64 id)
{
    if (!m_db) return;

    try {
        m_db->delete_document(idTerm(id));
        m_db->commit();
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::removeNote failed for id" << id
                   << ":" << e.get_msg().c_str();
    }
}

void SearchManager::rebuildAll(const QList<Note> &notes)
{
    if (m_indexPath.isEmpty()) return;

    // 先释放旧连接，再清空目录
    m_indexer.reset();
    m_db.reset();

    QDir(m_indexPath).removeRecursively();
    QDir().mkpath(m_indexPath);

    try {
        m_db = std::make_unique<Xapian::WritableDatabase>(
            m_indexPath.toStdString(),
            Xapian::DB_CREATE_OR_OVERWRITE);

        m_indexer = std::make_unique<Xapian::TermGenerator>();
        m_indexer->set_flags(Xapian::TermGenerator::FLAG_CJK_NGRAM);

        for (const Note &note : notes) {
            indexNote(note);
        }

        m_db->commit();
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::rebuildAll failed:" << e.get_msg().c_str();
    }
}

QList<SearchResult> SearchManager::search(const QString &keyword, bool globalScope) const
{
    Q_UNUSED(globalScope);  // 范围过滤在 NoteController 层按 category 处理

    QList<SearchResult> results;
    if (!m_db || keyword.trimmed().isEmpty()) return results;

    try {
        Xapian::Database db(m_indexPath.toStdString());

        Xapian::QueryParser qp;
        qp.set_default_op(Xapian::Query::OP_AND);
        qp.set_stemming_strategy(Xapian::QueryParser::STEM_NONE);

        const std::string q = keyword.trimmed().toStdString();

        Xapian::Query titleQ = qp.parse_query(q, Xapian::QueryParser::FLAG_CJK_NGRAM, kTitlePrefix);
        Xapian::Query bodyQ = qp.parse_query(q, Xapian::QueryParser::FLAG_CJK_NGRAM, kBodyPrefix);

        Xapian::Query combined = Xapian::Query(Xapian::Query::OP_OR, titleQ, bodyQ);

        Xapian::Enquire enquire(db);
        enquire.set_query(combined);

        Xapian::MSet matches = enquire.get_mset(0, db.get_doccount());

        // 先单独查标题命中集合，用于判断 relevanceScore
        QSet<qint64> titleHitIds;
        if (titleQ.get_type() != Xapian::Query::LEAF_MATCH_ALL || !titleQ.empty()) {
            Xapian::Enquire titleEnquire(db);
            titleEnquire.set_query(titleQ);
            Xapian::MSet titleMatches = titleEnquire.get_mset(0, db.get_doccount());
            for (Xapian::MSetIterator it = titleMatches.begin();
                 it != titleMatches.end(); ++it) {
                Xapian::Document doc = it.get_document();
                titleHitIds.insert(
                    QString::fromStdString(doc.get_value(0)).toLongLong());
            }
        }

        // 组合结果按 BM25 排序（标题加权已通过 indexNote 的 wdf_inc 体现）
        for (Xapian::MSetIterator it = matches.begin(); it != matches.end(); ++it) {
            SearchResult sr;
            Xapian::Document doc = it.get_document();
            sr.id = QString::fromStdString(doc.get_value(0)).toLongLong();
            sr.relevanceScore = titleHitIds.contains(sr.id) ? 0 : 1;
            results.append(sr);
        }
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::search failed:" << e.get_msg().c_str();
    }

    return results;
}

QString SearchManager::stripHtml(const QString &html)
{
    return Note::stripHtml(html);
}

int SearchManager::docCount() const
{
    if (!m_db) return 0;
    try {
        return static_cast<int>(m_db->get_doccount());
    } catch (const Xapian::Error &e) {
        qWarning() << "SearchManager::docCount failed:" << e.get_msg().c_str();
        return 0;
    }
}

void SearchManager::rebuildAllSwap(const QString &finalPath, const QString &tmpPath)
{
    if (m_indexPath.isEmpty() || finalPath.isEmpty() || tmpPath.isEmpty()) return;

    // 1. 释放主线程当前的索引连接（否则 Windows 下文件锁残留导致 rename 失败）
    m_indexer.reset();
    m_db.reset();

    // 2. 删除正式目录，将临时目录 rename 为正式目录
    //    QDir::rename 在 Windows 上对目录行为不可靠（可能返回 true 但未真正移走目录），
    //    因此统一采用"copy 文件 + 删除临时目录"策略，跨平台稳定。
    if (QDir(finalPath).exists()) {
        if (!QDir(finalPath).removeRecursively()) {
            qWarning() << "rebuildAllSwap: failed to remove final dir" << finalPath;
        }
    }
    QDir().mkpath(finalPath);
    {
        QDir tmpDir(tmpPath);
        const auto entries = tmpDir.entryInfoList(QDir::Files | QDir::NoDotAndDotDot);
        bool copyOk = true;
        for (const QFileInfo &fi : entries) {
            QString dest = finalPath + "/" + fi.fileName();
            if (QFile::exists(dest)) QFile::remove(dest);
            if (!QFile::copy(fi.absoluteFilePath(), dest)) {
                qWarning() << "rebuildAllSwap: copy failed for" << fi.absoluteFilePath();
                copyOk = false;
            }
        }
        if (copyOk) {
            QDir(tmpPath).removeRecursively();
        } else {
            qWarning() << "rebuildAllSwap: some files failed to copy, tmp dir left at" << tmpPath;
        }
    }

    // 3. 重新打开正式目录
    try {
        m_db = std::make_unique<Xapian::WritableDatabase>(
            finalPath.toStdString(), Xapian::DB_CREATE_OR_OPEN);
        m_indexer = std::make_unique<Xapian::TermGenerator>();
        m_indexer->set_flags(Xapian::TermGenerator::FLAG_CJK_NGRAM);
        m_indexPath = finalPath;
    } catch (const Xapian::Error &e) {
        qWarning() << "rebuildAllSwap reopen failed:" << e.get_msg().c_str();
        m_db.reset();
        m_indexer.reset();
    }
}
