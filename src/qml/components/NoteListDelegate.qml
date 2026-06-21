import QtQuick
import HuskarUI.Basic

MouseArea {
    id: root

    property int noteId: -1
    property string noteTitle: ""
    property string notePreview: ""
    property string noteTime: ""
    property string noteCategory: ""
    property bool isSelected: false
    property color accentColor: "#8B8B8B"
    property var themeTokens
    property bool darkTheme: themeTokens ? themeTokens.isDark : false
    readonly property bool _compact: themeTokens ? themeTokens.isCompact : false
    property string highlightKeyword: ""

    // 富文本高亮工具：转义 HTML + 包裹匹配子串
    function _highlightText(plain, keyword) {
        if (!plain) return ""
        // 先 HTML 转义
        var escaped = plain.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
        var kw = keyword ? keyword.trim() : ""
        if (!kw) return escaped
        // 转义关键词中的正则特殊字符
        var kwEscaped = kw.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")
        try {
            var re = new RegExp(kwEscaped, "gi")
            return escaped.replace(re, function(m) {
                return '<span style="background-color:#0078D4;color:#FFFFFF;">' + m + '</span>'
            })
        } catch (e) {
            return escaped
        }
    }

    signal selected()
    signal deleteRequested()
    signal moveRequested(string category)

    property color cardColor: root.isSelected
        ? (themeTokens ? themeTokens.accentSelectionBg : Qt.rgba(0.0, 0.47, 0.83, 0.10))
        : (root.containsMouse
            ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
            : "transparent")
    property color frameColor: root.isSelected
        ? (themeTokens ? themeTokens.accentSelectionBorder : Qt.rgba(0.0, 0.47, 0.83, 0.22))
        : "transparent"
    property color titleColor: root.isSelected
        ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
        : (root.containsMouse
            ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
            : (themeTokens ? themeTokens.textPrimary : "#202020"))
    property color previewColor: themeTokens ? themeTokens.textSecondary : "#5F5F5F"
    property color timeColor: themeTokens ? themeTokens.textMuted : "#8C8C8C"

    HusContextMenu {
        id: contextMenu
        defaultMenuWidth: 170
        defaultMenuIconSize: 14

        function _refreshModel() {
            var cats = categoryManager ? categoryManager.categories : []
            var children = []
            for (var i = 1; i < cats.length; i++) {
                var c = cats[i]
                if (c.name !== root.noteCategory) {
                    var iconSrc = root.themeTokens ? root.themeTokens.categoryIcon(c.icon || "", i) : 0
                    children.push({ key: "move_" + c.name, label: c.name, iconSource: iconSrc })
                }
            }
            initModel = [
                { key: "move", label: qsTr("移动到分类"), iconSource: HusIcon.FolderOutlined, menuChildren: children },
                { key: "delete", label: qsTr("删除便签"), iconSource: HusIcon.DeleteOutlined }
            ]
            // 子菜单高度足够容纳所有分类项（在 open 前设置，避免首次显示截断）
            contextMenu.menu.popupMaxHeight = Math.max(200, children.length * 34 + 20)
        }

        Component.onCompleted: _refreshModel()
        onClickMenu: function(deep, key, keyPath, data) {
            if (key === "delete")
                root.deleteRequested()
            else if (key.indexOf("move_") === 0)
                root.moveRequested(key.substring(5))
        }
    }

    // 高度三档：紧凑74/标准84/宽松96（均能容纳标题+预览+时间47px + 合理margin）
    // compact 窗口不再强制覆盖（64 必溢出），统一由 listDensity 控制
    implicitHeight: {
        var d = settingsManager ? settingsManager.listDensity : 1
        return d === 0 ? 78 : (d === 2 ? 96 : 84)
    }
    hoverEnabled: true
    cursorShape: Qt.PointingHandCursor
    acceptedButtons: Qt.LeftButton | Qt.RightButton
    property real cardScale: pressed ? 0.985 : 1.0

    Rectangle {
        anchors.fill: parent
        anchors.margins: 2
        radius: 12
        color: root.cardColor
        border.width: 1
        border.color: root.frameColor
        scale: root.cardScale

        Behavior on scale { NumberAnimation { duration: 90; easing.type: Easing.OutQuad } }

        Rectangle {
            anchors.left: parent.left
            anchors.leftMargin: 11
            anchors.verticalCenter: parent.verticalCenter
            width: root.isSelected ? 3 : 0
            height: 26
            radius: 2
            color: themeTokens ? themeTokens.accentBase : "#0078D4"

            Behavior on width { NumberAnimation { duration: 120; easing.type: Easing.OutQuad } }
        }

        Column {
            anchors.fill: parent
            anchors.leftMargin: 23
            anchors.topMargin: {
                var d = settingsManager ? settingsManager.listDensity : 1
                return d === 0 ? 14 : (d === 2 ? 22 : 16)
            }
            anchors.bottomMargin: {
                var d = settingsManager ? settingsManager.listDensity : 1
                return d === 0 ? 14 : (d === 2 ? 22 : 16)
            }
            anchors.rightMargin: 40
            spacing: 6

            Text {
                width: parent.width
                text: root._highlightText(root.noteTitle || qsTr("新便签"), root.highlightKeyword)
                textFormat: Text.RichText
                font.pixelSize: 14
                font.bold: true
                color: root.titleColor
                elide: Text.ElideRight
                maximumLineCount: 1
                wrapMode: Text.NoWrap
                clip: true
            }

            Text {
                width: parent.width
                text: root._highlightText(root.notePreview || qsTr("暂无内容"), root.highlightKeyword)
                textFormat: Text.RichText
                font.pixelSize: 11
                color: root.previewColor
                elide: Text.ElideRight
                maximumLineCount: 1
                wrapMode: Text.NoWrap
                clip: true
            }

            Text {
                text: root.noteTime
                font.pixelSize: 10
                color: root.timeColor
            }
        }

        Rectangle {
            id: deleteBadge
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 9
            width: 24
            height: 24
            radius: 12
            color: deleteArea.containsMouse
                ? (themeTokens ? themeTokens.dangerBubbleBgHover : "#FFF3E8")
                : (themeTokens ? themeTokens.dangerBubbleBg : "#CCFFF8F1")
            border.width: 1
            border.color: deleteArea.containsMouse
                ? (themeTokens ? themeTokens.dangerBubbleBorderHover : "#66C27E5D")
                : (themeTokens ? themeTokens.dangerBubbleBorder : "#22B78C6E")
            opacity: root.containsMouse ? 1 : 0
            scale: deleteArea.containsMouse ? 1.06 : 1.0

            Behavior on color { ColorAnimation { duration: 120 } }
            Behavior on border.color { ColorAnimation { duration: 120 } }
            Behavior on scale { NumberAnimation { duration: 120; easing.type: Easing.OutQuad } }

            HusIconText {
                anchors.centerIn: parent
                iconSource: HusIcon.DeleteOutlined
                iconSize: 13
                colorIcon: deleteArea.containsMouse
                    ? (themeTokens ? themeTokens.dangerBubbleIconHover : "#B86843")
                    : (themeTokens ? themeTokens.dangerBubbleIcon : "#C27E5D")

                Behavior on colorIcon { ColorAnimation { duration: 120 } }
            }

            MouseArea {
                id: deleteArea
                anchors.fill: parent
                hoverEnabled: true
                cursorShape: Qt.PointingHandCursor
                acceptedButtons: Qt.LeftButton
                propagateComposedEvents: false
                onClicked: function(mouse) {
                    mouse.accepted = true
                    root.deleteRequested()
                }
            }

            Behavior on opacity { NumberAnimation { duration: 150 } }
        }
    }

    onClicked: function(mouse) {
        if (mouse.button === Qt.RightButton) {
            contextMenu._refreshModel()
            contextMenu.x = mouse.x
            contextMenu.y = mouse.y
            contextMenu.open()
        } else {
            root.selected()
        }
    }
}
