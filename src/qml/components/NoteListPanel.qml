import QtQuick
import QtQuick.Controls
import HuskarUI.Basic

Item {
    id: root

    property var themeTokens
    property bool darkTheme: themeTokens ? themeTokens.isDark : false
    property bool _compact: themeTokens ? themeTokens.isCompact : false
    property alias noteCount: noteListView.count
    property string currentCategory: qsTr("全部")
    property string currentCategoryKey: "all"
    property int currentNoteId: -1
    property color accentColor: "#8B8B8B"

    signal noteSelected(int noteId)
    signal addNoteRequested()
    signal deleteNoteRequested(int noteId)
    signal moveNoteRequested(int noteId, string category)

    // 清空搜索框（供外部调用，如切换分类时）
    function clearSearchBox() {
        searchInput.text = ""
    }

    // 搜索框状态
    property bool isSearching: noteController ? noteController.isSearching : false
    property string searchKeyword: noteController ? noteController.currentSearchKeyword : ""
    property string searchScope: noteController ? noteController.searchScope : "global"
    // 索引重建中：禁用搜索框（不读到不一致的索引）
    property bool isRebuildingIndex: noteController ? noteController.isRebuildingIndex : false

    // 时间格式化：0=相对 / 1=绝对(MM-dd hh:mm) / 2=完整(yyyy-MM-dd hh:mm)
    // 注意：model.updatedAt 是 QDateTime（非 JS Date），不能直接用 .getTime()/.valid
    function _formatTime(dateTime) {
        if (!dateTime) return ""
        var fmt = settingsManager ? settingsManager.timeFormat : 1
        if (fmt === 0) {
            // 相对时间：用 QDateTime 的 secsTo 无法从 QML 调，改为解析格式化后的字符串比较
            // 简化：对近期的显示"刚刚/N分钟前/N小时前/N天前"，用与当前时间的差值
            var now = new Date()
            // QDateTime → JS Date：用 ISO 字符串中转
            var iso = Qt.formatDateTime(dateTime, "yyyy-MM-dd hh:mm:ss")
            var noteDate = new Date(iso.replace(/-/g, "/"))
            var diff = (now.getTime() - noteDate.getTime()) / 1000
            if (isNaN(diff)) return Qt.formatDateTime(dateTime, "MM-dd hh:mm")
            if (diff < 60) return qsTr("刚刚")
            if (diff < 3600) return qsTr("%1分钟前").arg(Math.floor(diff / 60))
            if (diff < 86400) return qsTr("%1小时前").arg(Math.floor(diff / 3600))
            if (diff < 604800) return qsTr("%1天前").arg(Math.floor(diff / 86400))
            return Qt.formatDateTime(dateTime, "MM-dd")
        } else if (fmt === 2) {
            return Qt.formatDateTime(dateTime, "yyyy-MM-dd hh:mm")
        }
        return Qt.formatDateTime(dateTime, "MM-dd hh:mm")
    }

    Rectangle {
        anchors.fill: parent
        radius: 12
        color: themeTokens ? themeTokens.listPanelBg : "#FCF8F2"
        border.width: 1
        border.color: themeTokens ? themeTokens.listPanelBorder : "#E7DCCD"
    }

    Column {
        anchors.fill: parent
        anchors.leftMargin: 12
        anchors.rightMargin: 12
        anchors.topMargin: 12
        anchors.bottomMargin: 12
        spacing: 0

        Item {
            width: parent.width
            height: 54

            Column {
                anchors.left: parent.left
                anchors.verticalCenter: parent.verticalCenter
                spacing: 4

                Row {
                    spacing: 6

                    Text {
                        id: categoryText
                        text: root.currentCategory
                        font.pixelSize: root._compact ? 14 : 16
                        font.bold: true
                        color: themeTokens ? themeTokens.titleText : "#3F342D"
                    }

                    Rectangle {
                        id: countText
                        anchors.verticalCenter: parent.verticalCenter
                        width: countLabel.visible ? countLabel.implicitWidth + 14 : 0
                        height: 22
                        radius: 11
                        color: themeTokens
                            ? themeTokens.mixColor(root.accentColor, themeTokens.listPanelBg, darkTheme ? 0.68 : 0.88, darkTheme ? 0.95 : 1)
                            : Qt.rgba(root.accentColor.r, root.accentColor.g, root.accentColor.b, 0.12)
                        visible: countLabel.visible

                        Text {
                            id: countLabel
                            anchors.centerIn: parent
                            text: root.noteCount > 0 ? `${root.noteCount}` : ""
                            font.pixelSize: 11
                            font.bold: true
                            color: themeTokens ? themeTokens.textSecondary : "#8B7767"
                            visible: root.noteCount > 0
                        }
                    }
                }

                Text {
                    text: qsTr("便签摘要")
                    font.pixelSize: 11
                    color: themeTokens ? themeTokens.textMuted : "#9C8D80"
                    visible: !root._compact
                }
            }

            Rectangle {
                id: addButtonWrap
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                width: 28
                height: 28
                radius: 14
                color: addButtonArea.containsMouse
                    ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
                    : (themeTokens ? themeTokens.accentSelectionBg : Qt.rgba(0.0, 0.47, 0.83, 0.10))
                border.width: 1
                border.color: themeTokens ? themeTokens.accentSelectionBorder : Qt.rgba(0.0, 0.47, 0.83, 0.22)

                Behavior on color { ColorAnimation { duration: 140 } }

                Text {
                    id: addButton
                    anchors.centerIn: parent
                    text: "+"
                    font.pixelSize: 18
                    font.weight: Font.Medium
                    color: themeTokens ? themeTokens.accentBase : "#0078D4"
                }

                MouseArea {
                    id: addButtonArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: {
                        clickAnim.start()
                        root.addNoteRequested()
                    }
                }

                SequentialAnimation {
                    id: clickAnim
                    NumberAnimation { target: addButton; property: "scale"; to: 0.9; duration: 80; easing.type: Easing.InQuad }
                    NumberAnimation { target: addButton; property: "scale"; to: 1.0; duration: 120; easing.type: Easing.OutBack }
                }
            }
        }

        // 搜索框行（与列表 delegate 宽度对齐：减去 ListView rightMargin）
        Item {
            id: searchBarRow
            width: parent.width - 20
            height: 36

            Rectangle {
                anchors.fill: parent
                radius: 8
                color: themeTokens
                    ? (darkTheme ? "#2A2A2A" : "#F0F0F0")
                    : "#F0F0F0"
                border.width: 1
                border.color: searchInput.activeFocus
                    ? (themeTokens ? themeTokens.accentBase : "#0078D4")
                    : (themeTokens
                        ? (darkTheme ? "#3A3A3A" : "#E0E0E0")
                        : "#E0E0E0")

                Behavior on border.color { ColorAnimation { duration: 120 } }
            }

            // 左侧搜索图标
            HusIconText {
                anchors.left: parent.left
                anchors.leftMargin: 10
                anchors.verticalCenter: parent.verticalCenter
                iconSource: HusIcon.SearchOutlined
                iconSize: 16
                colorIcon: themeTokens ? themeTokens.textMuted : "#9C8D80"
            }

            TextField {
                id: searchInput
                anchors.left: parent.left
                anchors.leftMargin: 30
                anchors.right: scopeButton.left
                anchors.rightMargin: 4
                anchors.verticalCenter: parent.verticalCenter
                placeholderText: root.isRebuildingIndex ? qsTr("正在重建搜索索引...") : qsTr("搜索便签...")
                placeholderTextColor: themeTokens ? themeTokens.textMuted : "#9C8D80"
                color: themeTokens ? themeTokens.titleText : "#3F342D"
                font.pixelSize: 12
                selectByMouse: true
                verticalAlignment: TextInput.AlignVCenter
                background: Item {}
                enabled: !root.isRebuildingIndex

                onTextEdited: {
                    searchTimer.restart()
                }

                Keys.onEscapePressed: {
                    if (text.length > 0) {
                        text = ""
                        noteController.searchNotes("")
                    }
                }
            }

            // 右侧范围切换按钮（仅在"全部"分类下显示，其他分类默认按当前分类搜索）
            Rectangle {
                id: scopeButton
                anchors.right: clearButton.left
                anchors.rightMargin: 2
                anchors.verticalCenter: parent.verticalCenter
                width: root.currentCategoryKey !== "all" ? 28 : 0
                height: 24
                radius: 6
                color: scopeArea.containsMouse
                    ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
                    : "transparent"
                visible: root.currentCategoryKey !== "all"

                Behavior on color { ColorAnimation { duration: 120 } }
                Behavior on width { NumberAnimation { duration: 120 } }

                HusIconText {
                    anchors.centerIn: parent
                    iconSource: root.searchScope === "global" ? HusIcon.GlobalOutlined : HusIcon.FolderOutlined
                    iconSize: 16
                    colorIcon: themeTokens ? themeTokens.textMuted : "#9C8D80"
                }

                MouseArea {
                    id: scopeArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: {
                        noteController.setSearchScope(root.searchScope === "global" ? "category" : "global")
                    }

                    HusToolTip {
                        visible: scopeArea.containsMouse
                        text: root.searchScope === "global" ? qsTr("全局搜索（点击切换到当前分类）") : qsTr("当前分类搜索（点击切换到全局）")
                    }
                }
            }

            // 清除按钮
            Rectangle {
                id: clearButton
                anchors.right: parent.right
                anchors.rightMargin: 4
                anchors.verticalCenter: parent.verticalCenter
                width: searchInput.text.length > 0 ? 24 : 0
                height: 24
                radius: 6
                color: clearArea.containsMouse
                    ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
                    : "transparent"
                visible: searchInput.text.length > 0

                Behavior on color { ColorAnimation { duration: 120 } }
                Behavior on width { NumberAnimation { duration: 120 } }

                Text {
                    anchors.centerIn: parent
                    text: "✕"
                    font.pixelSize: 11
                    color: themeTokens ? themeTokens.textSecondary : "#5F5F5F"
                }

                MouseArea {
                    id: clearArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: {
                        searchInput.text = ""
                        noteController.clearSearch()
                    }
                }
            }

            // 防抖 Timer
            Timer {
                id: searchTimer
                interval: 300
                repeat: false
                onTriggered: {
                    noteController.searchNotes(searchInput.text)
                }
            }
        }

        // 分隔线：搜索框与列表项之间(顶部留 8px 间距,避免与搜索框底边重合)
        Item { width: parent.width; height: 8 }
        Rectangle {
            width: parent.width - 20
            height: 1
            color: themeTokens ? themeTokens.divider : "#14A0897D"
        }

        ListView {
            id: noteListView
            width: parent.width
            height: parent.height - 54 - 36 - 8 - 1 - 28
            clip: true
            spacing: 8
            rightMargin: 20

            model: noteController ? noteController.noteModel : null

            // 搜索空状态
            Text {
                anchors.centerIn: parent
                visible: root.isSearching && noteListView.count === 0
                text: qsTr('未找到包含 "%1" 的便签').arg(root.searchKeyword)
                color: themeTokens ? themeTokens.textMuted : "#9C8D80"
                font.pixelSize: 12
            }

            delegate: NoteListDelegate {
                width: noteListView.width - noteListView.rightMargin
                noteId: model.id
                noteTitle: model.title
                notePreview: model.digest
                noteTime: root._formatTime(model.updatedAt)
                noteCategory: model.category
                isSelected: model.id === root.currentNoteId
                accentColor: root.accentColor
                themeTokens: root.themeTokens
                highlightKeyword: root.isSearching ? root.searchKeyword : ""
                onSelected: root.noteSelected(model.id)
                onDeleteRequested: root.deleteNoteRequested(model.id)
                onMoveRequested: function(category) { root.moveNoteRequested(model.id, category) }
            }

            ScrollBar.vertical: ScrollBar {
                policy: ScrollBar.AsNeeded
                width: 6
                contentItem: Rectangle {
                    radius: 3
                    color: noteListView.ScrollBar.vertical.active
                        ? (themeTokens ? themeTokens.scrollbarActive : "#BCA895")
                        : (themeTokens ? themeTokens.scrollbarInactive : "#D9CBBF")
                    opacity: noteListView.ScrollBar.vertical.active ? 1.0 : 0.5

                    Behavior on opacity { NumberAnimation { duration: 200 } }
                    Behavior on color { ColorAnimation { duration: 200 } }
                }
            }
        }

        // 底部状态栏：便签总数
        Item {
            width: parent.width
            height: 28

            Rectangle {
                anchors.top: parent.top
                width: parent.width
                height: 1
                color: themeTokens ? themeTokens.divider : "#14A0897D"
            }

            Text {
                anchors.right: parent.right
                anchors.rightMargin: 16
                anchors.verticalCenter: parent.verticalCenter
                text: root.noteCount > 0 ? qsTr("共 %1 条").arg(root.noteCount) : qsTr("暂无便签")
                font.pixelSize: 11
                color: themeTokens ? themeTokens.textTertiary : "#A39588"
            }
        }
    }
}
