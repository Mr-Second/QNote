#pragma once

#include <QObject>
#include <QQuickItem>
#include <QTextCursor>

class QTextDocument;

class TextFormatHelper : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString currentFontFamily READ currentFontFamily NOTIFY formatStateChanged)
    Q_PROPERTY(int currentFontPixelSize READ currentFontPixelSize NOTIFY formatStateChanged)
    Q_PROPERTY(QString currentTextColor READ currentTextColor NOTIFY formatStateChanged)
public:
    explicit TextFormatHelper(QObject *parent = nullptr);

    Q_INVOKABLE void setTextArea(QQuickItem *textArea);
    Q_INVOKABLE void toggleBold();
    Q_INVOKABLE void toggleItalic();
    Q_INVOKABLE void toggleUnderline();
    Q_INVOKABLE void toggleStrikeout();
    Q_INVOKABLE void insertUnorderedList();
    Q_INVOKABLE void insertOrderedList();
    Q_INVOKABLE void setAlignment(int alignment);
    Q_INVOKABLE void setFontFamily(const QString &family);
    Q_INVOKABLE void setFontPixelSize(int size);
    Q_INVOKABLE void setTextColor(const QString &color);
    Q_INVOKABLE void refreshFormatState();

    // 清除当前 cursor 位置的 charFormat/blockFormat 残留(背景色/字体等),
    // 用于新建便签或 Ctrl+A 删除后,防止上次粘贴的格式污染后续输入。
    Q_INVOKABLE void clearCharFormat();

    // 插入一个格式干净的新段落(blockFormat/charFormat 均为默认),
    // 用于 Ctrl+Enter:粘贴段末尾换行时,新段落不继承上段的背景色残留。
    Q_INVOKABLE void insertCleanParagraph();

    // 粘贴纯文本（去格式）。Ctrl+V/C/X 由 TextEdit 原生处理，无需 C++。
    Q_INVOKABLE void pastePlainText();

    QString currentFontFamily() const;
    int currentFontPixelSize() const;
    QString currentTextColor() const;

signals:
    void formatStateChanged();

private:
    QTextDocument *_getDocument() const;
    QTextCursor _getCursor() const;
    void _mergeCharFormat(const QTextCharFormat &format);
    void _updateFormatState(const QTextCharFormat &format);

    QQuickItem *m_textArea = nullptr;
    QString m_currentFontFamily;
    int m_currentFontPixelSize = 16;
    QString m_currentTextColor = QStringLiteral("#473C35");
};
