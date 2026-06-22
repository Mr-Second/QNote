#include "WindowsTaskbarHelper.h"

#include <QWindow>
#include <QDebug>

#ifdef Q_OS_WIN
#include <windows.h>
#include <shobjidl.h>
#endif

WindowsTaskbarHelper::WindowsTaskbarHelper(QObject *parent)
    : QObject(parent)
{
}

bool WindowsTaskbarHelper::setTaskbarIconVisible(QWindow *window, bool visible)
{
#ifdef Q_OS_WIN
    if (!window)
        return false;

    HWND hwnd = reinterpret_cast<HWND>(window->winId());
    if (!hwnd)
        return false;

    // 使用 ITaskbarList3 接口控制任务栏图标（推荐方式，不改变窗口样式）
    ITaskbarList3 *taskbarList = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_TaskbarList, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_ITaskbarList3, reinterpret_cast<void**>(&taskbarList));

    if (FAILED(hr) || !taskbarList) {
        qWarning() << "WindowsTaskbarHelper: failed to create ITaskbarList3, hr=" << Qt::hex << hr;
        return false;
    }

    hr = taskbarList->HrInit();
    if (FAILED(hr)) {
        qWarning() << "WindowsTaskbarHelper: ITaskbarList3::HrInit failed, hr=" << Qt::hex << hr;
        taskbarList->Release();
        return false;
    }

    if (visible) {
        // 添加到任务栏（显示图标）
        hr = taskbarList->AddTab(hwnd);
        qDebug() << "WindowsTaskbarHelper: AddTab, hr=" << Qt::hex << hr;
    } else {
        // 从任务栏移除（隐藏图标）
        hr = taskbarList->DeleteTab(hwnd);
        qDebug() << "WindowsTaskbarHelper: DeleteTab, hr=" << Qt::hex << hr;
    }

    taskbarList->Release();
    return SUCCEEDED(hr);
#else
    Q_UNUSED(window)
    Q_UNUSED(visible)
    return false;
#endif
}
