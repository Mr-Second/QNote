import QtQuick
import QtQuick.Dialogs
import HuskarUI.Basic

Row {
    id: root
    spacing: 6

    property var themeTokens: null
    property var textArea: null
    property bool stylePopupVisible: false
    readonly property bool _compact: themeTokens ? themeTokens.isCompact : false

    signal requestStylePopup()

    function _ensureHelper() {
        if (textFormatHelper && textArea) {
            textFormatHelper.setTextArea(textArea)
            textFormatHelper.refreshFormatState()
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: boldMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"

        Text {
            anchors.centerIn: parent
            text: "B"
            font.pixelSize: 14
            font.bold: true
            color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: boldMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.toggleBold() }

            HusToolTip {
                visible: boldMouse.containsMouse
                text: qsTr("加粗")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: italicMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"

        Text {
            anchors.centerIn: parent
            text: "I"
            font.pixelSize: 14
            font.italic: true
            color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: italicMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.toggleItalic() }

            HusToolTip {
                visible: italicMouse.containsMouse
                text: qsTr("斜体")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: underlineMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"

        Text {
            anchors.centerIn: parent
            text: "U"
            font.pixelSize: 14
            font.underline: true
            color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: underlineMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.toggleUnderline() }

            HusToolTip {
                visible: underlineMouse.containsMouse
                text: qsTr("下划线")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: strikeMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"

        Text {
            anchors.centerIn: parent
            text: "S"
            font.pixelSize: 14
            font.strikeout: true
            color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: strikeMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.toggleStrikeout() }

            HusToolTip {
                visible: strikeMouse.containsMouse
                text: qsTr("删除线")
            }
        }
    }

    Rectangle {
        width: 1
        height: 18
        anchors.verticalCenter: parent.verticalCenter
        color: root.themeTokens ? root.themeTokens.divider : "#18A39283"
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: styleMouse.containsMouse || root.stylePopupVisible
            ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
            : "transparent"

        Item {
            anchors.fill: parent

            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                anchors.top: parent.top
                anchors.topMargin: 4
                text: "A"
                font.pixelSize: 14
                font.bold: true
                color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
            }

            Rectangle {
                anchors.horizontalCenter: parent.horizontalCenter
                anchors.bottom: parent.bottom
                anchors.bottomMargin: 4
                width: 12
                height: 2
                radius: 1
                color: textFormatHelper ? textFormatHelper.currentTextColor : "#473C35"
            }
        }

        MouseArea {
            id: styleMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: {
                root._ensureHelper()
                root.requestStylePopup()
            }

            HusToolTip {
                visible: styleMouse.containsMouse
                text: qsTr("字体样式")
            }
        }
    }

    Rectangle {
        width: 1
        height: 18
        anchors.verticalCenter: parent.verticalCenter
        color: root.themeTokens ? root.themeTokens.divider : "#18A39283"
        visible: !root._compact
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: ulMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"
        visible: !root._compact

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.UnorderedListOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: ulMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.insertUnorderedList() }

            HusToolTip {
                visible: ulMouse.containsMouse
                text: qsTr("无序列表")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: olMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"
        visible: !root._compact

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.OrderedListOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: olMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.insertOrderedList() }

            HusToolTip {
                visible: olMouse.containsMouse
                text: qsTr("有序列表")
            }
        }
    }

    Rectangle {
        width: 1
        height: 18
        anchors.verticalCenter: parent.verticalCenter
        color: root.themeTokens ? root.themeTokens.divider : "#18A39283"
        visible: !root._compact
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: alignLMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"
        visible: !root._compact

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.AlignLeftOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: alignLMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.setAlignment(Qt.AlignLeft) }

            HusToolTip {
                visible: alignLMouse.containsMouse
                text: qsTr("左对齐")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: alignCMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"
        visible: !root._compact

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.AlignCenterOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: alignCMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.setAlignment(Qt.AlignHCenter) }

            HusToolTip {
                visible: alignCMouse.containsMouse
                text: qsTr("居中对齐")
            }
        }
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: alignRMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"
        visible: !root._compact

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.AlignRightOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: alignRMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: { root._ensureHelper(); textFormatHelper.setAlignment(Qt.AlignRight) }

            HusToolTip {
                visible: alignRMouse.containsMouse
                text: qsTr("右对齐")
            }
        }
    }

    Rectangle {
        width: 1
        height: 18
        anchors.verticalCenter: parent.verticalCenter
        color: root.themeTokens ? root.themeTokens.divider : "#18A39283"
        visible: !root._compact
    }

    Rectangle {
        width: 26
        height: 26
        radius: 6
        color: imgMouse.containsMouse ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05)) : "transparent"

        HusIconText {
            anchors.centerIn: parent
            iconSource: HusIcon.PictureOutlined
            iconSize: 13
            colorIcon: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
        }

        MouseArea {
            id: imgMouse
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: {
                root._ensureHelper()
                imageManager.setTextArea(root.textArea)
                imageFileDialog.open()
            }

            HusToolTip {
                visible: imgMouse.containsMouse
                text: qsTr("插入图片")
            }
        }
    }

    FileDialog {
        id: imageFileDialog
        title: qsTr("选择图片")
        nameFilters: [qsTr("图片文件 (*.png *.jpg *.jpeg *.gif *.bmp *.webp *.svg)")]
        onAccepted: {
            imageManager.insertFromFile(selectedFile.toString().replace("file:///", ""))
        }
    }
}
