#pragma once

#include <functional>
#include <QObject>
#include <QString>
#include <QStringList>

class QQmlApplicationEngine;
class QTranslator;

class TranslationManager : public QObject {
    Q_OBJECT
    Q_PROPERTY(QString currentLanguage READ currentLanguage NOTIFY languageChanged)
    Q_PROPERTY(QStringList availableLanguages READ availableLanguages CONSTANT)
public:
    explicit TranslationManager(QObject *parent = nullptr);
    ~TranslationManager() override;

    void setEngine(QQmlApplicationEngine *engine);
    using TrayMenuBuilder = std::function<void()>;
    void setTrayMenuBuilder(TrayMenuBuilder builder);

    QString currentLanguage() const;
    QStringList availableLanguages() const;

    Q_INVOKABLE bool setLanguage(const QString &locale);
    void initialize(const QString &savedLocale = QString());

    static QString systemLanguage();

signals:
    void languageChanged();

private:
    bool loadAndInstall(const QString &locale);
    QString qmDir() const;

    QTranslator *m_translator = nullptr;
    QQmlApplicationEngine *m_engine = nullptr;
    TrayMenuBuilder m_trayMenuBuilder;
    QString m_currentLanguage;
};
