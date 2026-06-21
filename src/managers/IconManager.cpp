#include "IconManager.h"

#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QSet>
#include <QStandardPaths>
#include <QUrl>

IconManager::IconManager(QObject *parent)
    : QObject(parent)
{
}

QString IconManager::iconsDir() const
{
    return _iconsDirPath();
}

QString IconManager::importIcon(const QString &sourcePath)
{
    if (sourcePath.isEmpty()) return QString();

    QFileInfo src(sourcePath);
    if (!src.exists() || !src.isFile()) return QString();

    QString suffix = src.suffix().toLower();
    // Qt QML Image 支持的图片格式
    static const QSet<QString> kAllowedSuffixes = {
        QStringLiteral("svg"), QStringLiteral("png"), QStringLiteral("jpg"),
        QStringLiteral("jpeg"), QStringLiteral("gif"), QStringLiteral("bmp"),
        QStringLiteral("ico"), QStringLiteral("webp")
    };
    if (!kAllowedSuffixes.contains(suffix)) {
        return QString();
    }

    QString dir = _iconsDirPath();
    QDir().mkpath(dir);

    // MD5 去重:相同内容文件复用已存的
    QByteArray md5 = _fileMd5(sourcePath);
    QString filename = QString::fromLatin1(md5.toHex()) + QStringLiteral(".") + suffix;

    QString target = dir + QDir::separator() + filename;
    if (!QFile::exists(target)) {
        if (!QFile::copy(sourcePath, target)) {
            return QString();
        }
    }

    return QStringLiteral("custom:") + filename;
}

QString IconManager::resolveIconUrl(const QString &iconKey) const
{
    if (iconKey.isEmpty()) return QString();

    if (iconKey.startsWith(QStringLiteral("custom:"))) {
        QString filename = iconKey.mid(7);  // "custom:" 长度 7
        QString path = _iconsDirPath() + QDir::separator() + filename;
        return QUrl::fromLocalFile(path).toString();
    }

    // 非自定义:iconKey 是 HusIcon 枚举名,QML 侧用 HusIcon[iconKey] 查找
    return iconKey;
}

QString IconManager::_iconsDirPath()
{
    return QStandardPaths::writableLocation(QStandardPaths::AppDataLocation)
           + QDir::separator() + QStringLiteral("icons");
}

QByteArray IconManager::_fileMd5(const QString &path)
{
    QFile f(path);
    if (!f.open(QIODevice::ReadOnly)) return QByteArray();
    return QCryptographicHash::hash(f.readAll(), QCryptographicHash::Md5);
}
