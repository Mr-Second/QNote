#include "TextFormatHelper.h"

#include <QColor>
#include <QTextCursor>
#include <QTextCharFormat>
#include <QTextDocument>
#include <QTextList>
#include <QQuickTextDocument>
#include <QVariant>
#include <QDebug>
#include <QApplication>
#include <QClipboard>
#include <QMimeData>

TextFormatHelper::TextFormatHelper(QObject *parent)
    : QObject(parent)
{
}

void TextFormatHelper::setTextArea(QQuickItem *textArea)
{
    m_textArea = textArea;
    refreshFormatState();
}

QTextDocument *TextFormatHelper::_getDocument() const
{
    if (!m_textArea) return nullptr;
    QVariant v = m_textArea->property("textDocument");
    if (!v.isValid()) {
        qWarning() << "TextFormatHelper: textDocument property not found";
        return nullptr;
    }
    auto *td = v.value<QQuickTextDocument *>();
    if (!td) {
        qWarning() << "TextFormatHelper: failed to cast to QQuickTextDocument*";
        return nullptr;
    }
    return td->textDocument();
}

QTextCursor TextFormatHelper::_getCursor() const
{
    QTextDocument *doc = _getDocument();
    if (!doc) return QTextCursor();

    // 真实文档长度，含末尾 block separator（RichText 模式比 QML TextEdit.length 多 1）
    int docLen = doc->characterCount();
    int maxPos = qMax(0, docLen - 1);  // cursor 有效位置范围 [0, docLen-1]

    // 直接读 QML 实时选区。persistentSelection: true 保证失焦场景（如点工具栏按钮）
    // 选区不丢失；键盘 Ctrl+V/X 本身不夺焦点，选区始终有效。
    int start = m_textArea ? m_textArea->property("selectionStart").toInt() : -1;
    int end = m_textArea ? m_textArea->property("selectionEnd").toInt() : -1;

    // clamp 到真实文档范围，避免 QTextCursor::setPosition 越界
    start = qBound(0, start, maxPos);
    end = qBound(0, end, maxPos);

    QTextCursor cursor(doc);
    if (start != end) {
        cursor.setPosition(qMin(start, end));
        cursor.setPosition(qMax(start, end), QTextCursor::KeepAnchor);
    } else {
        int curPos = m_textArea ? m_textArea->property("cursorPosition").toInt() : start;
        cursor.setPosition(qBound(0, curPos, maxPos));
    }
    return cursor;
}

void TextFormatHelper::_mergeCharFormat(const QTextCharFormat &format)
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    cursor.mergeCharFormat(format);
    refreshFormatState();
}

void TextFormatHelper::_updateFormatState(const QTextCharFormat &format)
{
    // Qt 6.9.3 QTextCharFormat::fontFamily() dereferences a null pointer when
    // FontFamily property is unset (e.g. empty new document's default format).
    // Use hasProperty + property to avoid the crash.
    // Fallback 对齐 SettingsManager 默认编辑器字体（曾用打包的 "Source Han Sans SC"，
    // 现改为 Windows 系统字体 "Microsoft YaHei"，见 06-21-system-font-default 任务）
    QString nextFontFamily = QStringLiteral("Microsoft YaHei");
    if (format.hasProperty(QTextFormat::FontFamily)) {
        const QVariant famVar = format.property(QTextFormat::FontFamily);
        if (famVar.isValid() && famVar.canConvert<QString>()) {
            const QString famStr = famVar.toString();
            if (!famStr.isEmpty())
                nextFontFamily = famStr;
        }
    }
    int nextFontPixelSize = 16;
    if (format.hasProperty(QTextFormat::FontPixelSize)) {
        bool ok = false;
        const int size = format.property(QTextFormat::FontPixelSize).toInt(&ok);
        if (ok && size > 0)
            nextFontPixelSize = size;
    }
    QString nextTextColor = QStringLiteral("#473C35");
    if (format.hasProperty(QTextFormat::ForegroundBrush)) {
        const QBrush fg = format.foreground();
        if (fg.style() != Qt::NoBrush && fg.color().isValid())
            nextTextColor = fg.color().name(QColor::HexRgb);
    }

    if (m_currentFontFamily == nextFontFamily
        && m_currentFontPixelSize == nextFontPixelSize
        && m_currentTextColor == nextTextColor) {
        return;
    }

    m_currentFontFamily = nextFontFamily;
    m_currentFontPixelSize = nextFontPixelSize;
    m_currentTextColor = nextTextColor;
    emit formatStateChanged();
}

void TextFormatHelper::toggleBold()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextCharFormat fmt = cursor.charFormat();
    fmt.setFontWeight(fmt.fontWeight() == QFont::Bold ? QFont::Normal : QFont::Bold);
    _mergeCharFormat(fmt);
}

void TextFormatHelper::toggleItalic()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextCharFormat fmt = cursor.charFormat();
    fmt.setFontItalic(!fmt.fontItalic());
    _mergeCharFormat(fmt);
}

void TextFormatHelper::toggleUnderline()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextCharFormat fmt = cursor.charFormat();
    fmt.setFontUnderline(!fmt.fontUnderline());
    _mergeCharFormat(fmt);
}

void TextFormatHelper::toggleStrikeout()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextCharFormat fmt = cursor.charFormat();
    fmt.setFontStrikeOut(!fmt.fontStrikeOut());
    _mergeCharFormat(fmt);
}

