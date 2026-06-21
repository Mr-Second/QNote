#pragma once

#include <QObject>
#include <QQuickItem>
#include <QTextCursor>

class QTextDocument;
class SettingsManager;

class ImageManager : public QObject {
    Q_OBJECT
public:
    explicit ImageManager(QObject *parent = nullptr);

    void setSettingsManager(SettingsManager *manager);

    Q_INVOKABLE void setTextArea(QQuickItem *textArea);
    Q_INVOKABLE void insertFromFile(const QString &filePath);
    Q_INVOKABLE void insertFromClipboard();
    Q_INVOKABLE void preloadImages();

protected:
    bool eventFilter(QObject *watched, QEvent *event) override;

private:
    void _insertHtmlImage(const QString &src);
    QString _copyToLocal(const QString &filePath);
    void _uploadToPicgo(const QString &filePath);
    static QString _toLocalImageUrl(const QString &filePath);
    static QString _toLocalFilePath(const QString &src);

    QQuickItem *m_textArea = nullptr;
    SettingsManager *m_settings = nullptr;
};
