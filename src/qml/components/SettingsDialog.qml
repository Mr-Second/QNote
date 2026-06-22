import QtQuick
import QtQuick.Controls
import HuskarUI.Basic

Popup {
    id: root
    width: 520
    height: 480
    parent: Overlay.overlay
    anchors.centerIn: parent
    modal: true
    dim: true
    focus: true
    padding: 0

    property var themeTokens: null
    // 字体改动后需重启生效（HusTheme/HusTextArea 字体在启动时应用）
    property bool needRestart: false
    // 记录打开设置时的原始字体，用于检测是否需要提示重启
    property string _origUiFont: ""
    property string _origEditorFont: ""
    // 系统字体枚举（Qt.fontFamilies 返回字符串数组，转为 HusSelect model 需要的对象数组）
    readonly property var _systemFontsModel: {
        var fams = Qt.fontFamilies()
        var result = []
        for (var i = 0; i < fams.length; i++) {
            result.push({ label: fams[i], value: fams[i] })
        }
        return result
    }

    background: Rectangle {
        radius: 12
        color: root.themeTokens ? root.themeTokens.listPanelBg : "#FFFFFF"
        border.width: 1
        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
    }

    function _initControls() {
        if (!settingsManager) return
        edgeSwitch.checked = edgeHideController ? edgeHideController.edgeEnabled : true
        hideTaskbarIconSwitch.checked = settingsManager.hideTaskbarIconOnEdgeHide
        minimizeSwitch.checked = settingsManager.startMinimized
        stickOnTopSwitch.checked = settingsManager.stickOnTopDefault
        rememberWindowSwitch.checked = settingsManager.rememberWindowGeometry
        noOutsideCloseSwitch.checked = false  // 默认允许点击外部关闭
        autoSaveRadio.currentCheckedIndex = _autoSaveIndex(settingsManager.autoSaveInterval)
        themeModeRadio.currentCheckedIndex = settingsManager.defaultThemeMode
        imageModeRadio.currentCheckedIndex = settingsManager.imageStorageMode
        picgoHostInput.text = settingsManager.picgoHost
        picgoPortInput.text = settingsManager.picgoPort > 0 ? settingsManager.picgoPort.toString() : ""
        listDensityRadio.currentCheckedIndex = settingsManager.listDensity
        timeFormatRadio.currentCheckedIndex = settingsManager.timeFormat
        sortRadio.currentCheckedIndex = settingsManager.noteSortOrder
        confirmDeleteSwitch.checked = settingsManager.confirmBeforeDelete
        autostartSwitch.checked = settingsManager.launchAtStartup
        // 字体下拉初始化：在 model 就绪后定位到当前字体
        _selectFontByValue(uiFontSelect, settingsManager.uiFontFamily)
        _selectFontByValue(editorFontSelect, settingsManager.editorFontFamily)
        var curLang = translationManager ? translationManager.currentLanguage : "zh_CN"
        languageRadio.currentCheckedIndex = (curLang === "en") ? 1 : 0
    }

    // 字体下拉的 model 在 Component.onCompleted 里异步赋值（Qt.fontFamilies()），
    // 初始化时若 model 还没建好，currentValue 匹配会失败，这里加一个 fallback：
    // 在 onCurrentValueChanged 不会反向写 settingsManager（只读），所以重复定位安全。
    function _selectFontByValue(select, family) {
        if (!select || !family) return
        var m = select.model || []
        for (var i = 0; i < m.length; i++) {
            if (m[i].value === family) {
                select.currentIndex = i
                return
            }
        }
        // 当前字体不在系统枚举（如旧 DB 残留"Source Han Sans SC VF"），editable 模式显示原值
        select.currentIndex = -1
        if (select.editText !== undefined) {
            select.editText = family
        }
    }

    function _autoSaveIndex(ms) {
        var opts = [300, 500, 1000, 2000]
        for (var i = 0; i < opts.length; i++)
            if (opts[i] === ms) return i
        return 1  // 默认 500ms
    }

    function _autoSaveValue(idx) {
        var opts = [300, 500, 1000, 2000]
        return opts[idx] || 500
    }

    function _applyAndClose() {
        if (!settingsManager) { root.close(); return }
        settingsManager.startMinimized = minimizeSwitch.checked
        settingsManager.stickOnTopDefault = stickOnTopSwitch.checked
        settingsManager.rememberWindowGeometry = rememberWindowSwitch.checked
        settingsManager.autoSaveInterval = _autoSaveValue(autoSaveRadio.currentCheckedIndex)
        settingsManager.defaultThemeMode = themeModeRadio.currentCheckedIndex
        settingsManager.listDensity = listDensityRadio.currentCheckedIndex
        settingsManager.timeFormat = timeFormatRadio.currentCheckedIndex
        settingsManager.noteSortOrder = sortRadio.currentCheckedIndex
        settingsManager.confirmBeforeDelete = confirmDeleteSwitch.checked
        settingsManager.launchAtStartup = autostartSwitch.checked
        settingsManager.imageStorageMode = imageModeRadio.currentCheckedIndex
        settingsManager.picgoHost = picgoHostInput.text
        var portNum = parseInt(picgoPortInput.text)
        if (!isNaN(portNum)) settingsManager.picgoPort = portNum
        // 字体保存：UI 字体改动需重启；编辑器字体即时生效（NoteEditor 绑定 settingsManager.editorFontFamily）
        // HusSelect 继承 ComboBox，currentValue 来自 valueRole；editable 模式下手输的值走 editText
        var newUiFont = uiFontSelect.currentValue || uiFontSelect.editText || uiFontSelect.currentText || ""
        var newEditorFont = editorFontSelect.currentValue || editorFontSelect.editText || editorFontSelect.currentText || ""
        if (newUiFont && newUiFont !== root._origUiFont) {
            settingsManager.uiFontFamily = newUiFont
            root.needRestart = true
        }
        if (newEditorFont && newEditorFont !== root._origEditorFont) {
            settingsManager.editorFontFamily = newEditorFont
            root.needRestart = true
        }
        // 排序规则变化立即生效（NoteController 用新 sortOrder 重新加载当前视图）
        if (noteController)
            noteController.applySortOrder(sortRadio.currentCheckedIndex)
        // 语言切换（即时生效）
        if (translationManager) {
            translationManager.setLanguage(languageRadio.currentCheckedValue)
        }
        settingsManager.language = languageRadio.currentCheckedValue
        settingsManager.hideTaskbarIconOnEdgeHide = hideTaskbarIconSwitch.checked
        settingsManager.save()
        if (edgeHideController)
            edgeHideController.edgeEnabled = edgeSwitch.checked
        // 主题即时生效
        var themeMode = themeModeRadio.currentCheckedIndex
        if (themeMode === 2)
            HusTheme.darkMode = HusTheme.Dark
        else
            HusTheme.darkMode = HusTheme.Light
        // 关闭设置弹窗。若字体改动（needRestart=true），
        // 由 Main.qml 监听 closed 信号弹独立的重启确认 Popup
        root.close()
    }

    function showDialog() {
        root.needRestart = false
        open()
        Qt.callLater(_initControls)
        // _initControls 异步执行（Qt.callLater），原始字体在 _initControls 里赋值
        // 这里再 callLater 一次确保 model 已就绪后记录
        Qt.callLater(function() {
            root._origUiFont = settingsManager ? settingsManager.uiFontFamily : ""
            root._origEditorFont = settingsManager ? settingsManager.editorFontFamily : ""
        })
    }

    // closePolicy 受"禁用点击外部关闭"开关控制
    closePolicy: noOutsideCloseSwitch.checked ? Popup.CloseOnEscape : (Popup.CloseOnEscape | Popup.CloseOnPressOutside)

    Column {
        anchors.fill: parent
        spacing: 0

        // 标题栏
        Item {
            width: parent.width
            height: 44

            Text {
                anchors.left: parent.left
                anchors.leftMargin: 18
                anchors.verticalCenter: parent.verticalCenter
                text: qsTr("设置")
                font.pixelSize: 15
                font.bold: true
                color: root.themeTokens ? root.themeTokens.titleText : "#1A1A1A"
            }

            Rectangle {
                anchors.right: parent.right
                anchors.top: parent.top
                anchors.rightMargin: 10
                anchors.topMargin: 8
                width: 28; height: 28; radius: 14
                color: closeMouse.containsMouse
                    ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                    : "transparent"
                Text { anchors.centerIn: parent; text: "×"; font.pixelSize: 18; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                MouseArea { id: closeMouse; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: root.close() }
            }
        }

        Rectangle { width: parent.width; height: 1; color: root.themeTokens ? root.themeTokens.divider : "#14A0897D" }

        // 内容区
        Flickable {
            id: contentFlick
            width: parent.width
            height: parent.height - 44 - 1 - 60
            clip: true
            contentWidth: parent.width
            contentHeight: contentColumn.implicitHeight
            flickableDirection: Flickable.VerticalFlick
            boundsBehavior: Flickable.StopAtBounds

            Column {
                id: contentColumn
                width: parent.width
                spacing: 24
                leftPadding: 24
                rightPadding: 24
                topPadding: 20
                bottomPadding: 20

                // === 常规分组 ===
                Column {
                    width: parent.width - 48
                    spacing: 14

                    Text {
                        text: qsTr("常规")
                        font.pixelSize: 13; font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("语言"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: languageRadio
                            initCheckedIndex: 0
                            model: [
                                { label: qsTr("简体中文"), value: "zh_CN" },
                                { label: "English", value: "en" }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("贴边隐藏"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: edgeSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text {
                            width: 180
                            anchors.verticalCenter: parent.verticalCenter
                            text: qsTr("贴边隐藏时隐藏任务栏图标")
                            font.pixelSize: 12
                            color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                        }
                        HusSwitch { id: hideTaskbarIconSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("窗口默认置顶"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: stickOnTopSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("开机自启动"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: autostartSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("启动最小化到托盘"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: minimizeSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("记忆窗口位置"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: rememberWindowSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 180; anchors.verticalCenter: parent.verticalCenter; text: qsTr("禁用点击外部关闭"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: noOutsideCloseSwitch }
                    }
                }

                // === 显示分组 ===
                Column {
                    width: parent.width - 48
                    spacing: 14

                    Text {
                        text: qsTr("显示")
                        font.pixelSize: 13; font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("列表密度"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: listDensityRadio
                            initCheckedIndex: 1
                            model: [
                                { label: qsTr("紧凑"), value: 0 },
                                { label: qsTr("标准"), value: 1 },
                                { label: qsTr("宽松"), value: 2 }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("时间格式"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: timeFormatRadio
                            initCheckedIndex: 1
                            model: [
                                { label: qsTr("相对"), value: 0 },
                                { label: qsTr("绝对"), value: 1 },
                                { label: qsTr("完整"), value: 2 }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("便签排序"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: sortRadio
                            initCheckedIndex: 0
                            model: [
                                { label: qsTr("更新时间"), value: 0 },
                                { label: qsTr("创建时间"), value: 1 },
                                { label: qsTr("标题"), value: 2 }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("删除前确认"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSwitch { id: confirmDeleteSwitch }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("UI 字体"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSelect {
                            id: uiFontSelect
                            width: 220
                            editable: true
                            showToolTip: true
                            defaultPopupMaxHeight: 240
                            placeholderText: qsTr("选择字体")
                            // 改动需重启生效（HusWindow 不支持运行时 font 覆盖）
                            textRole: "label"
                            valueRole: "value"
                            model: root._systemFontsModel
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("编辑器字体"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusSelect {
                            id: editorFontSelect
                            width: 220
                            editable: true
                            showToolTip: true
                            defaultPopupMaxHeight: 240
                            placeholderText: qsTr("选择字体")
                            // 编辑器字体改动即时生效（NoteEditor 绑定 settingsManager.editorFontFamily）
                            textRole: "label"
                            valueRole: "value"
                            model: root._systemFontsModel
                        }
                    }
                }

                // === 其他分组 ===
                Column {
                    width: parent.width - 48
                    spacing: 14

                    Text {
                        text: qsTr("其他")
                        font.pixelSize: 13; font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("自动保存间隔"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: autoSaveRadio
                            initCheckedIndex: 1
                            model: [
                                { label: "300ms", value: 0 },
                                { label: "500ms", value: 1 },
                                { label: "1s", value: 2 },
                                { label: "2s", value: 3 }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("默认主题"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: themeModeRadio
                            initCheckedIndex: 0
                            model: [
                                { label: qsTr("跟随系统"), value: 0 },
                                { label: qsTr("亮色"), value: 1 },
                                { label: qsTr("暗色"), value: 2 }
                            ]
                        }
                    }
                }

                // === 图片分组 ===
                Column {
                    width: parent.width - 48
                    spacing: 14

                    Text {
                        text: qsTr("图片")
                        font.pixelSize: 13; font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: qsTr("存储模式"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusRadioBlock {
                            id: imageModeRadio
                            initCheckedIndex: 0
                            model: [
                                { label: qsTr("本地"), value: 0 },
                                { label: "PicGo", value: 1 }
                            ]
                        }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        visible: imageModeRadio.currentCheckedValue === 1
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: "PicGo Host"; font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusInput { id: picgoHostInput; width: parent.width - 120 - 12; placeholderText: "127.0.0.1" }
                    }

                    Row {
                        width: parent.width
                        spacing: 12
                        visible: imageModeRadio.currentCheckedValue === 1
                        Text { width: 160; elide: Text.ElideRight; rightPadding: 8; anchors.verticalCenter: parent.verticalCenter; text: "PicGo Port"; font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                        HusInput { id: picgoPortInput; width: parent.width - 120 - 12; placeholderText: "36677" }
                    }
                }

                // === 数据分组 ===
                Column {
                    width: parent.width - 48
                    spacing: 14

                    Text {
                        text: qsTr("数据")
                        font.pixelSize: 13; font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Row {
                        width: parent.width
                        spacing: 12

                        Text {
                            width: parent.width - 180 - 12
                            anchors.verticalCenter: parent.verticalCenter
                            text: qsTr("备份/恢复全部数据（便签、分类、图片、设置），支持可选 AES-256 加密")
                            font.pixelSize: 12
                            color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                            wrapMode: Text.WordWrap
                        }

                        Rectangle {
                            width: 84; height: 32; radius: 6
                            color: backupBtnArea.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            Text { anchors.centerIn: parent; text: qsTr("备份数据"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textPrimary : "#202020" }
                            MouseArea { id: backupBtnArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: backupDialog.showDialog() }
                        }

                        Rectangle {
                            width: 84; height: 32; radius: 6
                            color: restoreBtnArea.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            Text { anchors.centerIn: parent; text: qsTr("恢复数据"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textPrimary : "#202020" }
                            MouseArea { id: restoreBtnArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: restoreDialog.showDialog() }
                        }
                    }
                }
            }
        }

        Rectangle { width: parent.width; height: 1; color: root.themeTokens ? root.themeTokens.divider : "#14A0897D" }

        // 底部按钮
        Item {
            width: parent.width
            height: 60

            Row {
                anchors.right: parent.right
                anchors.rightMargin: 20
                anchors.verticalCenter: parent.verticalCenter
                spacing: 10

                Rectangle {
                    width: 72; height: 34; radius: 6
                    color: cancelBtnArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                        : "transparent"
                    border.width: 1
                    border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                    Text { anchors.centerIn: parent; text: qsTr("取消"); font.pixelSize: 13; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                    MouseArea { id: cancelBtnArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: root.close() }
                }

                Rectangle {
                    width: 80; height: 34; radius: 6
                    color: okBtnArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                        : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")
                    Behavior on color { ColorAnimation { duration: 120 } }
                    Text { anchors.centerIn: parent; text: qsTr("保存"); font.pixelSize: 13; font.bold: true; color: "#FFFFFF" }
                    MouseArea { id: okBtnArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: root._applyAndClose() }
                }
            }
        }
    }

    BackupDialog {
        id: backupDialog
        themeTokens: root.themeTokens
    }

    RestoreDialog {
        id: restoreDialog
        themeTokens: root.themeTokens
    }
}
