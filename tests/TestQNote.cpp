#include <QTest>
#include <QCoreApplication>

#include "Constants.h"
#include "database/NoteDatabase.h"
#include "controllers/EdgeHideController.h"
#include "models/Note.h"
#include "models/NoteModel.h"
#include "managers/TextFormatHelper.h"
#include "managers/SearchManager.h"
#include "managers/RebuildIndexWorker.h"

#include <QTemporaryDir>
#include <QSignalSpy>
#include <QTextCharFormat>
#include <QTextCursor>
#include <QTextDocument>
#include <QFont>
#include <QScopedPointer>
#include <QStringList>
#include <QVector>
#include <QThread>
#include <QDir>
#include <functional>

class TestNoteDatabase : public QObject {
    Q_OBJECT

private slots:
    void initTestCase();
    void testInitSchema();
    void testInsertNote();
    void testUpdateNote();
    void testDeleteNote();
    void testQueryByCategory();
    void testQueryAll();
    void testEmptyQuery();
    void testTitleGeneration();
    void testSchemaIdempotent();

private:
    QTemporaryDir m_tempDir;
    int m_counter = 0;
    QString nextDbPath();
};

QString TestNoteDatabase::nextDbPath()
{
    return m_tempDir.path() + "/" + QString("test_%1.db").arg(m_counter++);
}

void TestNoteDatabase::initTestCase()
{
    QVERIFY(m_tempDir.isValid());
}

void TestNoteDatabase::testInitSchema()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());
}

void TestNoteDatabase::testInsertNote()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    Note note;
    note.content = "Hello QNote";
    note.category = Constants::workCategory();

    Note inserted = db.insertNote(note);
    QVERIFY(inserted.id > 0);
    QCOMPARE(inserted.content, QString("Hello QNote"));
    QCOMPARE(inserted.title, Note::generateTitle("Hello QNote"));
    QVERIFY(inserted.createdAt.isValid());
    QVERIFY(inserted.updatedAt.isValid());

    QList<Note> notes = db.queryNotes();
    QCOMPARE(notes.size(), 1);
    QCOMPARE(notes[0].id, inserted.id);
}

void TestNoteDatabase::testUpdateNote()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    Note note;
    note.content = "Original";
    Note inserted = db.insertNote(note);

    inserted.content = "Updated content";
    inserted.title = Note::generateTitle(inserted.content);
    QVERIFY(db.updateNote(inserted));

    QList<Note> notes = db.queryNotes();
    QCOMPARE(notes.size(), 1);
    QCOMPARE(notes[0].content, QString("Updated content"));
    QCOMPARE(notes[0].title, Note::generateTitle("Updated content"));
    QVERIFY(notes[0].updatedAt >= inserted.createdAt);
}

void TestNoteDatabase::testDeleteNote()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    Note note;
    note.content = "To be deleted";
    Note inserted = db.insertNote(note);

    QVERIFY(db.deleteNote(inserted.id));

    QList<Note> notes = db.queryNotes();
    QCOMPARE(notes.size(), 0);
}

void TestNoteDatabase::testQueryByCategory()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    Note n1; n1.content = "Work note";
    n1.category = Constants::workCategory();
    db.insertNote(n1);

    Note n2; n2.content = "Life note";
    n2.category = Constants::lifeCategory();
    db.insertNote(n2);

    Note n3; n3.content = "Important note";
    n3.category = Constants::importantCategory();
    db.insertNote(n3);

    QCOMPARE(db.queryNotes(Constants::workCategory()).size(), 1);
    QCOMPARE(db.queryNotes(Constants::lifeCategory()).size(), 1);
    QCOMPARE(db.queryNotes(Constants::importantCategory()).size(), 1);
    QCOMPARE(db.queryNotes().size(), 3);
}

void TestNoteDatabase::testQueryAll()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    for (int i = 0; i < 5; ++i) {
        Note note;
        note.content = QString("Note %1").arg(i);
        db.insertNote(note);
    }

    QList<Note> notes = db.queryNotes();
    QCOMPARE(notes.size(), 5);

    // queryNotes 默认按 updatedAt DESC,sortOrder 只是返回字段,不影响行顺序
    // 验证 sortOrder 列被正确读出(每条都应该是 1..5 递增分配的非零值)
    QSet<int> sortOrders;
    for (const Note &n : notes) {
        QVERIFY(n.sortOrder >= 1 && n.sortOrder <= 5);
        sortOrders.insert(n.sortOrder);
    }
    QCOMPARE(sortOrders.size(), 5); // 5 条互不相同

    // 行顺序应按 updatedAt DESC(插入极快时间戳相同,允许相等)
    for (int i = 0; i < notes.size() - 1; ++i) {
        QVERIFY(notes[i].updatedAt >= notes[i + 1].updatedAt);
    }
}

