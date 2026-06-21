#include "ColorPaletteProvider.h"

#include <QFile>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>

ColorPaletteProvider::ColorPaletteProvider(QObject *parent)
    : QObject(parent)
{
}

QVariantList ColorPaletteProvider::colors() const
{
    return m_colors;
}

void ColorPaletteProvider::load()
{
    QFile file(QStringLiteral(":/assets/colors.json"));
    if (!file.open(QIODevice::ReadOnly)) {
        return;
    }

    const auto doc = QJsonDocument::fromJson(file.readAll());
    if (!doc.isArray()) {
        return;
    }

    QVariantList nextColors;
    const auto array = doc.array();
    nextColors.reserve(array.size());

    for (const auto &value : array) {
        if (!value.isObject()) {
            continue;
        }

        const auto object = value.toObject();
        const QString name = object.value(QStringLiteral("name")).toString().trimmed();
        const QString hex = object.value(QStringLiteral("hex")).toString().trimmed();
        if (name.isEmpty() || hex.isEmpty()) {
            continue;
        }

        QVariantMap color;
        color.insert(QStringLiteral("name"), name);
        color.insert(QStringLiteral("hex"), hex);
        color.insert(QStringLiteral("pinyin"), object.value(QStringLiteral("pinyin")).toString());
        nextColors.push_back(color);
    }

    if (m_colors == nextColors) {
        return;
    }

    m_colors = nextColors;
    emit colorsChanged();
}
