// QNoteRelauncher: 临时辅助进程，用于 QNote 重启和（未来）热更新。
//
// 工作模式（B2 临时 launcher，参考 VSCode squirrel / Electron updater）：
//   1. QNote.exe（主程序）需要重启时，启动 QNoteRelauncher.exe 并立即退出
//   2. Relauncher 等待指定的旧 PID 退出（确保锁释放）
//   3. Relauncher 启动新的 QNote.exe 实例
//   4. Relauncher 自己退出（非常驻）
//
// 命令行语法：
//   QNoteRelauncher.exe --wait-pid <pid> [--timeout <秒>] --launch <exe> [--launch-arg <arg>]...
//
// 退出码：
//   0 = 成功（成功启动新实例或超时后尽力启动）
//   1 = 参数错误
//   2 = wait-pid 无效
//   3 = launch exe 不存在或启动失败

#include <QCoreApplication>
#include <QProcess>
#include <QThread>
#include <QDebug>
#include <QFileInfo>
#include <QFile>
#include <QTextStream>
#include <QDateTime>
#include <QDir>
#include <QStandardPaths>

#ifdef Q_OS_WIN
// windows.h 必须在 Qt 头之后包含（Qt 用 #define NOMINMAX 等避免冲突）
// WIN32_LEAN_AND_MEAN 排除一些冷门 API，但保留进程/句柄相关
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
// SYNCHRONIZE (0x00100000) 是 WaitForSingleObject 需要的访问权限
// 某些 SDK 里 PROCESS_SYNCHRONIZE 未定义，直接用 SYNCHRONIZE
#ifndef SYNCHRONIZE
#define SYNCHRONIZE 0x00100000L
#endif
#endif

// Relauncher 日志写入文件（独立进程，qDebug 默认无去处）
// 写入 %APPDATA%/QNote/QNote/logs/relauncher.log
static QFile s_logFile;
static void logMessage(const QString &msg)
{
    if (!s_logFile.isOpen()) return;
    QTextStream ts(&s_logFile);
    ts << QDateTime::currentDateTime().toString("HH:mm:ss.zzz") << " " << msg << "\n";
    ts.flush();
}

int main(int argc, char *argv[])
{
    // 必须在 QCoreApplication 构造前设置，QStandardPaths 依赖
    QCoreApplication::setOrganizationName("QNote");
    QCoreApplication::setApplicationName("QNoteRelauncher");

    QCoreApplication app(argc, argv);

    // 打开日志文件（写入 QNote 日志目录，便于排查）
    // 用 GenericDataLocation + 手动拼路径，避免 AppName 影响
    {
        QString appData = QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation);
        // GenericDataLocation = %APPDATA% on Windows
        QString logDir = appData + "/QNote/QNote/logs";
        QDir().mkpath(logDir);
        QString logPath = logDir + "/relauncher.log";
        s_logFile.setFileName(logPath);
        if (s_logFile.open(QIODevice::WriteOnly | QIODevice::Append | QIODevice::Text)) {
            logMessage(QStringLiteral("=== QNoteRelauncher start (pid=%1) logPath=%2 ===").arg(
                QString::number(app.applicationPid()), logPath));
        }
    }

    const QStringList args = app.arguments();
    logMessage(QStringLiteral("Arguments: %1").arg(args.join(" ")));

    qint64 waitPid = -1;
    int timeoutSec = 30;
    QString launchExe;
    QStringList launchArgs;

    // 简单命令行解析（不引入 QCommandLineParser，保持依赖最小）
    for (int i = 1; i < args.size(); ++i) {
        const QString &arg = args[i];
        if (arg == "--wait-pid" && i + 1 < args.size()) {
            waitPid = args[++i].toLongLong();
        } else if (arg == "--timeout" && i + 1 < args.size()) {
            timeoutSec = args[++i].toInt();
        } else if (arg == "--launch" && i + 1 < args.size()) {
            launchExe = args[++i];
        } else if (arg == "--launch-arg" && i + 1 < args.size()) {
            launchArgs.append(args[++i]);
        } else if (arg == "--verbose") {
            // 静默运行，日志输出到 QNote 日志目录外（relauncher 无独立日志）
        }
    }

    logMessage(QStringLiteral("Parsed: waitPid=%1 timeout=%2 launch=%3 args=%4")
        .arg(QString::number(waitPid))
        .arg(QString::number(timeoutSec))
        .arg(launchExe)
        .arg(launchArgs.join(" ")));

    // 参数校验
    if (launchExe.isEmpty()) {
        logMessage(QStringLiteral("ERROR: Missing --launch argument"));
        return 1;
    }
    // 路径存在性检查：对绝对路径用 QFileInfo::exists，对相对/裸名用 findExecutable（搜索 PATH）
    QString resolvedExe = launchExe;
    if (!QFileInfo(launchExe).isAbsolute()) {
        resolvedExe = QStandardPaths::findExecutable(launchExe);
    }
    if (!QFileInfo::exists(resolvedExe)) {
        logMessage(QStringLiteral("ERROR: Launch target does not exist: %1 (resolved: %2)").arg(launchExe, resolvedExe));
        return 3;
    }
    launchExe = resolvedExe;

    // 阶段 1：等待旧进程退出（释放文件锁、DB 锁、search_index 锁）
