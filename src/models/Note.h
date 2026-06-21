#pragma once

#include "Constants.h"

#include <QDateTime>
#include <QString>
#include <QTextDocument>

struct Note {
    qint64 id = -1;
    QString uuid;
    QString title;
    QString content;
    QString category;
    QDateTime createdAt;
    QDateTime updatedAt;
    bool titleManual = false;
    int sortOrder = 0;

    static QString stripHtml(const QString &html) {
        if (html.isEmpty())
            return {};
        if (!html.startsWith(QLatin1Char('<')))
            return html.simplified();
        QTextDocument doc;
        doc.setHtml(html);
        return doc.toPlainText().simplified();
    }

    static QString generateTitle(const QString &content) {
        QString plain = stripHtml(content);
        if (plain.isEmpty())
            return Constants::newNoteTitle();
        constexpr int kMaxLen = 20;
        if (plain.length() > kMaxLen)
            return plain.left(kMaxLen) + QStringLiteral("…");
        return plain;
    }
};