void TextFormatHelper::insertUnorderedList()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextListFormat listFmt;
    listFmt.setStyle(QTextListFormat::ListDisc);
    cursor.createList(listFmt);
    refreshFormatState();
}

void TextFormatHelper::insertOrderedList()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    QTextListFormat listFmt;
    listFmt.setStyle(QTextListFormat::ListDecimal);
    cursor.createList(listFmt);
    refreshFormatState();
}

void TextFormatHelper::setAlignment(int alignment)
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    cursor.movePosition(QTextCursor::StartOfBlock);
    cursor.movePosition(QTextCursor::EndOfBlock, QTextCursor::KeepAnchor);

    QTextBlockFormat fmt = cursor.blockFormat();
    fmt.setAlignment(static_cast<Qt::AlignmentFlag>(alignment));
    cursor.mergeBlockFormat(fmt);
    refreshFormatState();
}

void TextFormatHelper::setFontFamily(const QString &family)
{
    if (family.trimmed().isEmpty()) return;

    QTextCharFormat fmt = _getCursor().charFormat();
    fmt.setFontFamily(family);
    _mergeCharFormat(fmt);
}

void TextFormatHelper::setFontPixelSize(int size)
{
    if (size <= 0) return;

    QTextCharFormat fmt = _getCursor().charFormat();
    fmt.setProperty(QTextFormat::FontPixelSize, size);
    _mergeCharFormat(fmt);
}

void TextFormatHelper::setTextColor(const QString &color)
{
    QColor parsed(color);
    if (!parsed.isValid()) return;

    QTextCharFormat fmt = _getCursor().charFormat();
    fmt.setForeground(parsed);
    _mergeCharFormat(fmt);
}

void TextFormatHelper::refreshFormatState()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    _updateFormatState(cursor.charFormat());
}

void TextFormatHelper::clearCharFormat()
{
    if (!m_textArea) return;

    // 关键认知:_getCursor() 创建的是临时 QTextCursor,QTextCursor::setCharFormat
    // 不会更新 QML TextEdit 内部 cursor 的 currentCharFormat(后者用于后续输入)。
    //
    // 方案:用 doc->setHtml("") 重建文档模型,触发 TextEdit 重置 cursor.currentCharFormat,
    // 然后对 block 0 显式清格式做双保险。调用场景是 editorText=="",文档本就该空。

    QTextDocument *doc = _getDocument();
    if (!doc) return;

    doc->setHtml(QStringLiteral(""));

    QTextCursor cursor(doc);
    cursor.movePosition(QTextCursor::Start);

    QTextCharFormat cleanCharFmt;
    cleanCharFmt.clearBackground();
    cleanCharFmt.clearForeground();
    cursor.setCharFormat(cleanCharFmt);

    QTextBlockFormat cleanBlockFmt;
    cleanBlockFmt.clearBackground();
    cursor.setBlockFormat(cleanBlockFmt);

    m_textArea->setProperty("cursorPosition", 0);
    m_textArea->setProperty("selectionStart", 0);
    m_textArea->setProperty("selectionEnd", 0);

    refreshFormatState();
}

void TextFormatHelper::insertCleanParagraph()
{
    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    // insertBlock 会继承前段格式,所以插入后必须显式覆盖新 block + cursor.currentCharFormat。

    QTextCharFormat cleanCharFmt;
    cleanCharFmt.clearBackground();
    cleanCharFmt.clearForeground();

    QTextBlockFormat cleanBlockFmt;
    cleanBlockFmt.clearBackground();

    cursor.insertBlock(cleanBlockFmt, cleanCharFmt);

    cursor.setCharFormat(cleanCharFmt);
    cursor.setBlockFormat(cleanBlockFmt);

    cursor.movePosition(QTextCursor::EndOfBlock);
    if (m_textArea) {
        int newPos = cursor.position();
        int docLen = cursor.document() ? cursor.document()->characterCount() : newPos + 1;
        int maxPos = qMax(0, docLen - 1);
        newPos = qBound(0, newPos, maxPos);
        m_textArea->setProperty("cursorPosition", newPos);
        m_textArea->setProperty("selectionStart", newPos);
        m_textArea->setProperty("selectionEnd", newPos);
    }

    refreshFormatState();
}

QString TextFormatHelper::currentFontFamily() const
{
    return m_currentFontFamily;
}

int TextFormatHelper::currentFontPixelSize() const
{
    return m_currentFontPixelSize;
}

QString TextFormatHelper::currentTextColor() const
{
    return m_currentTextColor;
}

void TextFormatHelper::pastePlainText()
{
    if (!m_textArea) return;
    auto *clipboard = QApplication::clipboard();
    const QMimeData *mimeData = clipboard->mimeData();
    if (!mimeData || !mimeData->hasText() || mimeData->hasImage()) return;

    QTextCursor cursor = _getCursor();
    if (cursor.isNull()) return;

    if (cursor.hasSelection()) {
        cursor.removeSelectedText();
    }

    cursor.insertText(mimeData->text());

    if (m_textArea) {
        int newPos = cursor.position();
        m_textArea->setProperty("cursorPosition", newPos);
        m_textArea->setProperty("selectionStart", newPos);
        m_textArea->setProperty("selectionEnd", newPos);
    }

    refreshFormatState();
}
