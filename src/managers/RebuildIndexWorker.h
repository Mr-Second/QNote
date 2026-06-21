#pragma once

#include <QObject>
#include <QString>

// 异步搜索索引重建 worker。
// 在子线程中创建独立的 NoteDatabase 和 SearchManager 实例，写入临时索引目录，
// 完成后通过 finished 信号通知主线程（由主线程执行 rebuildAllSwap）。
//
// 线程安全：Xapian WritableDatabase 和 SQLiteCpp Database 都不是线程安全的，
// 因此本 worker 必须使用自己独立创建的实例，绝不访问主线程对象。
class RebuildIndexWorker : public QObject {
    Q_OBJECT
public:
    explicit RebuildIndexWorker(const QString &dbPath, const QString &tmpIndexPath, QObject *parent = nullptr);

public slots:
    void run();

signals:
    void finished(bool success);

private:
    QString m_dbPath;
    QString m_tmpIndexPath;
};
