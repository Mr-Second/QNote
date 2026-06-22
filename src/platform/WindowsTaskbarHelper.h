#pragma once

#include <QObject>

class QWindow;

/**
 * @brief Windows 平台任务栏图标控制辅助类
 *
 * 使用 Windows ITaskbarList3 COM 接口的 DeleteTab/AddTab 方法来隐藏/显示任务栏图标，
 * 不改变窗口类型或样式，避免影响窗口行为、焦点、热区检测等。
 */
class WindowsTaskbarHelper : public QObject {
    Q_OBJECT
public:
    explicit WindowsTaskbarHelper(QObject *parent = nullptr);

    /**
     * @brief 设置窗口任务栏图标显隐
     * @param window 目标窗口
     * @param visible true 显示任务栏图标，false 隐藏
     * @return 操作是否成功（非 Windows 平台总是返回 false）
     */
    Q_INVOKABLE static bool setTaskbarIconVisible(QWindow *window, bool visible);
};
