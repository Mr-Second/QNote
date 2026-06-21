#include "CategoryManager.h"

#include "Constants.h"

#include <QDebug>
#include <QDateTime>

#include <SQLiteCpp/Database.h>
#include <SQLiteCpp/Statement.h>

CategoryManager::CategoryManager(QObject *parent)
    : QObject(parent)
{
}

void CategoryManager::setDatabase(SQLite::Database *db)
{
    m_db = db;
}

QVariantList CategoryManager::categories() const
{
    return m_categories;
}

void CategoryManager::loadCategories()
{
    if (!m_db) return;

    m_categories.clear();
    m_categories.append(QVariantMap{
        {QStringLiteral("key"), Constants::defaultCategoryKey()},
        {QStringLiteral("name"), Constants::defaultCategory()},
        {QStringLiteral("color"), Constants::colorAll()}
    });

    try {
        SQLite::Statement query(*m_db,
            "SELECT name, color, iconKey, key FROM categories ORDER BY sortOrder ASC");
        while (query.executeStep()) {
            QString dbName = QString::fromStdString(query.getColumn(0).getString());
            QString dbKey = QString::fromStdString(query.getColumn(3).getString());
            // 内置分类（key 非空）：name 用当前语言翻译，保证切换语言后刷新
            // 用户自定义分类（key 空）：name 用 DB 存储的用户输入
            QString displayName = dbName;
            if (dbKey == Constants::workCategoryKey()) displayName = Constants::workCategory();
            else if (dbKey == Constants::lifeCategoryKey()) displayName = Constants::lifeCategory();
            else if (dbKey == Constants::importantCategoryKey()) displayName = Constants::importantCategory();

            QVariantMap entry{
                {QStringLiteral("name"), displayName},
                {QStringLiteral("color"), QString::fromStdString(query.getColumn(1).getString())},
                {QStringLiteral("icon"), QString::fromStdString(query.getColumn(2).getString())}
            };
            // key 非空时注入，供 QML 层逻辑判断（如 deletable/index>0）
            if (!dbKey.isEmpty()) entry.insert(QStringLiteral("key"), dbKey);
            m_categories.append(entry);
        }
    } catch (const std::exception &e) {
        qWarning() << "loadCategories failed:" << e.what();
    }

    emit categoriesChanged();
}

bool CategoryManager::addCategory(const QString &name, const QString &color, const QString &iconKey)
{
    if (!m_db) return false;

    try {
        auto now = QDateTime::currentDateTime().toString(Qt::ISODateWithMs);

        int maxSort = 0;
        {
            SQLite::Statement maxQuery(*m_db, "SELECT COALESCE(MAX(sortOrder), 0) FROM categories");
            if (maxQuery.executeStep())
                maxSort = maxQuery.getColumn(0).getInt();
        }

        SQLite::Statement query(*m_db,
            "INSERT INTO categories (name, color, iconKey, sortOrder, createdAt) VALUES (?, ?, ?, ?, ?)");
        query.bind(1, name.toStdString());
        query.bind(2, color.toStdString());
        query.bind(3, iconKey.toStdString());
        query.bind(4, maxSort + 1);
        query.bind(5, now.toStdString());
        query.exec();

        loadCategories();
        return true;
    } catch (const std::exception &e) {
        qWarning() << "addCategory failed:" << e.what();
        return false;
    }
}

bool CategoryManager::deleteCategory(const QString &name)
{
    if (!m_db) return false;
    if (name.isEmpty() || name == Constants::defaultCategoryKey()) return false;

    try {
        // 先删除该分类下所有便签
        {
            SQLite::Statement delNotes(*m_db, "DELETE FROM notes WHERE category = ?");
            delNotes.bind(1, name.toStdString());
            delNotes.exec();
        }
        // 再删除分类本身
        {
            SQLite::Statement delCat(*m_db, "DELETE FROM categories WHERE name = ?");
            delCat.bind(1, name.toStdString());
            delCat.exec();
        }
        loadCategories();
        return true;
    } catch (const std::exception &e) {
        qWarning() << "deleteCategory failed:" << e.what();
        return false;
    }
}

bool CategoryManager::updateCategory(const QString &oldName, const QString &newName,
                                     const QString &color, const QString &iconKey)
{
    if (!m_db) return false;
    if (oldName.isEmpty() || newName.trimmed().isEmpty()) return false;
    if (oldName == Constants::defaultCategoryKey()) return false;  // "全部"不可改

    QString trimmedNew = newName.trimmed();

    // 重名校验(排除自身)
    try {
        SQLite::Statement check(*m_db, "SELECT COUNT(*) FROM categories WHERE name = ? AND name != ?");
        check.bind(1, trimmedNew.toStdString());
        check.bind(2, oldName.toStdString());
        if (check.executeStep() && check.getColumn(0).getInt() > 0) {
            qWarning() << "updateCategory: name" << trimmedNew << "already exists";
            return false;
        }
    } catch (const std::exception &e) {
        qWarning() << "updateCategory check failed:" << e.what();
        return false;
    }

    try {
        // 更新分类
        SQLite::Statement updCat(*m_db,
            "UPDATE categories SET name = ?, color = ?, iconKey = ? WHERE name = ?");
        updCat.bind(1, trimmedNew.toStdString());
        updCat.bind(2, color.toStdString());
        updCat.bind(3, iconKey.toStdString());
        updCat.bind(4, oldName.toStdString());
        updCat.exec();

        // 改名时同步 notes.category
        if (oldName != trimmedNew) {
            SQLite::Statement updNotes(*m_db, "UPDATE notes SET category = ? WHERE category = ?");
            updNotes.bind(1, trimmedNew.toStdString());
            updNotes.bind(2, oldName.toStdString());
            updNotes.exec();
        }

        loadCategories();
        return true;
    } catch (const std::exception &e) {
        qWarning() << "updateCategory failed:" << e.what();
        return false;
    }
}
