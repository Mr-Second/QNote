#include "ImageManager.h"
#include "SettingsManager.h"

#include <QTextCursor>
#include <QTextDocument>
#include <QTextFragment>
#include <QQuickTextDocument>
#include <QFileDialog>
#include <QStandardPaths>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QCryptographicHash>
#include <QNetworkAccessManager>
#include <QNetworkReply>
#include <QNetworkRequest>
#include <QHttpMultiPart>
#include <QApplication>
#include <QClipboard>
#include <QMimeData>
#include <QImage>
#include <QBuffer>
#include <QJsonDocument>
#include <QJsonObject>
#include <QKeyEvent>

ImageManager::ImageManager(QObject *parent)
    : QObject(parent)
{
}

void ImageManager::setSettingsManager(SettingsManager *manager) { m_settings = manager; }

QString ImageManager::_toLocalImageUrl(const QString &filePath)
{
    return QUrl::fromLocalFile(QDir::fromNativeSeparators(filePath)).toString();
}

QString ImageManager::_toLocalFilePath(const QString &src)
{
    const QUrl url(src);
    if (url.isLocalFile())
        return url.toLocalFile();

    if (src.startsWith("file:///"))
        return src.mid(8);

    if (src.startsWith("file://"))
        return src.mid(7);

    return QDir::fromNativeSeparators(src);
}

void ImageManager::setTextArea(QQuickItem *textArea)
{
    if (m_textArea) m_textArea->removeEventFilter(this);
    m_textArea = textArea;
    if (m_textArea) m_textArea->installEventFilter(this);
}

void ImageManager::preloadImages()
{
    if (!m_textArea) {
        return;
    }

    auto *td = m_textArea->property("textDocument").value<QQuickTextDocument *>();
    if (!td) {
        return;
    }

    QTextDocument *doc = td->textDocument();
    if (!doc) return;

    for (QTextBlock block = doc->begin(); block != doc->end(); block = block.next()) {
        for (auto it = block.begin(); !(it.atEnd()); ++it) {
            QTextFragment fragment = it.fragment();
            if (!fragment.isValid()) continue;
            QTextCharFormat fmt = fragment.charFormat();
            if (!fmt.isImageFormat()) continue;

            QTextImageFormat imgFmt = fmt.toImageFormat();
            QString name = imgFmt.name();
            if (name.isEmpty()) continue;

            if (doc->resource(QTextDocument::ImageResource, QUrl(name)).isValid())
                continue;

            QString localPath = _toLocalFilePath(name);

            QImage img(localPath);
            if (!img.isNull()) {
                doc->addResource(QTextDocument::ImageResource, QUrl(name), img);
            }
        }
    }
}

bool ImageManager::eventFilter(QObject *watched, QEvent *event)
{
    if (event->type() == QEvent::KeyPress) {
        auto *ke = static_cast<QKeyEvent *>(event);
        if ((ke->key() == Qt::Key_V) && (ke->modifiers() & Qt::ControlModifier)) {
            auto *clipboard = QApplication::clipboard();
            auto *mimeData = clipboard->mimeData();
            if (mimeData && mimeData->hasImage()) {
                insertFromClipboard();
                return true;
            }
        }
    }
    return QObject::eventFilter(watched, event);
}

void ImageManager::insertFromFile(const QString &filePath)
{
    if (filePath.isEmpty()) return;

    if (!m_settings || m_settings->imageStorageMode() == 0) {
        QString localPath = _copyToLocal(filePath);
        if (!localPath.isEmpty())
            _insertHtmlImage(localPath);
    } else {
        _uploadToPicgo(filePath);
    }
}

void ImageManager::insertFromClipboard()
{
    if (!m_textArea) return;

    auto *clipboard = QApplication::clipboard();
    auto *mimeData = clipboard->mimeData();
    if (!mimeData || !mimeData->hasImage()) return;

    QImage image = clipboard->image();
    if (image.isNull()) return;

    QString imagesDir = QStandardPaths::writableLocation(QStandardPaths::AppDataLocation) + "/images";
    QDir().mkpath(imagesDir);

    QByteArray ba;
    QBuffer buffer(&ba);
    buffer.open(QIODevice::WriteOnly);
    image.save(&buffer, "PNG");
    buffer.close();

    QString hash = QCryptographicHash::hash(ba, QCryptographicHash::Md5).toHex();
    QString destPath = imagesDir + "/" + hash + ".png";
    image.save(destPath, "PNG");

    if (!m_settings || m_settings->imageStorageMode() == 0) {
        _insertHtmlImage(_toLocalImageUrl(destPath));
    } else {
        _uploadToPicgo(destPath);
    }
}