void TestNoteDatabase::testEmptyQuery()
{
    NoteDatabase db(nextDbPath());
    QVERIFY(db.initSchema());

    QList<Note> notes = db.queryNotes();
    QVERIFY(notes.isEmpty());
}

void TestNoteDatabase::testTitleGeneration()
{
    QCOMPARE(Note::generateTitle(""), Constants::newNoteTitle());
    QCOMPARE(Note::generateTitle("   "), Constants::newNoteTitle());
    QCOMPARE(Note::generateTitle("Short"), QString("Short"));

    QString longContent(25, 'A');
    QString title = Note::generateTitle(longContent);
    QCOMPARE(title.length(), 21);
    QVERIFY(title.startsWith("AAAAAAAAAAAAAAAAAAAA"));
}

void TestNoteDatabase::testSchemaIdempotent()
{
    auto path = nextDbPath();
    {
        NoteDatabase db(path);
        QVERIFY(db.initSchema());
    }
    {
        NoteDatabase db(path);
        QVERIFY(db.initSchema());

        Note note;
        note.content = "Still works";
        Note inserted = db.insertNote(note);
        QVERIFY(inserted.id > 0);
    }
}

class TestNoteModel : public QObject {
    Q_OBJECT

private slots:
    void testRowCount();
    void testData();
    void testRoleNames();
    void testSetNotes();
    void testNoteIdAt();
    void testEmptyModel();
};

void TestNoteModel::testRowCount()
{
    NoteModel model;
    QCOMPARE(model.rowCount(), 0);

    Note n1; n1.id = 1; n1.title = "T1"; n1.content = "C1";
    Note n2; n2.id = 2; n2.title = "T2"; n2.content = "C2";
    model.setNotes({n1, n2});

    QCOMPARE(model.rowCount(), 2);
}

void TestNoteModel::testData()
{
    NoteModel model;

    Note note;
    note.id = 42;
    note.title = "Test Title";
    note.content = "Test Content";
    note.category = "Work";
    note.createdAt = QDateTime::currentDateTime();
    note.updatedAt = QDateTime::currentDateTime();

    model.setNotes({note});

    QModelIndex idx = model.index(0);
    QCOMPARE(model.data(idx, NoteModel::IdRole).toLongLong(), 42);
    QCOMPARE(model.data(idx, NoteModel::TitleRole).toString(), QString("Test Title"));
    QCOMPARE(model.data(idx, NoteModel::ContentRole).toString(), QString("Test Content"));
    QCOMPARE(model.data(idx, NoteModel::CategoryRole).toString(), QString("Work"));
    QVERIFY(model.data(idx, NoteModel::CreatedAtRole).toDateTime().isValid());
    QVERIFY(model.data(idx, NoteModel::UpdatedAtRole).toDateTime().isValid());
    QCOMPARE(model.data(idx, Qt::DisplayRole), QVariant());
}

void TestNoteModel::testRoleNames()
{
    NoteModel model;
    auto roles = model.roleNames();

    QCOMPARE(roles.value(NoteModel::IdRole), QByteArray("id"));
    QCOMPARE(roles.value(NoteModel::TitleRole), QByteArray("title"));
    QCOMPARE(roles.value(NoteModel::ContentRole), QByteArray("content"));
    QCOMPARE(roles.value(NoteModel::CategoryRole), QByteArray("category"));
    QCOMPARE(roles.value(NoteModel::CreatedAtRole), QByteArray("createdAt"));
    QCOMPARE(roles.value(NoteModel::UpdatedAtRole), QByteArray("updatedAt"));
}

void TestNoteModel::testSetNotes()
{
    NoteModel model;

    Note n1; n1.id = 1;
    model.setNotes({n1});
    QCOMPARE(model.rowCount(), 1);

    Note n10; n10.id = 10;
    Note n20; n20.id = 20;
    Note n30; n30.id = 30;
    model.setNotes({n10, n20, n30});
    QCOMPARE(model.rowCount(), 3);
    QCOMPARE(model.noteIdAt(0), 10);
    QCOMPARE(model.noteIdAt(1), 20);
    QCOMPARE(model.noteIdAt(2), 30);
}

