#pragma once

#include <QObject>
#include <QVariantList>

class ColorPaletteProvider : public QObject {
    Q_OBJECT
    Q_PROPERTY(QVariantList colors READ colors NOTIFY colorsChanged)
public:
    explicit ColorPaletteProvider(QObject *parent = nullptr);

    QVariantList colors() const;

    Q_INVOKABLE void load();

signals:
    void colorsChanged();

private:
    QVariantList m_colors;
};