QString ImageManager::_copyToLocal(const QString &filePath)
{
    QString imagesDir = QStandardPaths::writableLocation(QStandardPaths::AppDataLocation) + "/images";
    QDir().mkpath(imagesDir);

    QFile file(filePath);
    if (!file.open(QIODevice::ReadOnly)) {
        qWarning() << "ImageManager: cannot open file" << filePath;
        return {};
    }

    QByteArray data = file.readAll();
    file.close();

    QString hash = QCryptographicHash::hash(data, QCryptographicHash::Md5).toHex();
    QFileInfo fi(filePath);
    QString destPath = imagesDir + "/" + hash + "." + fi.suffix();

    if (!QFile::exists(destPath)) {
        QFile::copy(filePath, destPath);
    }

    return _toLocalImageUrl(destPath);
}

void ImageManager::_uploadToPicgo(const QString &filePath)
{
    QString host = m_settings ? m_settings->picgoHost() : "127.0.0.1";
    int port = m_settings ? m_settings->picgoPort() : 36677;
    QUrl url(QString("http://%1:%2/upload").arg(host).arg(port));

    auto *manager = new QNetworkAccessManager(this);
    auto *multiPart = new QHttpMultiPart(QHttpMultiPart::FormDataType);
    multiPart->setParent(manager);

    QHttpPart imagePart;
    imagePart.setHeader(QNetworkRequest::ContentDispositionHeader,
        QVariant("form-data; name=\"file\"; filename=\"" + QFileInfo(filePath).fileName() + "\""));

    QFile *file = new QFile(filePath);
    if (!file->open(QIODevice::ReadOnly)) {
        qWarning() << "ImageManager: cannot open file for upload" << filePath;
        delete file;
        delete multiPart;
        manager->deleteLater();
        return;
    }
    imagePart.setBodyDevice(file);
    file->setParent(multiPart);
    multiPart->append(imagePart);

    QNetworkRequest request(url);
    auto *reply = manager->post(request, multiPart);

    connect(reply, &QNetworkReply::finished, this, [this, reply, manager]() {
        reply->deleteLater();
        manager->deleteLater();

        if (reply->error() != QNetworkReply::NoError) {
            qWarning() << "ImageManager: PicGo upload failed:" << reply->errorString();
            return;
        }

        QByteArray data = reply->readAll();
        QJsonDocument doc = QJsonDocument::fromJson(data);
        QJsonObject obj = doc.object();

        QString imageUrl;
        if (obj.contains("data") && obj["data"].isObject()) {
            imageUrl = obj["data"].toObject()["url"].toString();
        }
        if (imageUrl.isEmpty()) {
            qWarning() << "ImageManager: PicGo response missing URL:" << data;
            return;
        }

        _insertHtmlImage(imageUrl);
    });
}

void ImageManager::_insertHtmlImage(const QString &src)
{
    if (!m_textArea) return;

    auto *td = m_textArea->property("textDocument").value<QQuickTextDocument *>();
    if (!td) return;

    QTextDocument *doc = td->textDocument();
    if (!doc) return;

    qreal textWidth = m_textArea->width();
    int imgWidth = qRound(textWidth > 0 ? textWidth : 400);

    QString localPath = _toLocalFilePath(src);

    QImage img(localPath);
    if (!img.isNull()) {
        qreal scale = (textWidth > 0 ? textWidth : 400) / img.width();
        if (scale > 1.0) scale = 1.0;
        imgWidth = qRound(img.width() * scale);
    }

    int pos = m_textArea->property("cursorPosition").toInt();

    QTextCursor cursor(doc);
    cursor.setPosition(pos);

    QImage preloadImg(localPath);
    if (!preloadImg.isNull()) {
        doc->addResource(QTextDocument::ImageResource, QUrl(src), preloadImg);
    }

    QTextImageFormat imgFmt;
    imgFmt.setName(src);
    imgFmt.setWidth(imgWidth);
    cursor.insertImage(imgFmt);
}
