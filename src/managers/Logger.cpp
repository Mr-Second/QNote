#include "Logger.h"

#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QMutex>
#include <QMutexLocker>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QTextStream>

#include <cstdio>

namespace {

QMutex g_logMutex;
QString g_logDir;
QString g_currentLogFilePath;
QDate g_currentDate;
QFile g_logFile;

QString _logFilePathForDate(const QDate &date)
{
    return g_logDir + QDir::separator()
           + QStringLiteral("qnote-") + date.toString(QStringLiteral("yyyyMMdd"))
           + QStringLiteral(".log");
}

void _openLogFile(const QDate &date)
{
    if (g_logFile.isOpen()) {
        g_logFile.close();
    }
    g_currentLogFilePath = _logFilePathForDate(date);
    g_logFile.setFileName(g_currentLogFilePath);
    if (!g_logFile.open(QIODevice::WriteOnly | QIODevice::Append | QIODevice::Text)) {
        g_currentLogFilePath.clear();
        fprintf(stderr, "Logger: failed to open log file, falling back to stderr only\n");
        fflush(stderr);
    }
}

char _levelChar(QtMsgType type)
{
    switch (type) {
    case QtDebugMsg:    return 'D';
    case QtInfoMsg:     return 'I';
    case QtWarningMsg:  return 'W';
    case QtCriticalMsg: return 'C';
    case QtFatalMsg:    return 'F';
    default:            return '?';
    }
}

void _cleanupOldLogs(const QString &logDir)
{
    const QDate cutoff = QDate::currentDate().addDays(-7);
    QDir dir(logDir);
    const QFileInfoList files = dir.entryInfoList({QStringLiteral("qnote-*.log")}, QDir::Files);
    static const QRegularExpression re(QStringLiteral("qnote-(\\d{8})\\.log"));
    for (const QFileInfo &fi : files) {
        const QRegularExpressionMatch m = re.match(fi.fileName());
        if (m.hasMatch()) {
            const QDate fileDate = QDate::fromString(m.captured(1), QStringLiteral("yyyyMMdd"));
            if (fileDate.isValid() && fileDate < cutoff) {
                QFile::remove(fi.absoluteFilePath());
            }
        }
    }
}

void messageHandler(QtMsgType type, const QMessageLogContext &ctx, const QString &msg)
{
    Q_UNUSED(ctx);

    const QDate today = QDate::currentDate();
    if (today != g_currentDate) {
        QMutexLocker locker(&g_logMutex);
        if (today != g_currentDate) {
            _openLogFile(today);
            g_currentDate = today;
        }
    }

    const QString line = QDateTime::currentDateTime().toString(QStringLiteral("HH:mm:ss.zzz"))
                        + QStringLiteral(" [") + QChar::fromLatin1(_levelChar(type))
                        + QStringLiteral("] ") + msg;

    {
        QMutexLocker locker(&g_logMutex);
        if (g_logFile.isOpen()) {
            QTextStream ts(&g_logFile);
            ts << line << QStringLiteral("\n");
            ts.flush();
        }
    }

    fprintf(stderr, "%s\n", qPrintable(line));
    fflush(stderr);
}

} // namespace

namespace Logger {

void install()
{
    QMutexLocker locker(&g_logMutex);

    // NOTE: Logger::install() runs before QApplication is constructed, so
    // QStandardPaths::writableLocation(AppDataLocation) is unreliable here
    // (organization/application name not set yet). Resolve AppData directly
    // from the environment to keep early logs available.
    const QString appData = QString::fromWCharArray(_wgetenv(L"APPDATA"));
    if (appData.isEmpty()) {
        fprintf(stderr, "Logger: APPDATA env var is empty, falling back to stderr only\n");
        fflush(stderr);
        return;
    }
    g_logDir = appData + QStringLiteral("/QNote/QNote/logs");

    QDir().mkpath(g_logDir);
    if (!QDir(g_logDir).exists()) {
        fprintf(stderr, "Logger: failed to create log dir: %s\n", qPrintable(g_logDir));
        fflush(stderr);
        return;
    }

    _cleanupOldLogs(g_logDir);

    _openLogFile(QDate::currentDate());
    g_currentDate = QDate::currentDate();

    qInstallMessageHandler(messageHandler);
}

QString currentLogFilePath()
{
    QMutexLocker locker(&g_logMutex);
    return g_currentLogFilePath;
}

} // namespace Logger