void TestNoteModel::testNoteIdAt()
{
    NoteModel model;

    QCOMPARE(model.noteIdAt(0), -1);
    QCOMPARE(model.noteIdAt(-1), -1);

    Note n5; n5.id = 5;
    Note n10; n10.id = 10;
    model.setNotes({n5, n10});

    QCOMPARE(model.noteIdAt(0), 5);
    QCOMPARE(model.noteIdAt(1), 10);
    QCOMPARE(model.noteIdAt(2), -1);
}

void TestNoteModel::testEmptyModel()
{
    NoteModel model;

    QCOMPARE(model.rowCount(), 0);
    QVERIFY(!model.index(0).isValid());
    QCOMPARE(model.data(model.index(0), NoteModel::IdRole), QVariant());
}

class TestTextFormatHelper : public QObject {
    Q_OBJECT

private slots:
    void testEmptyCharFormatAccessors();
    void testToggleOnDocumentWithContent();
    void testTextFormatHelperWithoutTextArea();
    void testClearCharFormatWithoutTextArea();
    void testInsertCleanParagraphWithoutTextArea();
};

void TestTextFormatHelper::testEmptyCharFormatAccessors()
{
    // Default-constructed QTextCharFormat has no properties set. Verify that
    // the int/bool accessors used by toggleBold/Italic/Underline/Strikeout
    // do NOT trigger null-pointer dereference (unlike fontFamily() which
    // returns QString and was fixed separately in _updateFormatState).
    QTextCharFormat fmt;

    int weight = fmt.fontWeight();
    bool italic = fmt.fontItalic();
    bool underline = fmt.fontUnderline();
    bool strikeout = fmt.fontStrikeOut();

    QCOMPARE(weight, static_cast<int>(QFont::Normal));
    QCOMPARE(italic, false);
    QCOMPARE(underline, false);
    QCOMPARE(strikeout, false);
}

void TestTextFormatHelper::testToggleOnDocumentWithContent()
{
    // Exercise toggleBold via TextFormatHelper on a real QTextDocument to
    // confirm charFormat read/write roundtrip is safe.
    QTextDocument doc;
    doc.setPlainText(QStringLiteral("hello world"));

    TextFormatHelper helper;

    // TextFormatHelper is designed to be driven by QML via QQuickItem property
    // reads. Without a real QQuickItem we cannot use setTextArea(); but we can
    // still confirm the helper is safe to construct/destruct and that calling
    // toggle methods before setTextArea() (cursor null) returns cleanly.
    helper.toggleBold();
    helper.toggleItalic();
    helper.toggleUnderline();
    helper.toggleStrikeout();

    QVERIFY(helper.currentFontFamily().isEmpty());
    QCOMPARE(helper.currentFontPixelSize(), 16);
}

void TestTextFormatHelper::testTextFormatHelperWithoutTextArea()
{
    // Regression guard: TextFormatHelper used without a QML textArea must not
    // crash on any toggle or refresh. This documents the defensive contract
    // that covers the fontFamily crash scenario (Session 4).
    TextFormatHelper helper;
    helper.toggleBold();
    helper.toggleItalic();
    helper.toggleUnderline();
    helper.toggleStrikeout();
    helper.refreshFormatState();
    helper.insertUnorderedList();
    helper.insertOrderedList();
    helper.setAlignment(static_cast<int>(Qt::AlignCenter));

    // No crash = pass.
    QVERIFY(true);
}

void TestTextFormatHelper::testClearCharFormatWithoutTextArea()
{
    // clearCharFormat 在无 textArea(cursor null)时必须安全返回不崩溃。
    // 覆盖 richtext-format-leak 任务的防御契约。
    TextFormatHelper helper;
    helper.clearCharFormat();
    helper.clearCharFormat();  // 重复调用也应安全
    QVERIFY(true);
}

void TestTextFormatHelper::testInsertCleanParagraphWithoutTextArea()
{
    // insertCleanParagraph 在无 textArea(cursor null)时必须安全返回不崩溃。
    TextFormatHelper helper;
    helper.insertCleanParagraph();
    helper.insertCleanParagraph();
    QVERIFY(true);
}

