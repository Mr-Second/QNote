#pragma once

#include <QObject>
#include <QPoint>
#include <QRect>
#include <QTimer>

class EdgeHideController : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool edgeEnabled READ edgeEnabled WRITE setEdgeEnabled NOTIFY edgeEnabledChanged)
    Q_PROPERTY(bool isHidden READ isHidden NOTIFY isHiddenChanged)
public:
    explicit EdgeHideController(QObject *parent = nullptr);

    bool edgeEnabled() const { return m_edgeEnabled; }
    void setEdgeEnabled(bool enabled);

    bool isHidden() const { return m_isHidden; }

    Q_INVOKABLE void updateWindowGeometry(qreal x, qreal y, qreal width, qreal height);
    Q_INVOKABLE void onActiveChanged(bool active);
    Q_INVOKABLE void syncPointerState(bool active);
    Q_INVOKABLE void notifyShowFinished();
    Q_INVOKABLE void notifyHideFinished();

    void syncPointerState(bool active, const QPoint &cursorPos);

signals:
    void hideRequested();
    void showRequested();
    void edgeEnabledChanged();
    void isHiddenChanged();

private:
    bool isPointerInsideVisibleWindow(const QPoint &cursorPos) const;
    bool isPointerInsideHotZone(const QPoint &cursorPos) const;
    void armHideTimerIfNeeded();
    void setHidden(bool hidden);
    void requestHide();
    void requestShow();

    bool m_edgeEnabled = true;
    bool m_isHidden = false;
    bool m_isHiding = false;
    bool m_showing = false;
    bool m_isTopDocked = false;
    QPoint m_lastPointerPos;
    bool m_hasPointerPos = false;
    QRect m_windowGeometry;
    QTimer m_hideTimer;
};
