import QtQuick
import QtQuick.Controls
import HuskarUI.Basic

MouseArea {
    id: root

    property string text: ""
    property color tabColor: "#8B8B8B"
    property bool selected: false
    property var iconSource: 0
    property var themeTokens
    property bool darkTheme: themeTokens ? themeTokens.isDark : false
    readonly property bool _compact: themeTokens ? themeTokens.isCompact : false
    property bool deletable: false  // 是否显示删除按钮("全部"分类为 false)
    property bool editing: false   // 是否处于行内编辑模式(双击触发)

    implicitWidth: 110
    implicitHeight: 46
    hoverEnabled: true
    cursorShape: Qt.PointingHandCursor
    acceptedButtons: Qt.LeftButton | Qt.RightButton

    signal deleteClicked()
    signal renameSubmitted(string newName)
    signal leftClicked()

    // 提交行内编辑(失焦/外部点击/Enter 共用)。供 CategoryTabBar._commitEditingCategory 外部调用
    function _commitRename() {
        if (!root.editing) return
        var newName = renameInput.text.trim()
        root.editing = false
        if (newName.length > 0 && newName !== root.text) {
            root.renameSubmitted(newName)
        }
    }

    property color hoverSurfaceColor: themeTokens
        ? themeTokens.accentHoverBg
        : Qt.rgba(0.0, 0.47, 0.83, 0.05)
    property real tabInset: root._compact ? 0 : (root.selected ? 0 : 10)
    property color surfaceColor: root.selected
        ? (themeTokens ? themeTokens.accentSelectionBg : Qt.rgba(0.0, 0.47, 0.83, 0.10))
        : (root.containsMouse ? root.hoverSurfaceColor : "transparent")
    property color edgeColor: root.selected
        ? (themeTokens ? themeTokens.accentSelectionBorder : Qt.rgba(0.0, 0.47, 0.83, 0.22))
        : "transparent"
    property color labelColor: root.selected
        ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
        : (root.containsMouse
            ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
            : (themeTokens ? themeTokens.sidebarMutedText : "#5F5F5F"))
    property color markerColor: themeTokens ? themeTokens.accentBase : "#0078D4"
    property color iconColor: root.selected
        ? (themeTokens
            ? themeTokens.withAlpha(root.tabColor, darkTheme ? 1.0 : 0.95)
            : root.tabColor)
        : (themeTokens
            ? themeTokens.withAlpha(root.tabColor, darkTheme ? 0.96 : 0.9)
            : Qt.rgba(root.tabColor.r, root.tabColor.g, root.tabColor.b, darkTheme ? 0.96 : 0.9))

    Rectangle {
        anchors.left: parent.left
        anchors.verticalCenter: parent.verticalCenter
        x: root.tabInset
        width: parent.width - root.tabInset
        height: parent.height
        radius: 8
        color: root.surfaceColor
        border.width: 1
        border.color: root.edgeColor

        Behavior on x { NumberAnimation { duration: 160; easing.type: Easing.OutQuad } }

        // compact 模式：图标水平居中（不用 Row，避免隐藏子项残留宽度）
        HusIconText {
            anchors.centerIn: parent
            iconSource: root.iconSource
            iconSize: 15
            colorIcon: root.iconColor
            visible: root._compact
        }

        // normal 模式：标记条 + 图标 + 文字/编辑框 + 删除按钮 Row 布局
        Row {
            anchors.fill: parent
            anchors.leftMargin: 10
            anchors.rightMargin: 8
            spacing: 8
            visible: !root._compact

            Rectangle {
                anchors.verticalCenter: parent.verticalCenter
                width: root.selected ? 3 : 0
                height: 22
                radius: width / 2
                color: root.markerColor

                Behavior on width { NumberAnimation { duration: 160; easing.type: Easing.OutQuad } }
            }

            HusIconText {
                anchors.verticalCenter: parent.verticalCenter
                iconSource: root.iconSource
                iconSize: 15
                colorIcon: root.iconColor
            }

            // 文本显示(非编辑模式)
            Text {
                id: label
                anchors.verticalCenter: parent.verticalCenter
                width: parent.width - 40 - (deleteBtn.visible ? deleteBtn.width + parent.spacing : 0)
                text: root.text
                font.pixelSize: 13
                font.bold: root.selected
                color: root.labelColor
                verticalAlignment: Text.AlignVCenter
                elide: Text.ElideRight
                visible: !root.editing
            }

            // 行内编辑框(双击/右键触发)
            TextField {
                id: renameInput
                anchors.verticalCenter: parent.verticalCenter
                width: parent.width - 40
                visible: root.editing
                text: root.text
                font.pixelSize: 13
                color: root.labelColor
                selectByMouse: true
                background: Rectangle {
                    color: "transparent"
                    border.width: 1
                    border.color: themeTokens ? themeTokens.accentBase : "#0078D4"
                    radius: 3
                }
                onActiveFocusChanged: {
                    if (!activeFocus && root.editing) {
                        // 失焦时提交
                        root._commitRename()
                    }
                }
                onAccepted: root._commitRename()
                Keys.onEscapePressed: {
                    root.editing = false
                }
            }

            // 悬停删除按钮(deletable 分类,鼠标悬停时显示)
            Item {
                id: deleteBtn
                anchors.verticalCenter: parent.verticalCenter
                width: 20
                height: 20
                visible: root.deletable && !root.editing && root.containsMouse

                Rectangle {
                    anchors.fill: parent
                    radius: 10
                    color: deleteBtnArea.containsMouse
                        ? (themeTokens ? themeTokens.dangerBubbleBgHover : Qt.rgba(0.8, 0.2, 0.2, 0.15))
                        : "transparent"
                }

                HusIconText {
                    anchors.centerIn: parent
                    iconSource: HusIcon.CloseOutlined
                    iconSize: 12
                    colorIcon: deleteBtnArea.containsMouse
                        ? (themeTokens ? themeTokens.dangerBubbleIconHover : "#B86843")
                        : (themeTokens ? themeTokens.dangerBubbleIcon : "#C27E5D")
                }

                MouseArea {
                    id: deleteBtnArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: function(mouse) {
                        mouse.accepted = true
                        root.deleteClicked()
                    }
                }
            }
        }

        HusToolTip {
            visible: root._compact && root.containsMouse
            text: root.text
            position: HusToolTip.Position_Right
        }
    }

    // 双击进入编辑模式(仅 deletable=true 的分类可改名)
    onDoubleClicked: function(mouse) {
        if (mouse.button === Qt.LeftButton && root.deletable) {
            root.editing = true
            renameInput.text = root.text
            renameInput.forceActiveFocus()
            renameInput.selectAll()
        }
    }

    // 点击处理:左键选中,右键进入改名(与双击一致,替代原 HusContextMenu)
    // 设计取舍:弃用 HusContextMenu,因 HusMenu 内部 __private.selectedItem 守卫
    // 会导致右键菜单二次点击失效(私有状态无法外部重置),详见 trellis-break-loop 分析
    onClicked: function(mouse) {
        if (mouse.button === Qt.LeftButton) {
            root.leftClicked()
        } else if (mouse.button === Qt.RightButton && root.deletable && !root.editing) {
            root.editing = true
            renameInput.text = root.text
            Qt.callLater(function() {
                renameInput.forceActiveFocus()
                renameInput.selectAll()
            })
        }
    }
}