class TestEdgeHideController : public QObject {
    Q_OBJECT

private slots:
    void testHiddenWindowRevealsFromTopHotZone();
    void testShowFinishWhileActiveKeepsWindowVisible();
    void testShowFinishWhileInactiveRearmsAutoHide();
    void testPointerLeavingWindowRearmsAutoHideWithoutWaitingForInactive();
    void testRepeatedInactiveSyncDoesNotDelayAutoHide();

private:
    static void dockWindowAtTop(EdgeHideController &controller);
    static void hideAtTopEdge(EdgeHideController &controller, QSignalSpy &hideSpy);
};

void TestEdgeHideController::dockWindowAtTop(EdgeHideController &controller)
{
    controller.updateWindowGeometry(100, 0, 240, 320);
}

void TestEdgeHideController::hideAtTopEdge(EdgeHideController &controller, QSignalSpy &hideSpy)
{
    dockWindowAtTop(controller);
    controller.syncPointerState(false, QPoint(10, 10));
    QTRY_COMPARE_WITH_TIMEOUT(hideSpy.count(), 1, 1200);
    controller.updateWindowGeometry(100, -317, 240, 320);
    controller.notifyHideFinished();
    QVERIFY(controller.isHidden());
}

void TestEdgeHideController::testHiddenWindowRevealsFromTopHotZone()
{
    EdgeHideController controller;
    QSignalSpy hideSpy(&controller, &EdgeHideController::hideRequested);
    QSignalSpy showSpy(&controller, &EdgeHideController::showRequested);

    hideAtTopEdge(controller, hideSpy);

    controller.syncPointerState(false, QPoint(120, 1));

    QCOMPARE(showSpy.count(), 1);
    QVERIFY(!controller.isHidden());
}

void TestEdgeHideController::testShowFinishWhileActiveKeepsWindowVisible()
{
    EdgeHideController controller;
    QSignalSpy hideSpy(&controller, &EdgeHideController::hideRequested);
    QSignalSpy showSpy(&controller, &EdgeHideController::showRequested);

    hideAtTopEdge(controller, hideSpy);
    controller.syncPointerState(true, QPoint(120, 1));
    controller.updateWindowGeometry(100, 0, 240, 320);
    controller.notifyShowFinished();

    QCOMPARE(showSpy.count(), 1);
    QVERIFY(!controller.isHidden());
    QTest::qWait(900);

    QCOMPARE(hideSpy.count(), 1);
    QVERIFY(!controller.isHidden());
}

void TestEdgeHideController::testShowFinishWhileInactiveRearmsAutoHide()
{
    EdgeHideController controller;
    QSignalSpy hideSpy(&controller, &EdgeHideController::hideRequested);
    QSignalSpy showSpy(&controller, &EdgeHideController::showRequested);

    hideAtTopEdge(controller, hideSpy);

    controller.syncPointerState(true, QPoint(120, 1));
    controller.updateWindowGeometry(100, 0, 240, 320);
    controller.onActiveChanged(false);
    controller.syncPointerState(false, QPoint(600, 600));
    controller.notifyShowFinished();

    QCOMPARE(showSpy.count(), 1);
    QVERIFY(!controller.isHidden());
    QTRY_COMPARE_WITH_TIMEOUT(hideSpy.count(), 2, 1200);
    controller.updateWindowGeometry(100, -317, 240, 320);
    controller.notifyHideFinished();

    QVERIFY(controller.isHidden());
}

void TestEdgeHideController::testPointerLeavingWindowRearmsAutoHideWithoutWaitingForInactive()
{
    EdgeHideController controller;
    QSignalSpy hideSpy(&controller, &EdgeHideController::hideRequested);
    QSignalSpy showSpy(&controller, &EdgeHideController::showRequested);

    hideAtTopEdge(controller, hideSpy);

    controller.syncPointerState(true, QPoint(120, 1));
    controller.updateWindowGeometry(100, 0, 240, 320);
    controller.notifyShowFinished();

    QCOMPARE(showSpy.count(), 1);
    QVERIFY(!controller.isHidden());

    controller.syncPointerState(true, QPoint(600, 600));
    QTRY_COMPARE_WITH_TIMEOUT(hideSpy.count(), 2, 1200);
    controller.updateWindowGeometry(100, -317, 240, 320);
    controller.notifyHideFinished();

    QVERIFY(controller.isHidden());
}

