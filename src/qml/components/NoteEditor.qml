import QtQuick
import QtQuick.Controls
import HuskarUI.Basic

Item {
    id: root

    readonly property int _menuScrollWidth: 8
    readonly property int _menuScrollInset: 1
    property alias text: textArea.text
    property bool hasContent: textArea.length > 0
    property bool _ready: false
    property string noteTime: ""
    property string noteCategory: ""
    property string noteTitle: ""
    property bool titleManual: false
    property color accentColor: "#CBA37B"
    property var themeTokens
    property bool darkTheme: themeTokens ? themeTokens.isDark : false
    // 字体列表：白名单常用中文字体（系统存在才保留）+ 当前编辑器字体
    // 不用全量 Qt.fontFamilies()（数百项过长），白名单覆盖大部分用户需求
    property var _fontOptions: {
        var sys = Qt.fontFamilies()
        var whitelist = [
            "Microsoft YaHei UI", "Microsoft YaHei",
            "SimSun", "SimHei", "KaiTi", "FangSong",
            "Consolas", "Arial", "Segoe UI", "Tahoma"
        ]
        var result = []
        for (var i = 0; i < whitelist.length; i++) {
            if (sys.indexOf(whitelist[i]) >= 0) result.push(whitelist[i])
        }
        // 当前编辑器字体若不在白名单（用户在设置页改过），追加到首位
        var cur = settingsManager ? settingsManager.editorFontFamily : ""
        if (cur && result.indexOf(cur) < 0) result.unshift(cur)
        return result
    }
    property var _fontDisplayNames: ({
        "Microsoft YaHei UI": qsTr("微软雅黑 UI"),
        "Microsoft YaHei": qsTr("微软雅黑"),
        "SimSun": qsTr("宋体"),
        "SimHei": qsTr("黑体"),
        "KaiTi": qsTr("楷体"),
        "FangSong": qsTr("仿宋"),
        "Consolas": "Consolas",
        "Arial": "Arial",
        "Segoe UI": "Segoe UI",
        "Tahoma": "Tahoma"
    })
    property var _fontSizeOptions: [12, 14, 16, 18, 20, 24, 28, 32]
    property var _colorOptions: colorPaletteProvider ? colorPaletteProvider.colors : []
    property bool _stylePopupVisible: false
    property bool _stylePopupPinned: false
    property bool _fontMenuVisible: false
    property bool _sizeMenuVisible: false
    property bool _colorMenuVisible: false
    property real _stylePopupX: 0
    property real _stylePopupY: 0

    signal deleteNoteRequested()
    signal textEdited(string text)
    signal titleEdited(string title)
    signal contentReady()

    function _refreshFormatState() {
        if (textFormatHelper && textArea && textArea.textArea) {
            textFormatHelper.setTextArea(textArea.textArea)
            textFormatHelper.refreshFormatState()
        }
    }

    function _labelForFont(family) {
        if (!family || family.length === 0)
            return qsTr("默认字体")
        return _fontDisplayNames[family] || family
    }

    function _labelForSize(size) {
        return size > 0 ? qsTr("%1 px").arg(size) : qsTr("字号")
    }

    function _currentTextColorHex() {
        return textFormatHelper ? textFormatHelper.currentTextColor.toLowerCase() : "#473c35"
    }

    function _selectionLength() {
        if (!textArea || !textArea.textArea) return 0
        return Math.abs(textArea.textArea.selectionEnd - textArea.textArea.selectionStart)
    }

    function _closeStyleMenus() {
        _fontMenuVisible = false
        _sizeMenuVisible = false
        _colorMenuVisible = false
    }

    function _selectionAnchorPoint() {
        if (!textArea || !textArea.textArea || !editorSurface)
            return { x: 24, y: 24, h: 20 }

        var editor = textArea.textArea
        var selectionLength = _selectionLength()
        if (selectionLength > 0) {
            var start = Math.min(editor.selectionStart, editor.selectionEnd)
            var end = Math.max(editor.selectionStart, editor.selectionEnd)
            var startRect = editor.positionToRectangle(start)
            var endRect = editor.positionToRectangle(end)
            var startPoint = editor.mapToItem(editorSurface, startRect.x + startRect.width / 2, startRect.y)
            var endPoint = editor.mapToItem(editorSurface, endRect.x + endRect.width / 2, endRect.y)
            var sameLine = Math.abs(startPoint.y - endPoint.y) < Math.max(startRect.height, endRect.height)
            return {
                x: sameLine ? (startPoint.x + endPoint.x) / 2 : startPoint.x,
                y: Math.min(startPoint.y, endPoint.y),
                h: Math.max(startRect.height, endRect.height)
            }
        }

        var cursorRect = editor.positionToRectangle(editor.cursorPosition)
        var cursorPoint = editor.mapToItem(editorSurface, cursorRect.x + cursorRect.width / 2, cursorRect.y)
        return {
            x: cursorPoint.x,
            y: cursorPoint.y,
            h: cursorRect.height
        }
    }

    function _updateStylePopupPosition() {
        if (!editorSurface) return

        var anchor = _selectionAnchorPoint()
        var desiredX = anchor.x - styleBubble.width / 2
        var desiredY = anchor.y - styleBubble.height - 12
        var minX = 12
        var maxX = Math.max(minX, editorSurface.width - styleBubble.width - 12)
        _stylePopupX = Math.max(minX, Math.min(desiredX, maxX))
        _stylePopupY = desiredY < 12 ? (anchor.y + anchor.h + 12) : desiredY
    }

    function _openStylePopup() {
        _refreshFormatState()
        _stylePopupVisible = true
        _closeStyleMenus()
        Qt.callLater(function() {
            _updateStylePopupPosition()
        })
    }

    function _closeStylePopup() {
        _closeStyleMenus()
        _stylePopupVisible = false
        _stylePopupPinned = false
    }

    function _syncSelectionPopup() {
        _refreshFormatState()
        if (_selectionLength() > 0) {
            _stylePopupPinned = false
            _openStylePopup()
        } else if (_stylePopupPinned) {
            Qt.callLater(function() {
                _updateStylePopupPosition()
            })
        } else {
            _closeStylePopup()
        }
    }

    function _toggleStylePopupFromToolbar() {
        if (_stylePopupVisible && _stylePopupPinned) {
            _closeStylePopup()
            return
        }

        _stylePopupPinned = true
        _openStylePopup()
    }

    function _toggleFontMenu() {
        _refreshFormatState()
        if (!_stylePopupVisible)
            _openStylePopup()
        _fontMenuVisible = !_fontMenuVisible
        _sizeMenuVisible = false
        _colorMenuVisible = false
        Qt.callLater(function() {
            _updateStylePopupPosition()
        })
    }

    function _toggleSizeMenu() {
        _refreshFormatState()
        if (!_stylePopupVisible)
            _openStylePopup()
        _fontMenuVisible = false
        _sizeMenuVisible = !_sizeMenuVisible
        _colorMenuVisible = false
        Qt.callLater(function() {
            _updateStylePopupPosition()
        })
    }

    function _toggleColorMenu() {
        _refreshFormatState()
        if (!_stylePopupVisible)
            _openStylePopup()
        _fontMenuVisible = false
        _sizeMenuVisible = false
        _colorMenuVisible = !_colorMenuVisible
        Qt.callLater(function() {
            _updateStylePopupPosition()
        })
    }

    Component.onCompleted: _ready = true

    Column {
        anchors.fill: parent
        spacing: 0

        Item {
            width: parent.width
            height: themeTokens ? themeTokens.editorTitleRowHeight : 48

            Column {
                anchors.left: parent.left
                anchors.leftMargin: 4
                anchors.right: parent.right
                anchors.rightMargin: 4
                anchors.verticalCenter: parent.verticalCenter
                spacing: 2

                MouseArea {
                    width: parent.width
                    height: Math.max(titleText.implicitHeight + 8, 28)
                    cursorShape: Qt.IBeamCursor
                    onClicked: {
                        titleInput.visible = true
                        titleInput.forceActiveFocus()
                        titleInput.selectAll()
                    }

                    Text {
                        id: titleText
                        anchors.verticalCenter: parent.verticalCenter
                        text: root.noteTitle || qsTr("无标题便签")
                        font.pixelSize: themeTokens ? themeTokens.editorTitleFontSize : 18
                        font.bold: true
                        color: root.themeTokens ? root.themeTokens.titleText : "#4B4038"
                        elide: Text.ElideRight
                        width: parent.width
                        visible: !titleInput.visible
                    }

                    HusInput {
                        id: titleInput
                        anchors.fill: parent
                        visible: false
                        type: HusInput.Type_Borderless
                        text: root.noteTitle
                        font.pixelSize: themeTokens ? themeTokens.editorTitleFontSize : 18
                        font.bold: true
                        colorText: root.themeTokens ? root.themeTokens.titleText : "#4B4038"
                        colorBorder: "transparent"
                        colorBg: "transparent"
                        topPadding: 0
                        bottomPadding: 0
                        verticalAlignment: TextInput.AlignVCenter
                        onAccepted: {
                            visible = false
                            if (text !== root.noteTitle)
                                root.titleEdited(text)
                        }
                        onActiveFocusChanged: {
                            if (!activeFocus && visible) {
                                visible = false
                                if (text !== root.noteTitle)
                                    root.titleEdited(text)
                            }
                        }
                    }
                }

                Text {
                    font.pixelSize: 11
                    color: root.themeTokens ? root.themeTokens.textTertiary : "#A39588"
                    text: {
                        var parts = []
                        if (root.noteCategory)
                            parts.push(root.noteCategory)
                        if (root.noteTime)
                            parts.push(root.noteTime)
                        return parts.join("  ·  ")
                    }
                }
            }
        }

        Rectangle {
            width: parent.width
            height: 1
            color: themeTokens ? themeTokens.divider : "#14A0897D"
        }

        Item {
            width: parent.width
            height: 36

            EditorToolbar {
                id: editorToolbar
                anchors.left: parent.left
                anchors.leftMargin: 2
                anchors.verticalCenter: parent.verticalCenter
                themeTokens: root.themeTokens
                textArea: textArea.textArea
                stylePopupVisible: root._stylePopupVisible
                onRequestStylePopup: root._toggleStylePopupFromToolbar()
            }
        }

        Rectangle {
            width: parent.width
            height: 1
            color: themeTokens ? themeTokens.divider : "#14A0897D"
        }

        Item {
            id: editorSurface
            width: parent.width
            height: parent.height - 48 - 36 - 2

            HusTextArea {
                id: textArea
                anchors.fill: parent
                anchors.leftMargin: themeTokens ? themeTokens.editorSideMargin : 22
                anchors.rightMargin: themeTokens ? themeTokens.editorSideMargin : 22
                anchors.topMargin: 16
                anchors.bottomMargin: 24
                textFormat: TextEdit.RichText
                // 编辑器字体：启动时从 settingsManager 读（重启生效）。
                // 运行时不动态切换（避免富文本已设的 char format 被覆盖混乱）。
                font.family: settingsManager ? settingsManager.editorFontFamily : "Microsoft YaHei"
                font.pixelSize: settingsManager ? settingsManager.editorFontSize : 16
                placeholderText: textArea.textArea.activeFocus || textArea.length > 0 ? "" : qsTr("在这里输入便签内容...")
                colorBg: "transparent"
                colorBorder: "transparent"
                colorText: themeTokens ? root.themeTokens.textPrimary : "#473C35"
                colorPlaceholderText: themeTokens ? root.themeTokens.textTertiary : "#A99A8D"
                colorSelection: themeTokens
                    ? themeTokens.withAlpha(root.accentColor, darkTheme ? 0.34 : 0.24)
                    : Qt.rgba(root.accentColor.r, root.accentColor.g, root.accentColor.b, darkTheme ? 0.34 : 0.24)
                font.letterSpacing: 0.2
                topPadding: 0
                leftPadding: 0
                rightPadding: 0
                bottomPadding: 0
                Component.onCompleted: {
                    imageManager.setTextArea(textArea.textArea)
                    // 失焦（含弹右键菜单）时保留选区，Qt 官方 TextEditor 示例方案。
                    // 不设这行，右键菜单一弹出选区就消失，所有复制/剪切/粘贴都拿不到正确选区。
                    textArea.textArea.persistentSelection = true
                }
                onTextChanged: {
                    imageManager.setTextArea(textArea.textArea)
                    imageManager.preloadImages()
                    if (root._ready) {
                        root.textEdited(textArea.text)
                    }
                }
            }

            Connections {
                target: textArea.textArea

                function onCursorPositionChanged() {
                    root._syncSelectionPopup()
                }

                function onSelectionStartChanged() {
                    root._syncSelectionPopup()
                }

                function onSelectionEndChanged() {
                    root._syncSelectionPopup()
                }
            }

            // 右键菜单方案已废弃（5 轮失败：HusContextMenu 弹出夺焦点破坏选区/焦点）。
            // 改为快捷键方案：
            //   - Ctrl+V / Ctrl+C / Ctrl+X：TextEdit 原生支持，无需代码
            //   - Ctrl+Shift+V：纯文本粘贴（去格式），需 C++ pastePlainText()
            // 键盘事件路径不夺焦点，连续操作可靠（用户已实测验证）。
            Shortcut {
                sequence: "Ctrl+Shift+V"
                onActivated: {
                    if (textArea && textArea.textArea) {
                        textArea.textArea.forceActiveFocus()
                    }
                    textFormatHelper.pastePlainText()
                }
            }

            // Ctrl+Enter / Ctrl+Return:插入格式干净的新段落。
            // 用于粘贴段末尾续写:新段落不继承上段的背景色残留。
            // 普通 Enter 仍继承当前段落格式(保留合法场景)。
            Shortcut {
                sequence: "Ctrl+Return"
                onActivated: {
                    if (textArea && textArea.textArea) {
                        textArea.textArea.forceActiveFocus()
                    }
                    textFormatHelper.insertCleanParagraph()
                }
            }
            Shortcut {
                sequence: "Ctrl+Enter"
                onActivated: {
                    if (textArea && textArea.textArea) {
                        textArea.textArea.forceActiveFocus()
                    }
                    textFormatHelper.insertCleanParagraph()
                }
            }

            Item {
                id: styleLayer
                visible: root._stylePopupVisible
                z: 20
                x: root._stylePopupX
                y: root._stylePopupY
                width: Math.max(styleBubble.width,
                    fontMenu.visible ? fontMenu.width + fontMenu.x :
                    sizeMenu.visible ? sizeMenu.width + sizeMenu.x :
                    colorMenu.visible ? colorMenu.width + colorMenu.x : styleBubble.width)
                height: styleBubble.height

                Rectangle {
                    id: styleBubble
                    width: styleBubbleRow.implicitWidth + 20
                    height: 42
                    radius: 14
                    color: root.themeTokens ? Qt.rgba(root.themeTokens.listPanelBg.r, root.themeTokens.listPanelBg.g, root.themeTokens.listPanelBg.b, 0.94) : "#F0F5EFE7"
                    border.width: 0

                    Row {
                        id: styleBubbleRow
                        anchors.centerIn: parent
                        spacing: 6

                        Repeater {
                            model: [
                                { label: "B", handler: function() { root._refreshFormatState(); textFormatHelper.toggleBold() } },
                                { label: "I", handler: function() { root._refreshFormatState(); textFormatHelper.toggleItalic() } },
                                { label: "U", handler: function() { root._refreshFormatState(); textFormatHelper.toggleUnderline() } },
                                { label: "S", handler: function() { root._refreshFormatState(); textFormatHelper.toggleStrikeout() } }
                            ]

                            Rectangle {
                                width: 24
                                height: 24
                                radius: 7
                                color: mouse.containsMouse
                                    ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#14FFFFFF")
                                    : "transparent"

                                Text {
                                    anchors.centerIn: parent
                                    text: modelData.label
                                    font.pixelSize: 13
                                    font.bold: modelData.label === "B"
                                    font.italic: modelData.label === "I"
                                    font.underline: modelData.label === "U"
                                    font.strikeout: modelData.label === "S"
                                    color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
                                }

                                MouseArea {
                                    id: mouse
                                    anchors.fill: parent
                                    hoverEnabled: true
                                    cursorShape: Qt.PointingHandCursor
                                    onClicked: modelData.handler()
                                }
                            }
                        }

                        Rectangle {
                            width: 1
                            height: 16
                            anchors.verticalCenter: parent.verticalCenter
                            color: root.themeTokens ? root.themeTokens.divider : "#18A39283"
                        }

                        Rectangle {
                            id: fontButton
                            width: 96
                            height: 26
                            radius: 8
                            color: fontMouse.containsMouse || root._fontMenuVisible
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#14FFFFFF")
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#18A39283"

                            Text {
                                anchors.left: parent.left
                                anchors.leftMargin: 8
                                anchors.verticalCenter: parent.verticalCenter
                                width: parent.width - 28
                                elide: Text.ElideRight
                                text: root._labelForFont(textFormatHelper ? textFormatHelper.currentFontFamily : "")
                                font.pixelSize: 11
                                color: root.themeTokens ? root.themeTokens.textPrimary : "#473C35"
                            }

                            Text {
                                anchors.right: parent.right
                                anchors.rightMargin: 8
                                anchors.verticalCenter: parent.verticalCenter
                                text: "▼"
                                font.pixelSize: 9
                                color: root.themeTokens ? root.themeTokens.textTertiary : "#8E7E71"
                            }

                            MouseArea {
                                id: fontMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: root._toggleFontMenu()
                            }
                        }

                        Rectangle {
                            id: sizeButton
                            width: 66
                            height: 26
                            radius: 8
                            color: sizeMouse.containsMouse || root._sizeMenuVisible
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#14FFFFFF")
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#18A39283"

                            Text {
                                anchors.left: parent.left
                                anchors.leftMargin: 18
                                anchors.verticalCenter: parent.verticalCenter
                                text: textFormatHelper ? textFormatHelper.currentFontPixelSize : 16
                                font.pixelSize: 11
                                color: root.themeTokens ? root.themeTokens.textPrimary : "#473C35"
                            }

                            Text {
                                anchors.right: parent.right
                                anchors.rightMargin: 14
                                anchors.verticalCenter: parent.verticalCenter
                                text: "▼"
                                font.pixelSize: 9
                                color: root.themeTokens ? root.themeTokens.textTertiary : "#8E7E71"
                            }

                            MouseArea {
                                id: sizeMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: root._toggleSizeMenu()
                            }
                        }

                        Rectangle {
                            id: colorButton
                            width: 28
                            height: 26
                            radius: 8
                            color: colorMouse.containsMouse || root._colorMenuVisible
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#14FFFFFF")
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#18A39283"

                            Item {
                                anchors.centerIn: parent
                                width: 14
                                height: 16

                                Text {
                                    anchors.horizontalCenter: parent.horizontalCenter
                                    anchors.top: parent.top
                                    text: "A"
                                    font.pixelSize: 12
                                    font.bold: true
                                    color: root.themeTokens ? root.themeTokens.actionBubbleIcon : "#A18F81"
                                }

                                Rectangle {
                                    anchors.horizontalCenter: parent.horizontalCenter
                                    anchors.bottom: parent.bottom
                                    width: 12
                                    height: 2
                                    radius: 1
                                    color: textFormatHelper ? textFormatHelper.currentTextColor : "#473C35"
                                }
                            }

                            MouseArea {
                                id: colorMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: root._toggleColorMenu()
                            }
                        }
                    }
                }

                Rectangle {
                    id: fontMenu
                    visible: root._fontMenuVisible
                    z: 21
                    x: fontButton.x
                    y: styleBubble.height + 8
                    width: 156
                    height: 188
                    radius: 12
                    color: root.themeTokens ? Qt.rgba(root.themeTokens.listPanelBg.r, root.themeTokens.listPanelBg.g, root.themeTokens.listPanelBg.b, 0.96) : "#F5EFE7"
                    border.width: 1
                    border.color: root.themeTokens ? root.themeTokens.listPanelBorder : "#28A39283"

                    ListView {
                        id: fontMenuList
                        anchors.fill: parent
                        anchors.margins: 8
                        clip: true
                        model: root._fontOptions
                        spacing: 6

                        delegate: Rectangle {
                            width: fontMenuList.width - root._menuScrollWidth - 8
                            height: 34
                            radius: 8
                            color: textFormatHelper && textFormatHelper.currentFontFamily === modelData
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#1FCBA37B")
                                : "transparent"

                            Text {
                                anchors.left: parent.left
                                anchors.leftMargin: 10
                                anchors.verticalCenter: parent.verticalCenter
                                text: root._fontDisplayNames[modelData] || modelData
                                font.family: modelData
                                font.pixelSize: 12
                                color: root.themeTokens ? root.themeTokens.textPrimary : "#473C35"
                            }

                            MouseArea {
                                anchors.fill: parent
                                cursorShape: Qt.PointingHandCursor
                                onClicked: {
                                    root._refreshFormatState()
                                    textFormatHelper.setFontFamily(modelData)
                                    root._fontMenuVisible = false
                                }
                            }
                        }

                        ScrollBar.vertical: ScrollBar {
                            policy: ScrollBar.AsNeeded
                            width: root._menuScrollWidth
                            padding: root._menuScrollInset
                            background: Rectangle {
                                radius: width / 2
                                color: "transparent"
                            }
                            contentItem: Rectangle {
                                implicitWidth: root._menuScrollWidth - root._menuScrollInset * 2
                                implicitHeight: 52
                                radius: Math.min(width, height) / 2
                                antialiasing: true
                                color: fontMenuList.ScrollBar.vertical.active
                                    ? (themeTokens ? themeTokens.scrollbarActive : "#BCA895")
                                    : (themeTokens ? themeTokens.scrollbarInactive : "#D9CBBF")
                                opacity: fontMenuList.ScrollBar.vertical.active ? 1.0 : 0.5
                            }
                        }
                    }
                }

                Rectangle {
                    id: sizeMenu
                    visible: root._sizeMenuVisible
                    z: 21
                    x: sizeButton.x
                    y: styleBubble.height + 8
                    width: 64
                    height: 188
                    radius: 12
                    color: root.themeTokens ? Qt.rgba(root.themeTokens.listPanelBg.r, root.themeTokens.listPanelBg.g, root.themeTokens.listPanelBg.b, 0.96) : "#F5EFE7"
                    border.width: 1
                    border.color: root.themeTokens ? root.themeTokens.listPanelBorder : "#28A39283"

                    ListView {
                        id: sizeMenuList
                        anchors.fill: parent
                        anchors.margins: 8
                        clip: true
                        model: root._fontSizeOptions
                        spacing: 6

                        delegate: Rectangle {
                            width: sizeMenuList.width - root._menuScrollWidth - 8
                            height: 30
                            radius: 8
                            color: textFormatHelper && textFormatHelper.currentFontPixelSize === modelData
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#1FCBA37B")
                                : "transparent"

                            Text {
                                anchors.centerIn: parent
                                text: modelData
                                font.pixelSize: 11
                                color: root.themeTokens ? root.themeTokens.textPrimary : "#473C35"
                            }

                            MouseArea {
                                anchors.fill: parent
                                cursorShape: Qt.PointingHandCursor
                                onClicked: {
                                    root._refreshFormatState()
                                    textFormatHelper.setFontPixelSize(modelData)
                                    root._sizeMenuVisible = false
                                }
                            }
                        }

                        ScrollBar.vertical: ScrollBar {
                            policy: ScrollBar.AsNeeded
                            width: root._menuScrollWidth
                            padding: root._menuScrollInset
                            background: Rectangle {
                                radius: width / 2
                                color: "transparent"
                            }
                            contentItem: Rectangle {
                                implicitWidth: root._menuScrollWidth - root._menuScrollInset * 2
                                implicitHeight: 52
                                radius: Math.min(width, height) / 2
                                antialiasing: true
                                color: sizeMenuList.ScrollBar.vertical.active
                                    ? (themeTokens ? themeTokens.scrollbarActive : "#BCA895")
                                    : (themeTokens ? themeTokens.scrollbarInactive : "#D9CBBF")
                                opacity: sizeMenuList.ScrollBar.vertical.active ? 1.0 : 0.5
                            }
                        }
                    }
                }

                Rectangle {
                    id: colorMenu
                    visible: root._colorMenuVisible
                    z: 21
                    x: Math.max(0, colorButton.x - 100)
                    y: styleBubble.height + 8
                    width: 126
                    height: 188
                    radius: 12
                    color: root.themeTokens ? Qt.rgba(root.themeTokens.listPanelBg.r, root.themeTokens.listPanelBg.g, root.themeTokens.listPanelBg.b, 0.96) : "#F5EFE7"
                    border.width: 1
                    border.color: root.themeTokens ? root.themeTokens.listPanelBorder : "#28A39283"

                    ListView {
                        id: colorMenuList
                        anchors.fill: parent
                        anchors.margins: 8
                        clip: true
                        model: root._colorOptions
                        spacing: 6

                        delegate: Rectangle {
                            width: colorMenuList.width - root._menuScrollWidth - 8
                            height: 30
                            radius: 8
                            color: _currentTextColorHex() === modelData.hex.toLowerCase()
                                ? (root.themeTokens ? root.themeTokens.actionBubbleBg : "#1FCBA37B")
                                : "transparent"

                            Row {
                                anchors.left: parent.left
                                anchors.leftMargin: 8
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 8

                                Rectangle {
                                    width: 14
                                    height: 14
                                    radius: 7
                                    color: modelData.hex
                                    border.width: 1
                                    border.color: Qt.rgba(0, 0, 0, 0.08)
                                }

                                Text {
                                    text: modelData.name
                                    font.pixelSize: 11
                                    color: root.themeTokens ? root.themeTokens.textPrimary : "#473C35"
                                }
                            }

                            MouseArea {
                                anchors.fill: parent
                                cursorShape: Qt.PointingHandCursor
                                onClicked: {
                                    root._refreshFormatState()
                                    textFormatHelper.setTextColor(modelData.hex)
                                    root._colorMenuVisible = false
                                }
                            }
                        }

                        ScrollBar.vertical: ScrollBar {
                            policy: ScrollBar.AsNeeded
                            width: root._menuScrollWidth
                            padding: root._menuScrollInset
                            background: Rectangle {
                                radius: width / 2
                                color: "transparent"
                            }
                            contentItem: Rectangle {
                                implicitWidth: root._menuScrollWidth - root._menuScrollInset * 2
                                implicitHeight: 52
                                radius: Math.min(width, height) / 2
                                antialiasing: true
                                color: colorMenuList.ScrollBar.vertical.active
                                    ? (themeTokens ? themeTokens.scrollbarActive : "#BCA895")
                                    : (themeTokens ? themeTokens.scrollbarInactive : "#D9CBBF")
                                opacity: colorMenuList.ScrollBar.vertical.active ? 1.0 : 0.5
                            }
                        }
                    }
                }
            }
        }
    }

    onContentReady: {
        imageManager.setTextArea(textArea.textArea)
        Qt.callLater(function() {
            imageManager.preloadImages()
        })
    }
}
