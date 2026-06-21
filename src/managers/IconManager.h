#pragma once

#include <QObject>

class IconManager : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString iconsDir READ iconsDir CONSTANT)
public:
    explicit IconManager(QObject *parent = nullptr);

    QString iconsDir() const;

    // 复制用户选中的图标文件到 AppData/icons/,返回 iconKey("custom:<filename>")
    // 失败返回空字符串。相同 MD5 的文件复用(避免重复存储)
    Q_INVOKABLE QString importIcon(const QString &sourcePath);

    // 把 iconKey 解析为可用的 QUrl(file:/// 或 HusIcon 名)
    // iconKey 形如 "custom:abc.svg" 或 "StarOutlined" 或 ""
    Q_INVOKABLE QString resolveIconUrl(const QString &iconKey) const;

private:
    static QString _iconsDirPath();
    static QByteArray _fileMd5(const QString &path);
};