void TestEdgeHideController::testRepeatedInactiveSyncDoesNotDelayAutoHide()
{
    EdgeHideController controller;
    QSignalSpy hideSpy(&controller, &EdgeHideController::hideRequested);

    hideAtTopEdge(controller, hideSpy);

    controller.syncPointerState(true, QPoint(120, 1));
    controller.updateWindowGeometry(100, 0, 240, 320);
    controller.notifyShowFinished();

    QVERIFY(!controller.isHidden());

    controller.syncPointerState(false, QPoint(600, 600));
    QTest::qWait(300);
    controller.syncPointerState(false, QPoint(600, 600));
    QTest::qWait(300);
    controller.syncPointerState(false, QPoint(600, 600));

    QTRY_COMPARE_WITH_TIMEOUT(hideSpy.count(), 2, 350);
}

class TestSearchManager : public QObject {
    Q_OBJECT
private slots:
    void init();
    void testIndexAndSearchEnglish();
    void testCjkNgramMatching();
    void testRemoveNote();
    void testRebuildAll();
    void testTitleRelevanceRanking();
    void testEmptyKeyword();
private:
    QTemporaryDir m_indexDir;
    SearchManager *m_search = nullptr;
    QString indexPath() const { return m_indexDir.path() + "/idx"; }
};

void TestSearchManager::init()
{
    // 每个测试用例独立的临时目录 + 全新 SearchManager 实例
    m_indexDir = QTemporaryDir();
    QVERIFY(m_indexDir.isValid());
    delete m_search;
    m_search = new SearchManager(this);
    QVERIFY(m_search->initialize(indexPath()));
}

void TestSearchManager::testIndexAndSearchEnglish()
{
    Note n1; n1.id = 1; n1.title = "meeting"; n1.content = "project discussion";
    Note n2; n2.id = 2; n2.title = "shopping"; n2.content = "buy groceries";
    m_search->indexNote(n1);
    m_search->indexNote(n2);

    auto results = m_search->search("meeting", true);
    QCOMPARE(results.size(), 1);
    QCOMPARE(results[0].id, qint64(1));

    results = m_search->search("groceries", true);
    QCOMPARE(results.size(), 1);
    QCOMPARE(results[0].id, qint64(2));
}

void TestSearchManager::testCjkNgramMatching()
{
    Note n1; n1.id = 10; n1.title = "会议记录"; n1.content = "今天讨论了项目进度";
    Note n2; n2.id = 11; n2.title = "购物清单"; n2.content = "买水果和蔬菜";
    m_search->indexNote(n1);
    m_search->indexNote(n2);

    // 2字中文 bigram 匹配
    auto results = m_search->search("会议", true);
    QVERIFY(!results.isEmpty());
    QVERIFY(results.contains(SearchResult{10, 0}));

    // 1字中文 unigram 匹配
    results = m_search->search("买", true);
    QVERIFY(!results.isEmpty());
    QVERIFY(results.contains(SearchResult{11, 1}));
}

void TestSearchManager::testRemoveNote()
{
    Note n; n.id = 20; n.title = "temp"; n.content = "will be deleted";
    m_search->indexNote(n);

    auto results = m_search->search("temp", true);
    QCOMPARE(results.size(), 1);

    m_search->removeNote(20);

    results = m_search->search("temp", true);
    QCOMPARE(results.size(), 0);
}

void TestSearchManager::testRebuildAll()
{
    Note n1; n1.id = 30; n1.title = "alpha"; n1.content = "first";
    Note n2; n2.id = 31; n2.title = "beta"; n2.content = "second";
    m_search->indexNote(n1);
    m_search->indexNote(n2);

    QList<Note> all = {n1, n2};
    m_search->rebuildAll(all);

    auto results = m_search->search("alpha", true);
    QCOMPARE(results.size(), 1);
    QCOMPARE(results[0].id, qint64(30));

    results = m_search->search("beta", true);
    QCOMPARE(results.size(), 1);
    QCOMPARE(results[0].id, qint64(31));
}

