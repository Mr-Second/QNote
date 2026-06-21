#pragma once

#include <QObject>
#include <QVariantList>

#include <SQLiteCpp/Database.h>

class CategoryManager : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList categories READ categories NOTIFY categoriesChanged)
public:
    explicit CategoryManager(QObject *parent = nullptr);

    void setDatabase(SQLite::Database *db);
    QVariantList categories() const;

    Q_INVOKABLE void loadCategories();
    Q_INVOKABLE bool addCategory(const QString &name, const QString &color, const QString &iconKey = QString());
    // 删除分类(同时删除该分类下所有便签)。name="全部"不可删除。
    Q_INVOKABLE bool deleteCategory(const QString &name);
    // 修改分类(名称/颜色/图标)。newName 重名(排除自身)返回 false。
    // 改名时同步更新 notes.category 字段。
    Q_INVOKABLE bool updateCategory(const QString &oldName, const QString &newName,
                                    const QString &color, const QString &iconKey);

signals:
    void categoriesChanged();

private:
    SQLite::Database *m_db = nullptr;
    QVariantList m_categories;
};
