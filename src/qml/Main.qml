import QtQuick
import QtQuick.Controls
import HuskarUI.Basic
import "components"
import "theme"

HusWindow {
    id: root
    width: 940
    height: 620
    minimumWidth: appTheme.windowMinWidth
    minimumHeight: appTheme.windowMinHeight
    title: "QNote"
    color: appTheme.windowColor

    AppTheme {
        id: appTheme
        windowWidth: root.width
        windowHeight: root.height
    }

    // 三栏列宽策略：保护最小宽度 → 剩余按比例分配；极端紧凑下按 0.15:0.30:0.55 兜底
    readonly property real _contentRowAvailWidth: contentRow.width - contentRow.spacing * 2
    readonly property real _minColsTotal:
        appTheme.categoryColumnMinWidth + appTheme.listColumnMinWidth + appTheme.editorColumnMinWidth
    readonly property bool _extremeCompact: _contentRowAvailWidth < _minColsTotal
    readonly property real _categoryColActualWidth: {
        if (_extremeCompact)
            return Math.max(40, Math.floor(_contentRowAvailWidth * 0.15))
        // compact 下左栏仅图标，宽度锁定为 pref（48），不吃增量
        if (appTheme.isCompact)
            return appTheme.categoryColumnPrefWidth
        var remaining = _contentRowAvailWidth - appTheme.categoryColumnMinWidth - appTheme.listColumnMinWidth - appTheme.editorColumnMinWidth
        if (remaining <= 0)
            return appTheme.categoryColumnMinWidth
        return Math.min(appTheme.categoryColumnPrefWidth + Math.floor(remaining * 0.08), appTheme.categoryColumnPrefWidth + 24)
    }
    readonly property real _listColActualWidth: {
        if (_extremeCompact)
            return Math.max(140, Math.floor(_contentRowAvailWidth * 0.30))
        // compact 下中栏使用 pref（180），剩余全给 editor
        if (appTheme.isCompact)
            return Math.min(appTheme.listColumnPrefWidth, _contentRowAvailWidth - _categoryColActualWidth - appTheme.editorColumnMinWidth)
        var remaining = _contentRowAvailWidth - _categoryColActualWidth - appTheme.listColumnMinWidth - appTheme.editorColumnMinWidth
        if (remaining <= 0)
            return appTheme.listColumnMinWidth
        return Math.min(appTheme.listColumnPrefWidth + Math.floor(remaining * 0.22), appTheme.listColumnPrefWidth + 80)
    }
    readonly property real _editorColActualWidth: {
        var leftover = _contentRowAvailWidth - _categoryColActualWidth - _listColActualWidth
        return Math.max(_extremeCompact ? 180 : 200, leftover)
    }

    captionBar.winIcon: "qrc:/assets/note.svg"
    captionBar.winIconWidth: 16
    captionBar.winIconHeight: 16
    captionBar.showWinIcon: true
    captionBar.showThemeButton: true
    captionBar.showTopButton: true
    captionBar.topButtonChecked: Qt.binding(function() { return root.stickOnTop })
    captionBar.closeCallback: () => root.hide()
    captionBar.themeCallback: cycleThemeMode
    captionBar.topCallback: function(checked) { root.stickOnTop = checked; root._applyWindowFlags(root._isEdgeHidden) }

    property bool stickOnTop: settingsManager ? settingsManager.stickOnTopDefault : false
    property bool _isEdgeHidden: false

    // 统一 flags 应用：hidden 态必加 StaysOnTop（隐藏窄条需可见）；stickOnTop 时 normal 态也加
    function _applyWindowFlags(hidden) {
        root._isEdgeHidden = hidden
        if (hidden) {
            root.flags = Qt.Window | Qt.WindowStaysOnTopHint
        } else if (root.stickOnTop) {
            root.flags = Qt.Window | Qt.WindowStaysOnTopHint
        } else {
            root.flags = Qt.Window
        }

        // 任务栏图标控制：通过 Windows API 而非改变窗口类型
        if (windowsTaskbarHelper && settingsManager) {
            var shouldHideTaskbar = hidden && settingsManager.hideTaskbarIconOnEdgeHide
            windowsTaskbarHelper.setTaskbarIconVisible(root, !shouldHideTaskbar)
        }
    }

    onClosing: function(close) {
        close.accepted = false
        root.hide()
    }

    // 最小化拦截：转 hide() 触发 GPU 资源释放（setPersistentSceneGraph(false)）。
    // 直接最小化不会触发 Qt 的 visibleChanged，资源不释放，内存居高。
    onVisibilityChanged: {
        if (visibility === Window.Minimized) {
            root.hide()
            visibility = Window.Windowed
        }
    }

    property int currentNoteId: -1
    property real normalY: y

    function syncWindowGeometry() {
        edgeHideController.updateWindowGeometry(root.x, root.y, root.width, root.height)
    }

    onActiveChanged: edgeHideController.onActiveChanged(active)
    onXChanged: { syncWindowGeometry(); _scheduleGeometrySave() }
    onWidthChanged: { syncWindowGeometry(); _scheduleGeometrySave() }
    onHeightChanged: { syncWindowGeometry(); _scheduleGeometrySave() }

    onYChanged: {
        if (!hideAnim.running && !showAnim.running) {
            if (root.y >= 0 && root.y <= 10)
                root.y = 0
            syncWindowGeometry()
        }
        _scheduleGeometrySave()
    }

    function _scheduleGeometrySave() {
        if (settingsManager && settingsManager.rememberWindowGeometry)
            geometrySaveTimer.restart()
    }

    Connections {
        target: edgeHideController
        function onHideRequested() {
            root.normalY = root.y
            hideAnim.from = root.y
            hideAnim.to = -root.height + 3
            hideAnim.start()
        }
        function onShowRequested() {
            root._applyWindowFlags(false)
            root.show()
            showAnim.from = root.y
            showAnim.to = root.normalY > 10 ? 0 : root.normalY
            showAnim.start()
        }
    }

    Connections {
        target: settingsManager
        function onSettingsChanged() {
            // 任务栏图标开关改动立即生效：若当前处于贴边隐藏态，立即更新任务栏图标显隐
            if (root._isEdgeHidden && windowsTaskbarHelper) {
                var shouldHideTaskbar = settingsManager.hideTaskbarIconOnEdgeHide
                windowsTaskbarHelper.setTaskbarIconVisible(root, !shouldHideTaskbar)
            }
        }
    }

    PropertyAnimation {
        id: hideAnim
        target: root
        property: "y"
        duration: 200
        easing.type: Easing.InQuad
        onFinished: {
            root._applyWindowFlags(true)
            root.show()
            syncWindowGeometry()
            edgeHideController.notifyHideFinished()
        }
    }

    PropertyAnimation {
        id: showAnim
        target: root
        property: "y"
        duration: 200
        easing.type: Easing.OutQuad
        onFinished: {
            root._applyWindowFlags(false)
            root.show()
            syncWindowGeometry()
            Qt.callLater(function() {
                edgeHideController.notifyShowFinished()
            })
        }
    }

    Timer {
        id: edgePointerSyncTimer
        interval: 100
        repeat: true
        running: edgeHideController.edgeEnabled
        onTriggered: edgeHideController.syncPointerState(root.active)
    }

    Timer {
        id: saveTimer
        interval: settingsManager ? settingsManager.autoSaveInterval : 500
        onTriggered: saveCurrentNote()
    }

    property string editorContent: ""
    property string savedContent: ""
    property string currentNoteTime: ""
    property string currentNoteTitle: ""
    property bool currentTitleManual: false

    function cycleThemeMode() {
        HusTheme.darkMode = (HusTheme.darkMode === HusTheme.Dark) ? HusTheme.Light : HusTheme.Dark
    }

    function saveCurrentNote() {
        if (root.currentNoteId > 0 && editorContent !== savedContent) {
            noteController.updateNote(root.currentNoteId, editorContent)
            currentNoteTime = noteController.noteModel.getNoteUpdatedAt(root.currentNoteId)
            currentNoteTitle = noteController.noteModel.getNoteTitle(root.currentNoteId)
            currentTitleManual = noteController.noteModel.getNoteTitleManual(root.currentNoteId)
        }
    }

    property int _pendingDeleteNoteId: -1
    property string _pendingDeleteCategory: ""
    property string _pendingDeleteCategoryName: ""

    function _confirmDelete(noteId) {
        if (settingsManager && settingsManager.confirmBeforeDelete) {
            root._pendingDeleteNoteId = noteId
            deleteConfirmDialog.open()
        } else {
            root._doDelete(noteId)
        }
    }

    function _doDelete(noteId) {
        saveTimer.stop()
        noteController.deleteNote(noteId)
        if (root.currentNoteId === noteId) {
            root.currentNoteId = -1
            root.savedContent = ""
            editorContent = ""
            currentNoteTime = ""
            currentNoteTitle = ""
            currentTitleManual = false
            stickyNote.showContent = false
        }
    }

    function _doDeleteCategory(key) {
        categoryManager.deleteCategory(key)
        // 切到"全部"分类,刷新列表
        categoryTabBar.currentCategory = 0
        var allName = categoryManager.categories.length > 0 ? categoryManager.categories[0].name : qsTr("全部")
        noteListPanel.currentCategory = allName
        noteListPanel.currentCategoryKey = "all"
        noteListPanel.currentNoteId = -1
        root.currentNoteId = -1
        root.savedContent = ""
        editorContent = ""
        currentNoteTime = ""
        currentNoteTitle = ""
        currentTitleManual = false
        stickyNote.showContent = false
        noteController.loadNotesSorted("all", settingsManager ? settingsManager.noteSortOrder : 0, allName)
    }

    // 自定义删除确认 Popup（替代 Qt 原生 MessageDialog，样式统一）
    Popup {
        id: deleteConfirmDialog
        parent: Overlay.overlay
        anchors.centerIn: parent
        modal: true
        dim: true
        padding: 0
        width: 280
        height: 132
        closePolicy: Popup.CloseOnEscape

        background: Rectangle {
            radius: 12
            color: appTheme.listPanelBg
            border.width: 1
            border.color: appTheme.divider
        }

        Column {
            anchors.fill: parent
            spacing: 0

            Item {
                width: parent.width
                height: parent.height - 52

                Text {
                    anchors.centerIn: parent
                    text: qsTr("确定要删除这条便签吗？")
                    font.pixelSize: 13
                    color: appTheme.titleText
                }
            }

            Item {
                width: parent.width
                height: 52

                Row {
                    anchors.centerIn: parent
                    spacing: 10

                    Rectangle {
                        width: 64; height: 28; radius: 5
                        color: delCancelArea.containsMouse ? appTheme.accentHoverBg : "transparent"
                        border.width: 1
                        border.color: appTheme.divider
                        Text { anchors.centerIn: parent; text: qsTr("取消"); font.pixelSize: 12; color: appTheme.textSecondary }
                        MouseArea {
                            id: delCancelArea
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: {
                                root._pendingDeleteNoteId = -1
                                deleteConfirmDialog.close()
                            }
                        }
                    }

                    Rectangle {
                        width: 64; height: 28; radius: 5
                        color: delOkArea.containsMouse ? appTheme.accentHover : appTheme.accentBase
                        Text { anchors.centerIn: parent; text: qsTr("删除"); font.pixelSize: 12; font.bold: true; color: "#FFFFFF" }
                        MouseArea {
                            id: delOkArea
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: {
                                root._doDelete(root._pendingDeleteNoteId)
                                root._pendingDeleteNoteId = -1
                                deleteConfirmDialog.close()
                            }
                        }
                    }
                }
            }
        }
    }

    // 分类删除确认 Popup(提示会连带删除分类下便签)
    Popup {
        id: categoryDeleteConfirmDialog
        parent: Overlay.overlay
        anchors.centerIn: parent
        modal: true
        dim: true
        padding: 0
        width: 320
        height: 148
        closePolicy: Popup.CloseOnEscape

        background: Rectangle {
            radius: 12
            color: appTheme.listPanelBg
            border.width: 1
            border.color: appTheme.divider
        }

        Column {
            anchors.fill: parent
            spacing: 0

            Item {
                width: parent.width
                height: parent.height - 52

                Column {
                    anchors.centerIn: parent
                    spacing: 4

                    Text {
                        anchors.horizontalCenter: parent.horizontalCenter
                        text: qsTr("确定要删除分类「%1」吗？").arg(root._pendingDeleteCategoryName)
                        font.pixelSize: 13
                        color: appTheme.titleText
                    }
                    Text {
                        anchors.horizontalCenter: parent.horizontalCenter
                        text: qsTr("该分类下的所有便签将被一并删除")
                        font.pixelSize: 11
                        color: appTheme.textSecondary
                    }
                }
            }

            Item {
                width: parent.width
                height: 52

                Row {
                    anchors.centerIn: parent
                    spacing: 10

                    Rectangle {
                        width: 64; height: 28; radius: 5
                        color: catDelCancelArea.containsMouse ? appTheme.accentHoverBg : "transparent"
                        border.width: 1
                        border.color: appTheme.divider
                        Text { anchors.centerIn: parent; text: qsTr("取消"); font.pixelSize: 12; color: appTheme.textSecondary }
                        MouseArea {
                            id: catDelCancelArea
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: {
                                root._pendingDeleteCategory = ""
                                root._pendingDeleteCategoryName = ""
                                categoryDeleteConfirmDialog.close()
                            }
                        }
                    }

                    Rectangle {
                        width: 64; height: 28; radius: 5
                        color: catDelOkArea.containsMouse ? appTheme.accentHover : appTheme.accentBase
                        Text { anchors.centerIn: parent; text: qsTr("删除"); font.pixelSize: 12; font.bold: true; color: "#FFFFFF" }
                        MouseArea {
                            id: catDelOkArea
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: {
                                root._doDeleteCategory(root._pendingDeleteCategory)
                                root._pendingDeleteCategory = ""
                                root._pendingDeleteCategoryName = ""
                                categoryDeleteConfirmDialog.close()
                            }
                        }
                    }
                }
            }
        }
    }

    // 背景层 1/3：整体暖色渐变（桌面氛围）
    Rectangle {
        id: bgLayer
        anchors.fill: parent
        gradient: Gradient {
            GradientStop { position: 0.0; color: appTheme.rootGradientStart }
            GradientStop { position: 0.55; color: appTheme.rootGradientMid }
            GradientStop { position: 1.0; color: appTheme.rootGradientEnd }
        }
    }

    // 背景层 2/3：柔光晕染（避免背景过平，制造画板光感）
    Rectangle {
        anchors.fill: parent
        color: appTheme.rootGlow
    }

    Rectangle {
        id: ambientLight
        anchors.top: parent.top
        anchors.horizontalCenter: parent.horizontalCenter
        width: parent.width * appTheme.rootAmbientRadius
        height: parent.height * 0.5
        radius: height
        color: appTheme.rootAmbientLight
        opacity: 0.6
    }

    Row {
        id: contentRow
        anchors.fill: parent
        anchors.leftMargin: 12
        anchors.rightMargin: 12
        anchors.topMargin: root.captionBar.height + 14
        anchors.bottomMargin: 16
        spacing: 8

        Item {
            id: categoryColumn
            width: root._categoryColActualWidth
            height: parent.height

            // 左栏壳层：最轻、最透明，偏导航壳
            Rectangle {
                id: categoryPanelShadowSource
                anchors.fill: parent
                radius: 12
                visible: false
                color: appTheme.sidebarShellBg
                border.width: 1
                border.color: appTheme.sidebarShellBorder
            }

            HusShadow {
                anchors.fill: categoryPanelShadowSource
                source: categoryPanelShadowSource
                shadowColor: "#000000"
                shadowOpacity: appTheme.sidebarShadowOpacity
                shadowScale: appTheme.sidebarShadowScale
                shadowVerticalOffset: 6
                shadowHorizontalOffset: 0
            }

            Rectangle {
                anchors.fill: parent
                radius: 12
                color: appTheme.sidebarShellBg
                border.width: 1
                border.color: appTheme.sidebarShellBorder

                Rectangle {
                    anchors.fill: parent
                    anchors.margins: 1
                    radius: 11
                    color: appTheme.sidebarShellInnerVeil
                }
            }

            CategoryTabBar {
                id: categoryTabBar
                anchors.fill: parent
                anchors.margins: 10
                themeTokens: appTheme
                onCategorySelected: function(key, displayName) {
                    saveTimer.stop()
                    saveCurrentNote()
                    root.currentNoteId = -1
                    root.savedContent = ""
                    editorContent = ""
                    currentNoteTime = ""
                    currentNoteTitle = ""
                    currentTitleManual = false
                    stickyNote.showContent = false
                    noteListPanel.currentCategory = displayName
                    noteListPanel.currentCategoryKey = key
                    noteListPanel.currentNoteId = -1
                    noteListPanel.clearSearchBox()
                    noteController.clearSearch()
                    noteController.loadNotesSorted(key, settingsManager ? settingsManager.noteSortOrder : 0, displayName)
                }
                onCategoryDeleted: function(key) {
                    // 确认删除
                    if (settingsManager && settingsManager.confirmBeforeDelete) {
                        root._pendingDeleteCategory = key
                        root._pendingDeleteCategoryName = (function() {
                            var cats = categoryManager.categories
                            for (var i = 0; i < cats.length; i++) {
                                if ((cats[i].key || cats[i].name) === key) return cats[i].name
                            }
                            return key
                        })()
                        categoryDeleteConfirmDialog.open()
                    } else {
                        root._doDeleteCategory(key)
                    }
                }
                onCategoryUpdated: function(oldName, newName) {
                    // 改名后,如果当前选中是被改名的分类,刷新列表
                    if (noteListPanel.currentCategory === oldName) {
                        noteListPanel.currentCategory = newName
                        noteListPanel.currentCategoryKey = newName
                        noteController.loadNotesSorted(newName, settingsManager ? settingsManager.noteSortOrder : 0, newName)
                    } else if (noteListPanel.currentCategoryKey === "all") {
                        var allName = categoryManager.categories.length > 0 ? categoryManager.categories[0].name : qsTr("全部")
                        noteController.loadNotesSorted("all", settingsManager ? settingsManager.noteSortOrder : 0, allName)
                    }
                }
                onCategoryRenameFailed: function(attemptedName) {
                    husMessage.warning(qsTr("分类名称「%1」已存在").arg(attemptedName))
                }
            }
        }

        Item {
            id: listColumn
            width: root._listColActualWidth
            height: parent.height

            Rectangle {
                id: listPanelShadowSource
                anchors.fill: parent
                radius: 12
                visible: false
                color: appTheme.listPanelBg
                border.width: 1
                border.color: appTheme.listPanelBorder
            }

            HusShadow {
                anchors.fill: listPanelShadowSource
                source: listPanelShadowSource
                shadowColor: "#000000"
                shadowOpacity: appTheme.panelShadowOpacity
                shadowScale: appTheme.panelShadowScale
                shadowVerticalOffset: 5
                shadowHorizontalOffset: 0
            }

            // 中栏承托壳层：居中强度的内层 veil
            Rectangle {
                anchors.fill: parent
                radius: 12
                color: "transparent"
                border.width: 0

                Rectangle {
                    anchors.fill: parent
                    anchors.margins: 1
                    radius: 11
                    color: appTheme.listPanelInnerVeil
                }
            }

            NoteListPanel {
                id: noteListPanel
                anchors.fill: parent
                anchors.margins: 1
                accentColor: categoryTabBar.currentColor
                themeTokens: appTheme
                onAddNoteRequested: {
                    noteController.addNote(currentCategory)
                }
                onNoteSelected: function(noteId) {
                    saveTimer.stop()
                    saveCurrentNote()
                    root.currentNoteId = noteId
                    noteListPanel.currentNoteId = noteId
                    var content = noteController.noteModel.getNoteContent(noteId)
                    root.savedContent = content
                    editorContent = content
                    currentNoteTime = noteController.noteModel.getNoteUpdatedAt(noteId)
                    currentNoteTitle = noteController.noteModel.getNoteTitle(noteId)
                    currentTitleManual = noteController.noteModel.getNoteTitleManual(noteId)
                    stickyNote.showContent = true
                }
                onDeleteNoteRequested: function(noteId) {
                    root._confirmDelete(noteId)
                }
                onMoveNoteRequested: function(noteId, category) {
                    noteController.moveNoteToCategory(noteId, category)
                    if (root.currentNoteId === noteId) {
                        root.currentNoteId = -1
                        root.savedContent = ""
                        editorContent = ""
                        currentNoteTime = ""
                        currentNoteTitle = ""
                        currentTitleManual = false
                        stickyNote.showContent = false
                    }
                }
            }
        }

        Item {
            id: editorColumn
            width: root._editorColActualWidth
            height: parent.height

            Rectangle {
                id: stickyShadowSource
                anchors.fill: parent
                visible: false
                radius: 12
                color: appTheme.editorPanelBg
                border.width: 1
                border.color: appTheme.editorPanelBorder
            }

            HusShadow {
                anchors.fill: stickyShadowSource
                source: stickyShadowSource
                shadowColor: "#000000"
                shadowOpacity: appTheme.editorShadowOpacity
                shadowScale: appTheme.editorShadowScale
                shadowVerticalOffset: 6
                shadowHorizontalOffset: 0
            }

            // 右栏承托壳层：最完整的内层 veil + 顶部高光
            Rectangle {
                anchors.fill: parent
                radius: 12
                color: "transparent"
                border.width: 0

                Rectangle {
                    anchors.fill: parent
                    anchors.margins: 1
                    radius: 11
                    color: appTheme.editorPanelInnerVeil
                }
            }

            StickyNoteBackground {
                id: stickyNote
                anchors.fill: parent
                anchors.margins: 1
                showContent: false
                editorText: editorContent
                noteColor: categoryTabBar.currentColor
                accentColor: categoryTabBar.currentColor
                themeTokens: appTheme
                noteTime: currentNoteTime
                noteCategory: noteListPanel.currentCategory
                noteTitle: currentNoteTitle
                titleManual: currentTitleManual
                onTextEdited: function(text) {
                    editorContent = text
                    saveTimer.restart()
                }
                onDeleteNoteRequested: {
                    if (root.currentNoteId > 0)
                        root._confirmDelete(root.currentNoteId)
                }
                onTitleEdited: function(title) {
                    if (root.currentNoteId > 0) {
                        noteController.updateTitle(root.currentNoteId, title)
                        currentNoteTitle = title
                        currentTitleManual = true
                    }
                }
            }
        }
    }

    Component.onCompleted: {
        // 默认主题：0=跟随系统(暂按亮色) / 1=亮色 / 2=暗色
        _applyThemeMode()
        // 应用 UI 字体到 HusTheme（HuskarUI 组件用 HusTheme 主题字体渲染，
        // 不读 QApplication::setFont。用 installThemePrimaryFontFamiliesBase 设置）
        if (settingsManager) {
            HusTheme.installThemePrimaryFontFamiliesBase(settingsManager.uiFontFamily)
            HusTheme.installThemePrimaryFontSizeBase(settingsManager.uiFontSize)
        }
        // 记忆窗口位置：恢复上次尺寸/位置
        if (settingsManager && settingsManager.rememberWindowGeometry) {
            var w = settingsManager.windowWidth
            var h = settingsManager.windowHeight
            if (w >= root.minimumWidth) root.width = w
            if (h >= root.minimumHeight) root.height = h
            var px = settingsManager.windowX
            var py = settingsManager.windowY
            if (px >= -50 && py >= -50) {
                root.x = px
                root.y = py
            }
        }
        var allName = categoryManager.categories.length > 0 ? categoryManager.categories[0].name : qsTr("全部")
        noteController.loadNotesSorted("all", settingsManager ? settingsManager.noteSortOrder : 0, allName)
        syncWindowGeometry()
        edgeHideController.syncPointerState(root.active)
    }

    function _applyThemeMode() {
        var themeMode = settingsManager ? settingsManager.defaultThemeMode : 0
        if (themeMode === 2)
            HusTheme.darkMode = HusTheme.Dark
        else
            HusTheme.darkMode = HusTheme.Light
    }

    // 记忆窗口位置：关闭时持久化（防抖）
    Timer {
        id: geometrySaveTimer
        interval: 800
        onTriggered: {
            if (settingsManager && settingsManager.rememberWindowGeometry) {
                settingsManager.windowX = root.x
                settingsManager.windowY = root.y
                settingsManager.windowWidth = root.width
                settingsManager.windowHeight = root.height
                settingsManager.save()
            }
        }
    }

    SettingsDialog {
        id: settingsDialog
        themeTokens: appTheme
        // 设置弹窗关闭后，若字体改动需重启，弹 HusModal 确认
        onClosed: {
            if (settingsDialog.needRestart) {
                restartConfirmModal.open()
            }
        }
    }

    // 字体改动重启确认弹窗（HusModal，Ant Design 风格）
    HusModal {
        id: restartConfirmModal
        width: 420
        title: qsTr("字体修改需要重启")
        description: qsTr("字体修改将在重启后生效，是否立即重启？")
        confirmText: qsTr("立即重启")
        cancelText: qsTr("稍后")
        closable: false       // 不显示右上角 ×（必须通过按钮选择）
        maskClosable: false   // 点遮罩不关闭（强制用户做选择）
        onConfirm: {
            close()
            if (!settingsManager.restartApplication()) {
                globalMessage.error(qsTr("重启失败，请手动关闭后重新启动"))
            }
        }
        onCancel: close()
    }

    HusMessage {
        id: globalMessage
        parent: Overlay.overlay
        z: 9999
        width: root.width
        anchors.top: parent.top
        anchors.horizontalCenter: parent.horizontalCenter
    }

    Connections {
        target: backupManager
        function onBackupFinished(result) {
            if (result.success)
                globalMessage.success(qsTr("备份成功"))
            else
                globalMessage.error(qsTr("备份失败：%1").arg(result.error || qsTr("未知错误")))
        }
        function onRestoreFinished(result) {
            if (result.success)
                globalMessage.success(qsTr("恢复成功"))
            else
                globalMessage.error(qsTr("恢复失败：%1").arg(result.error || qsTr("未知错误")))
        }
    }

    // 全屏点击穿透监听(置于最顶层):点击任意区域时,若分类正在行内编辑,显式提交修改
    // propagateComposedEvents 让事件继续传播给下层控件,不阻塞按钮/列表交互
    // cursorShape: undefined —— MouseArea 默认 ArrowCursor 即便 disabled 也会遮盖全窗口,
    // 导致下层文本控件(HusTextArea/TextField 等)的 IBeam 光标失效。设 undefined 让光标透传。
    MouseArea {
        id: globalClickArea
        anchors.fill: parent
        z: 9999
        enabled: categoryTabBar._editingIndex >= 0
        propagateComposedEvents: true
        cursorShape: undefined
        onPressed: function(mouse) {
            mouse.accepted = false
            categoryTabBar._commitEditingCategory()
        }
    }
}