void TestSearchManager::testTitleRelevanceRanking()
{
    Note n1; n1.id = 40; n1.title = "normal note"; n1.content = "nothing special here";
    Note n2; n2.id = 41; n2.title = "nothing"; n2.content = "some content about nothing";

    m_search->indexNote(n1);
    m_search->indexNote(n2);

    auto results = m_search->search("nothing", true);
    QVERIFY(results.size() >= 1);

    bool titleHitFirst = false;
    for (const auto &r : results) {
        if (r.id == 41 && r.relevanceScore == 0) {
            titleHitFirst = true;
            break;
        }
    }
    QVERIFY(titleHitFirst);
}

void TestSearchManager::testEmptyKeyword()
{
    Note n; n.id = 50; n.title = "test"; n.content = "content";
    m_search->indexNote(n);

    auto results = m_search->search("", true);
    QVERIFY(results.isEmpty());

    results = m_search->search("   ", true);
    QVERIFY(results.isEmpty());
}

class TestRebuildIndexWorker : public QObject {
    Q_OBJECT
private slots:
    void testRebuildsFromDbToTmpIndex();
    void testEmptyDbProducesEmptyIndex();
};

void TestRebuildIndexWorker::testRebuildsFromDbToTmpIndex()
{
    // 1. 准备临时 DB，插入若干 Note
    QTemporaryDir dbDir;
    QVERIFY(dbDir.isValid());
    QString dbPath = dbDir.path() + "/test.db";
    {
        NoteDatabase db(dbPath);
        QVERIFY(db.initSchema());
        Note n1; n1.content = "alpha task"; n1.category = Constants::workCategory();
        db.insertNote(n1);
        Note n2; n2.content = "beta task"; n2.category = Constants::workCategory();
        db.insertNote(n2);
    }

    // 2. 启动 worker（在同线程同步跑，因为是纯 DB/索引操作）
    QTemporaryDir idxDir;
    QVERIFY(idxDir.isValid());
    QString tmpIndexPath = idxDir.path() + "/idx_tmp";

    RebuildIndexWorker worker(dbPath, tmpIndexPath);
    QSignalSpy spy(&worker, &RebuildIndexWorker::finished);
    worker.run();  // 同步执行

    // 3. 验证临时索引目录有 2 条 doc
    QVERIFY(spy.count() == 1);
    QCOMPARE(spy.takeFirst().at(0).toBool(), true);

    SearchManager verify;
    QVERIFY(verify.initialize(tmpIndexPath));
    QCOMPARE(verify.docCount(), 2);
}

void TestRebuildIndexWorker::testEmptyDbProducesEmptyIndex()
{
    QTemporaryDir dbDir;
    QVERIFY(dbDir.isValid());
    QString dbPath = dbDir.path() + "/empty.db";
    {
        NoteDatabase db(dbPath);
        QVERIFY(db.initSchema());
    }

    QTemporaryDir idxDir;
    QVERIFY(idxDir.isValid());
    QString tmpIndexPath = idxDir.path() + "/idx_tmp";

    RebuildIndexWorker worker(dbPath, tmpIndexPath);
    QSignalSpy spy(&worker, &RebuildIndexWorker::finished);
    worker.run();

    QVERIFY(spy.count() == 1);
    QCOMPARE(spy.takeFirst().at(0).toBool(), true);

    SearchManager verify;
    QVERIFY(verify.initialize(tmpIndexPath));
    QCOMPARE(verify.docCount(), 0);
}

class TestSearchManagerSwap : public QObject {
    Q_OBJECT
private slots:
    void testRebuildAllSwapReplacesFinalIndex();
};

void TestSearchManagerSwap::testRebuildAllSwapReplacesFinalIndex()
{
    QTemporaryDir dir;
    QVERIFY(dir.isValid());
    QString finalPath = dir.path() + "/final_idx";
    QString tmpPath = dir.path() + "/tmp_idx";

    // 1. 主索引写入旧内容
    SearchManager main;
    QVERIFY(main.initialize(finalPath));
    Note oldNote; oldNote.id = 100; oldNote.title = "old"; oldNote.content = "legacy content";
    main.indexNote(oldNote);
    QCOMPARE(main.docCount(), 1);

    // 2. 临时索引写入新内容（2 条）
    //    用作用域保证 tmp 析构后释放 Xapian 文件锁，再执行 swap
    //    （生产代码中等价于子线程 worker run() 结束时栈对象析构）
    {
        SearchManager tmp;
        QVERIFY(tmp.initialize(tmpPath));
        Note n1; n1.id = 1; n1.title = "new1"; n1.content = "fresh alpha";
        Note n2; n2.id = 2; n2.title = "new2"; n2.content = "fresh beta";
        tmp.indexNote(n1);
        tmp.indexNote(n2);
    }

    // 3. 执行 swap：正式目录应被临时目录替换
    main.rebuildAllSwap(finalPath, tmpPath);

    // 4. 验证正式目录现在是新内容
    QCOMPARE(main.docCount(), 2);
    QVERIFY(main.search("fresh", true).size() == 2);
    QVERIFY(main.search("legacy", true).isEmpty());

    // 临时目录应已被清理
    QVERIFY(!QDir(tmpPath).exists());
}

