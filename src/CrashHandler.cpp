#include "CrashHandler.h"

#include "managers/Logger.h"

#include <QtGlobal>
#include <QCoreApplication>
#include <QDateTime>
#include <QDir>
#include <QFile>
#include <QStandardPaths>
#include <QString>
#include <QTextStream>
#include <QSysInfo>
#include <QVersionNumber>

#include <windows.h>
#include <dbghelp.h>
#include <psapi.h>

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <new.h>
#include <vector>

#ifndef QNOTE_VERSION
#define QNOTE_VERSION "unknown"
#endif

#ifndef QNOTE_BUILD_DATE
#define QNOTE_BUILD_DATE __DATE__ " " __TIME__
#endif

namespace {

std::atomic<bool> g_installed{false};

QString _moduleVersion(const wchar_t *path)
{
    DWORD handle = 0;
    const DWORD size = GetFileVersionInfoSizeW(path, &handle);
    if (size == 0) {
        return QStringLiteral("?");
    }
    std::vector<unsigned char> buf(size);
    if (!GetFileVersionInfoW(path, 0, size, buf.data())) {
        return QStringLiteral("?");
    }
    VS_FIXEDFILEINFO *ffi = nullptr;
    UINT ffiLen = 0;
    if (!VerQueryValueW(buf.data(), L"\\", reinterpret_cast<LPVOID *>(&ffi), &ffiLen) || !ffi || ffiLen == 0) {
        return QStringLiteral("?");
    }
    return QStringLiteral("%1.%2.%3.%4")
        .arg(HIWORD(ffi->dwProductVersionMS))
        .arg(LOWORD(ffi->dwProductVersionMS))
        .arg(HIWORD(ffi->dwProductVersionLS))
        .arg(LOWORD(ffi->dwProductVersionLS));
}

void _writeModuleList(QTextStream &ts)
{
    HANDLE hProcess = GetCurrentProcess();
    HMODULE modules[512];
    DWORD needed = 0;
    ts << QStringLiteral("\nLoaded modules:\n");
    if (!EnumProcessModules(hProcess, modules, sizeof(modules), &needed)) {
        ts << QStringLiteral("  (EnumProcessModules failed)\n");
        return;
    }
    const int count = qMin<int>(needed / sizeof(HMODULE), sizeof(modules) / sizeof(HMODULE));
    for (int i = 0; i < count; ++i) {
        wchar_t path[MAX_PATH] = {0};
        if (GetModuleFileNameExW(hProcess, modules[i], path, MAX_PATH) == 0) {
            continue;
        }
        const wchar_t *base = wcsrchr(path, L'\\');
        base = base ? base + 1 : path;
        const QString name = QString::fromWCharArray(base);
        const QString version = _moduleVersion(path);
        ts << QStringLiteral("  %1  0x%2  %3\n")
            .arg(name, -32)
            .arg(reinterpret_cast<quintptr>(modules[i]), 0, 16)
            .arg(version);
    }
}

void _writeTriageInfo(QFile &txtFile, DWORD exceptionCode, void *exceptionAddress)
{
    QTextStream ts(&txtFile);
    ts << QStringLiteral("QNote crash report\n");
    ts << QStringLiteral("Timestamp: %1\n").arg(QDateTime::currentDateTime().toString(Qt::ISODate));
    ts << QStringLiteral("ExceptionCode: 0x%1\n").arg(exceptionCode, 8, 16, QLatin1Char('0'));
    ts.setIntegerBase(16);
    ts << QStringLiteral("ExceptionAddress: 0x") << reinterpret_cast<quintptr>(exceptionAddress) << QStringLiteral("\n");
    ts.setIntegerBase(10);
    ts << QStringLiteral("Dump: see sibling .dmp file\n");
    ts << QStringLiteral("Log: %1\n").arg(Logger::currentLogFilePath());
    ts << QStringLiteral("QNoteVersion: %1\n").arg(QStringLiteral(QNOTE_VERSION));
    ts << QStringLiteral("QtVersion: %1 (runtime %2)\n").arg(QStringLiteral(QT_VERSION_STR)).arg(QString::fromLatin1(qVersion()));
    ts << QStringLiteral("BuildDate: %1\n").arg(QStringLiteral(QNOTE_BUILD_DATE));
    ts << QStringLiteral("OS: %1 (kernel %2)\n").arg(QSysInfo::prettyProductName()).arg(QSysInfo::kernelVersion());
    ts << QStringLiteral("CPU: %1\n").arg(QSysInfo::currentCpuArchitecture());
    _writeModuleList(ts);
}

LONG WINAPI unhandledExceptionFilter(EXCEPTION_POINTERS *ep)
{
    // CrashHandler may fire before QApplication sets org/app name, so
    // QStandardPaths::writableLocation(AppDataLocation) is unreliable.
    // Resolve directly from APPDATA env var.
    const QString appData = QString::fromWCharArray(_wgetenv(L"APPDATA"));
    const QString baseDir = appData + QStringLiteral("/QNote/QNote/CrashDumps");
    QDir().mkpath(baseDir);

    const QString ts = QDateTime::currentDateTime().toString("yyyyMMdd-HHmmss-zzz");
    const QString path = baseDir + QDir::separator() + "QNote-" + ts + ".dmp";

    const std::wstring wpath = path.toStdWString();

    HANDLE hFile = CreateFileW(wpath.c_str(), GENERIC_WRITE, 0, nullptr,
                               CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (hFile == INVALID_HANDLE_VALUE) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    MINIDUMP_EXCEPTION_INFORMATION mei;
    mei.ThreadId = GetCurrentThreadId();
    mei.ExceptionPointers = ep;
    mei.ClientPointers = FALSE;

    // MiniDumpWithDataSegs | MiniDumpWithThreadInfo | MiniDumpWithHandleData
    // | MiniDumpWithUnloadedModules.
    // NOTE: MiniDumpWithModuleInformation is not always defined in older SDK
    // headers; the ModuleInformation data is included implicitly when
    // MiniDumpWithThreadInfo is set, so we rely on that path for cdb `lm`.
    DWORD flags = MiniDumpWithDataSegs
                  | MiniDumpWithThreadInfo
                  | MiniDumpWithHandleData
                  | MiniDumpWithUnloadedModules;
#ifdef MiniDumpWithModuleInformation
    flags |= MiniDumpWithModuleInformation;
#endif

    MiniDumpWriteDump(GetCurrentProcess(),
                      GetCurrentProcessId(),
                      hFile,
                      static_cast<MINIDUMP_TYPE>(flags),
                      ep ? &mei : nullptr,
                      nullptr,
                      nullptr);

    CloseHandle(hFile);

    // Also write a sibling .txt with quick triage info for log-only inspection.
    const QString infoPath = baseDir + QDir::separator() + "QNote-" + ts + ".txt";
    QFile f(infoPath);
    if (f.open(QIODevice::WriteOnly | QIODevice::Text)) {
        const DWORD code = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionCode : 0;
        const void *addr = ep && ep->ExceptionRecord ? ep->ExceptionRecord->ExceptionAddress : nullptr;
        _writeTriageInfo(f, code, const_cast<void *>(addr));
        f.close();
    }

    return EXCEPTION_CONTINUE_SEARCH;
}

void __cdecl invalidParamHandler(const wchar_t *, const wchar_t *, const wchar_t *, unsigned int, uintptr_t)
{
    RaiseException(0xE0000002, EXCEPTION_NONCONTINUABLE, 0, nullptr);
}

void __cdecl pureCallHandler()
{
    RaiseException(0xE0000003, EXCEPTION_NONCONTINUABLE, 0, nullptr);
}

} // namespace

namespace CrashHandler {

void install()
{
    bool expected = false;
    if (!g_installed.compare_exchange_strong(expected, true)) {
        return;
    }

    // Reserve 64KB stack for the main thread so unhandledExceptionFilter has
    // enough stack space to write a minidump when a stack overflow occurs.
    ULONG guardSize = 64 * 1024;
    SetThreadStackGuarantee(&guardSize);

    SetUnhandledExceptionFilter(unhandledExceptionFilter);
    _set_invalid_parameter_handler(invalidParamHandler);
    _set_purecall_handler(pureCallHandler);

    // Also cover pure C++ uncaught exceptions -> they eventually become SEH.
    set_terminate([]() {
        __try {
            RaiseException(0xE0000001, EXCEPTION_NONCONTINUABLE, 0, nullptr);
        } __except (unhandledExceptionFilter(GetExceptionInformation()), EXCEPTION_EXECUTE_HANDLER) {
            std::terminate();
        }
    });
}

} // namespace CrashHandler
