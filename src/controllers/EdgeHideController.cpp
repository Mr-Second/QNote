#include "EdgeHideController.h"

#include <QCursor>

namespace {
constexpr int kHideDelayMs = 800;
constexpr int kTopDockThreshold = 10;
constexpr int kHotZoneHeight = 6;
}

EdgeHideController::EdgeHideController(QObject *parent)
    : QObject(parent)
{
    m_hideTimer.setSingleShot(true);
    m_hideTimer.setInterval(kHideDelayMs);

    QObject::connect(&m_hideTimer, &QTimer::timeout, this, [this]() {
        requestHide();
    });
}

void EdgeHideController::updateWindowGeometry(qreal x, qreal y, qreal width, qreal height)
{
    m_windowGeometry = QRect(qRound(x), qRound(y), qRound(width), qRound(height));

    if (!m_edgeEnabled) {
        m_isTopDocked = false;
        m_hideTimer.stop();
        return;
    }

    if (m_isHidden || m_isHiding || m_showing)
        return;

    if (y > kTopDockThreshold) {
        m_isTopDocked = false;
        m_hideTimer.stop();
        return;
    }

    m_isTopDocked = (y <= kTopDockThreshold);
}

void EdgeHideController::setEdgeEnabled(bool enabled)
{
    if (m_edgeEnabled == enabled)
        return;
    m_edgeEnabled = enabled;
    if (!enabled) {
        m_hideTimer.stop();
        if (m_isHidden) {
            m_showing = true;
            setHidden(false);
            emit showRequested();
        }
    }
    emit edgeEnabledChanged();
}

void EdgeHideController::onActiveChanged(bool active)
{
    Q_UNUSED(active)
}

void EdgeHideController::syncPointerState(bool active)
{
    syncPointerState(active, QCursor::pos());
}

void EdgeHideController::syncPointerState(bool active, const QPoint &cursorPos)
{
    Q_UNUSED(active)
    m_lastPointerPos = cursorPos;
    m_hasPointerPos = true;

    if (!m_edgeEnabled || m_windowGeometry.isEmpty())
        return;

    if (m_isHiding || m_showing)
        return;

    if (m_isHidden) {
        if (isPointerInsideHotZone(cursorPos))
            requestShow();
        return;
    }

    if (!m_isTopDocked) {
        m_hideTimer.stop();
        return;
    }

    if (isPointerInsideVisibleWindow(cursorPos)) {
        m_hideTimer.stop();
    } else {
        armHideTimerIfNeeded();
    }
}

void EdgeHideController::notifyShowFinished()
{
    m_showing = false;

    if (!m_edgeEnabled || m_isHidden)
        return;

    if (m_hasPointerPos && isPointerInsideVisibleWindow(m_lastPointerPos)) {
        m_hideTimer.stop();
        return;
    }

    armHideTimerIfNeeded();
}

void EdgeHideController::notifyHideFinished()
{
    m_isHiding = false;
    setHidden(true);
}

bool EdgeHideController::isPointerInsideVisibleWindow(const QPoint &cursorPos) const
{
    return m_windowGeometry.contains(cursorPos);
}

bool EdgeHideController::isPointerInsideHotZone(const QPoint &cursorPos) const
{
    if (!m_isTopDocked)
        return false;

    const QRect hotZone(m_windowGeometry.x(), 0, m_windowGeometry.width(), kHotZoneHeight);
    return hotZone.contains(cursorPos);
}

void EdgeHideController::armHideTimerIfNeeded()
{
    if (!m_edgeEnabled || m_isHidden || m_isHiding || m_showing || !m_isTopDocked || m_hideTimer.isActive())
        return;

    m_hideTimer.start();
}

void EdgeHideController::setHidden(bool hidden)
{
    if (m_isHidden == hidden)
        return;
    m_isHidden = hidden;
    emit isHiddenChanged();
}

void EdgeHideController::requestHide()
{
    if (!m_edgeEnabled || m_isHidden || m_isHiding || m_showing || !m_isTopDocked)
        return;

    m_isHiding = true;
    emit hideRequested();
}

void EdgeHideController::requestShow()
{
    if (!m_edgeEnabled || !m_isHidden || m_showing)
        return;

    m_hideTimer.stop();
    m_showing = true;
    setHidden(false);
    emit showRequested();
}
