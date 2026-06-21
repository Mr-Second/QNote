#include "TranslationManager.h"

#include <QCoreApplication>
#include <QDebug>
#include <QLocale>
#include <QQmlApplicationEngine>
#include <QTranslator>
#include <QDir>

TranslationManager::TranslationManager(QObject *parent)
    : QObject(parent)
{
}

TranslationManager::~TranslationManager()
{
    if (m_translator) {
        qApp->removeTranslator(m_translator);
        delete m_translator;
        m_translator = nullptr;
    }
}

void TranslationManager::setEngine(QQmlApplicationEngine *engine)
{
    m_engine = engine;
}

void TranslationManager::setTrayMenuBuilder(TrayMenuBuilder builder)
{
    m_trayMenuBuilder = builder;
}

QString TranslationManager::currentLanguage() const
{
    return m_currentLanguage;
}

QStringList TranslationManager::availableLanguages() const
{
    return {QStringLiteral("zh_CN"), QStringLiteral("en")};
}

bool TranslationManager::setLanguage(const QString &locale)
{
    if (locale == m_currentLanguage) return true;
    if (!availableLanguages().contains(locale)) {
        qWarning() << "TranslationManager: unsupported locale" << locale;
        return false;
    }

    // 切到源语言 zh_CN：移除现有 translator，不需要加载 .qm
    if (locale == QStringLiteral("zh_CN")) {
        if (m_translator) {
            qApp->removeTranslator(m_translator);
            delete m_translator;
            m_translator = nullptr;
        }
        m_currentLanguage = locale;
        if (m_engine) m_engine->retranslate();
        if (m_trayMenuBuilder) m_trayMenuBuilder();
        emit languageChanged();
        return true;
    }

    if (!loadAndInstall(locale)) {
        // 加载失败：translator 已被 loadAndInstall 移除，UI 实际回落到源语言（zh_CN）。
        // 同步 m_currentLanguage 标志 + retranslate + emit 信号，让 UI 与状态一致。
        m_currentLanguage = QStringLiteral("zh_CN");
        if (m_engine) m_engine->retranslate();
        if (m_trayMenuBuilder) m_trayMenuBuilder();
        emit languageChanged();
        return false;
    }

    m_currentLanguage = locale;
    if (m_engine) m_engine->retranslate();
    if (m_trayMenuBuilder) m_trayMenuBuilder();
    emit languageChanged();
    return true;
}

void TranslationManager::initialize(const QString &savedLocale)
{
    QString locale = savedLocale;
    if (locale.isEmpty()) locale = systemLanguage();
    // 源语言（zh_CN）不需要 translator，找不到 .qm 属正常：显示原文即中文
    m_currentLanguage = locale;
    if (locale == QStringLiteral("zh_CN")) {
        qDebug() << "TranslationManager: source language zh_CN, no translator needed";
        return;
    }
    if (!loadAndInstall(locale)) {
        // 加载失败回退到源语言（zh_CN），确保应用可用
        qWarning() << "TranslationManager: load failed for" << locale << ", fallback to zh_CN";
        m_currentLanguage = QStringLiteral("zh_CN");
    }
}

QString TranslationManager::systemLanguage()
{
    return QLocale::system().language() == QLocale::Chinese
        ? QStringLiteral("zh_CN")
        : QStringLiteral("en");
}

bool TranslationManager::loadAndInstall(const QString &locale)
{
    QString dir = qmDir();
    QString filename = QStringLiteral("qnote_") + locale;

    if (m_translator) {
        qApp->removeTranslator(m_translator);
        delete m_translator;
        m_translator = nullptr;
    }

    m_translator = new QTranslator(this);
    if (!m_translator->load(filename, dir)) {
        qWarning() << "TranslationManager: failed to load" << filename << "from" << dir;
        delete m_translator;
        m_translator = nullptr;
        return false;
    }
    qApp->installTranslator(m_translator);
    qDebug() << "TranslationManager: loaded" << filename << "from" << dir;
    return true;
}

QString TranslationManager::qmDir() const
{
    return QCoreApplication::applicationDirPath() + QDir::separator() + QStringLiteral("i18n");
}