int main(int argc, char *argv[])
{
    QCoreApplication app(argc, argv);

    // 测试类注册表：名称 + 工厂函数
    struct TestEntry { const char *name; std::function<QObject*()> factory; };
    const TestEntry entries[] = {
        {"TestNoteDatabase",      []() -> QObject* { return new TestNoteDatabase; }},
        {"TestNoteModel",         []() -> QObject* { return new TestNoteModel; }},
        {"TestTextFormatHelper",  []() -> QObject* { return new TestTextFormatHelper; }},
        {"TestEdgeHideController", []() -> QObject* { return new TestEdgeHideController; }},
        {"TestSearchManager",     []() -> QObject* { return new TestSearchManager; }},
        {"TestRebuildIndexWorker", []() -> QObject* { return new TestRebuildIndexWorker; }},
        {"TestSearchManagerSwap", []() -> QObject* { return new TestSearchManagerSwap; }},
    };
    const int entryCount = sizeof(entries) / sizeof(entries[0]);

    // 解析原始 argv 中的 -o 参数（QtTest 格式：-o <file>,<format>）
    // 若存在，则为每个测试类生成 <file>_<ClassName>.txt，避免多次 qExec 覆盖输出。
    // 用法示例：QNoteTest -o results.txt,txt  → 生成 results_TestNoteDatabase.txt 等 4 个文件
    QString outputFile;
    QString outputFormat;
    QStringList argList;
    for (int i = 1; i < argc; ++i) {
        QString arg = QString::fromLocal8Bit(argv[i]);
        if (arg == QStringLiteral("-o") && i + 1 < argc) {
            QString spec = QString::fromLocal8Bit(argv[i + 1]);
            int comma = spec.indexOf(QLatin1Char(','));
            if (comma > 0) {
                outputFile = spec.left(comma);
                outputFormat = spec.mid(comma + 1);
            } else {
                outputFile = spec;
                outputFormat = QStringLiteral("txt");
            }
            ++i; // 跳过下一个参数（已被 -o 消费）
        } else {
            argList << arg;
        }
    }

    int status = 0;
    for (int idx = 0; idx < entryCount; ++idx) {
        QScopedPointer<QObject> obj(entries[idx].factory());

        // 构造该类专属的 argv：原始参数（去掉 -o）+ 可选的 per-class -o
        QVector<QByteArray> byteArgs;
        byteArgs << QByteArray(argv[0]);
        for (const QString &a : argList) {
            byteArgs << a.toLocal8Bit();
        }
        QByteArray perClassSpec;
        if (!outputFile.isEmpty()) {
            // 在扩展名前插入 _<ClassName>：results.txt → results_TestNoteDatabase.txt
            int dot = outputFile.lastIndexOf(QLatin1Char('.'));
            QString perClassFile = (dot > 0)
                ? outputFile.left(dot) + QStringLiteral("_") + QString::fromLatin1(entries[idx].name) + outputFile.mid(dot)
                : outputFile + QStringLiteral("_") + QString::fromLatin1(entries[idx].name);
            perClassSpec = (perClassFile + QStringLiteral(",") + (outputFormat.isEmpty() ? QStringLiteral("txt") : outputFormat)).toLocal8Bit();
            byteArgs << QByteArray("-o") << perClassSpec;
        }

        int perArgc = byteArgs.count();
        QVector<char*> perArgv;
        perArgv.reserve(perArgc);
        for (QByteArray &ba : byteArgs) {
            perArgv << ba.data();
        }

        status |= QTest::qExec(obj.data(), perArgc, perArgv.data());
    }
    return status;
}
#include "TestQNote.moc"