#ifdef Q_OS_WIN
    if (waitPid > 0) {
        // Windows: OpenProcess 拿句柄，WaitForSingleObject 阻塞等待
        // 相比 QProcess::waitForFinished（需要进程是当前进程的子进程），
        // OpenProcess 可以等待任意进程。权限 SYNCHRONIZE 足够等待用。
        logMessage(QStringLiteral("Opening process handle for pid %1").arg(QString::number(waitPid)));
        HANDLE hProc = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(waitPid));
        if (hProc == nullptr) {
            DWORD err = GetLastError();
            if (err != ERROR_INVALID_PARAMETER) {
                logMessage(QStringLiteral("OpenProcess failed pid=%1 err=%2 (assuming exited)").arg(QString::number(waitPid)).arg(QString::number(err)));
            } else {
                logMessage(QStringLiteral("Process %1 already exited (ERROR_INVALID_PARAMETER)").arg(QString::number(waitPid)));
            }
        } else {
            DWORD timeoutMs = (timeoutSec > 0) ? (timeoutSec * 1000) : INFINITE;
            logMessage(QStringLiteral("Waiting for pid %1 (timeout=%2 ms)...").arg(QString::number(waitPid)).arg(QString::number(timeoutMs)));
            DWORD waitResult = WaitForSingleObject(hProc, timeoutMs);
            CloseHandle(hProc);
            QString resultStr;
            if (waitResult == WAIT_TIMEOUT) resultStr = "TIMEOUT";
            else if (waitResult == WAIT_FAILED) resultStr = QString("FAILED (err=%1)").arg(GetLastError());
            else if (waitResult == WAIT_OBJECT_0) resultStr = "OBJECT_0 (exited)";
            else if (waitResult == WAIT_ABANDONED) resultStr = "ABANDONED";
            else resultStr = QString::number(waitResult);
            logMessage(QStringLiteral("WaitForSingleObject returned %1").arg(resultStr));
        }
    }
#else
    Q_UNUSED(waitPid)
    Q_UNUSED(timeoutSec)
#endif

    // 阶段 2：启动新实例
    // 给一点缓冲时间确保 OS 完全回收旧进程资源（句柄、文件锁等）
    QThread::msleep(50);

    logMessage(QStringLiteral("Launching new instance: %1 %2").arg(launchExe).arg(launchArgs.join(" ")));
    if (!QProcess::startDetached(launchExe, launchArgs)) {
        logMessage(QStringLiteral("ERROR: startDetached failed for %1").arg(launchExe));
        return 3;
    }

    logMessage(QStringLiteral("Relauncher done, exiting"));
    return 0;
}
