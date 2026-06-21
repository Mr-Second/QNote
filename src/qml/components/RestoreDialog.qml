import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import HuskarUI.Basic

Popup {
    id: root
    width: 440
    height: 420
    parent: Overlay.overlay
    anchors.centerIn: parent
    modal: true
    dim: true
    focus: true
    padding: 0
    closePolicy: Popup.CloseOnEscape

    property var themeTokens: null
    property string selectedFile: ""
    property bool isEncrypted: false
    property string tempDir: ""
    property int step: 0  // 0=选文件, 1=输密码(仅加密), 2=显示冲突/选策略, 3=执行中
    property bool busy: false

    background: Rectangle {
        radius: 12
        color: root.themeTokens ? root.themeTokens.listPanelBg : "#FFFFFF"
        border.width: 1
        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
    }

    function showDialog() {
        selectedFile = ""
        isEncrypted = false
        tempDir = ""
        step = 0
        busy = false
        passwordInput.text = ""
        statusText.text = ""
        progressBar.visible = false
        progressText.text = ""
        nextBtn.enabled = true
        executeBtn.enabled = true
        open()
    }

    function _setBusy(b, msg) {
        busy = b
        if (b) {
            progressBar.visible = true
            progressBar.value = 0
            progressText.text = msg
            statusText.text = ""
        }
        nextBtn.enabled = !b
        executeBtn.enabled = !b
    }

    function _selectFile() { openFileDialog.open() }

    function _probeAndProceed() {
        if (selectedFile === "") {
            statusText.text = qsTr("请先选择备份文件")
            return
        }
        _setBusy(true, qsTr("正在读取备份..."))
        backupManager.probeBackupAsync(selectedFile)
    }

    function _extractAndAnalyze(pwd) {
        _setBusy(true, qsTr("正在解压备份..."))
        tempDir = backupManager.createTempRestoreDir()
        backupManager.extractBackupAsync(selectedFile, pwd, tempDir)
    }

    function _execute() {
        var strategy = strategyRadio.currentCheckedIndex
        _setBusy(true, qsTr("正在恢复..."))
        backupManager.applyRestoreAsync(strategy, tempDir)
    }

    Connections {
        target: backupManager
        function onProgress(percent, message) {
            progressBar.value = percent / 100.0
            if (message.length > 0) progressText.text = message
        }
        function onProbeFinished(result) {
            root._setBusy(false, "")
            if (!result.success) {
                statusText.text = qsTr("读取失败：%1").arg(result.error || qsTr("未知错误"))
                return
            }
            isEncrypted = result.encrypted
            if (isEncrypted) {
                step = 1
            } else {
                root._extractAndAnalyze("")
            }
        }
        function onExtractFinished(result) {
            if (!result.success) {
                root._setBusy(false, "")
                statusText.text = qsTr("解压失败：%1").arg(result.error || qsTr("未知错误"))
                if (result.detail) statusText.text += "\n" + result.detail
                return
            }
            progressText.text = qsTr("分析冲突...")
            progressBar.value = 0
            backupManager.analyzeConflictsAsync(tempDir)
        }
        function onAnalyzeFinished(result) {
            root._setBusy(false, "")
            if (!result.success) {
                statusText.text = qsTr("分析失败：%1").arg(result.error || qsTr("未知错误"))
                return
            }
            conflictCount.text = result.conflict
            newCount.text = result.new
            currentOnlyCount.text = result.currentOnly
            strategyRadio.currentCheckedIndex = (result.conflict === 0 && result.currentOnly === 0) ? 1 : 0
            step = 2
            statusText.text = ""
            progressBar.visible = false
            progressText.text = ""
        }
        function onRestoreFinished(result) {
            root._setBusy(false, "")
            if (!result.success) {
                statusText.text = qsTr("✗ 恢复失败：%1").arg(result.error || qsTr("未知错误"))
                if (result.detail) statusText.text += "\n" + result.detail
                return
            }
            // 刷新 UI
            noteController.loadNotes()
            categoryManager.loadCategories()

            statusText.text = qsTr("✓ 恢复成功")
            statusText.color = "#1A8A4A"
            progressBar.value = 1.0
            progressText.text = qsTr("完成")
            Qt.callLater(function() { root.close() })
        }
    }

    Column {
        anchors.fill: parent
        spacing: 0

        Item {
            width: parent.width
            height: 44

            Text {
                anchors.left: parent.left
                anchors.leftMargin: 18
                anchors.verticalCenter: parent.verticalCenter
                text: qsTr("恢复数据")
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
                MouseArea { id: closeMouse; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; enabled: !root.busy; onClicked: root.close() }
            }
        }

        Rectangle { width: parent.width; height: 1; color: root.themeTokens ? root.themeTokens.divider : "#14A0897D" }

        Flickable {
            width: parent.width
            height: parent.height - 44 - 1 - 60
            clip: true
            contentWidth: parent.width
            contentHeight: innerCol.implicitHeight
            flickableDirection: Flickable.VerticalFlick
            boundsBehavior: Flickable.StopAtBounds

            Column {
                id: innerCol
                width: parent.width
                spacing: 16
                leftPadding: 24
                rightPadding: 24
                topPadding: 20
                bottomPadding: 20

                Column {
                    width: parent.width - 48
                    visible: step === 0
                    spacing: 10

                    Text { text: qsTr("选择备份文件"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }

                    Row {
                        width: parent.width
                        spacing: 8

                        Rectangle {
                            width: parent.width - 90 - 8; height: 32; radius: 6
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            color: "transparent"
                            Text {
                                anchors.left: parent.left; anchors.leftMargin: 10
                                anchors.verticalCenter: parent.verticalCenter
                                text: root.selectedFile === "" ? qsTr("（点击右侧按钮选择 7z 文件）") : root.selectedFile
                                font.pixelSize: 12
                                color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                                elide: Text.ElideMiddle
                                width: parent.width - 20
                            }
                        }

                        Rectangle {
                            width: 90; height: 32; radius: 6
                            color: browseArea.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            Text { anchors.centerIn: parent; text: qsTr("浏览..."); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textPrimary : "#202020" }
                            MouseArea { id: browseArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: root._selectFile() }
                        }
                    }
                }

                Column {
                    width: parent.width - 48
                    visible: step === 1
                    spacing: 10

                    Text {
                        width: parent.width
                        text: qsTr("该备份已加密，请输入密码")
                        font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                        wrapMode: Text.WordWrap
                    }

                    HusInput {
                        id: passwordInput
                        width: parent.width
                        echoMode: TextInput.Password
                        placeholderText: qsTr("输入密码")
                    }
                }

                Column {
                    width: parent.width - 48
                    visible: step === 2
                    spacing: 14

                    Text { text: qsTr("冲突分析"); font.pixelSize: 13; font.bold: true; color: root.themeTokens ? root.themeTokens.textPrimary : "#202020" }

                    Row {
                        width: parent.width
                        spacing: 10

                        Rectangle {
                            width: (parent.width - 20) / 3; height: 60; radius: 6
                            border.width: 1; border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            color: "transparent"
                            Column {
                                anchors.centerIn: parent; spacing: 2
                                Text { id: conflictCount; anchors.horizontalCenter: parent.horizontalCenter; text: "0"; font.pixelSize: 20; font.bold: true; color: "#D97700" }
                                Text { anchors.horizontalCenter: parent.horizontalCenter; text: qsTr("冲突（同 uuid）"); font.pixelSize: 10; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                            }
                        }
                        Rectangle {
                            width: (parent.width - 20) / 3; height: 60; radius: 6
                            border.width: 1; border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            color: "transparent"
                            Column {
                                anchors.centerIn: parent; spacing: 2
                                Text { id: newCount; anchors.horizontalCenter: parent.horizontalCenter; text: "0"; font.pixelSize: 20; font.bold: true; color: "#1A8A4A" }
                                Text { anchors.horizontalCenter: parent.horizontalCenter; text: qsTr("新增"); font.pixelSize: 10; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                            }
                        }
                        Rectangle {
                            width: (parent.width - 20) / 3; height: 60; radius: 6
                            border.width: 1; border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                            color: "transparent"
                            Column {
                                anchors.centerIn: parent; spacing: 2
                                Text { id: currentOnlyCount; anchors.horizontalCenter: parent.horizontalCenter; text: "0"; font.pixelSize: 20; font.bold: true; color: "#5F5F5F" }
                                Text { anchors.horizontalCenter: parent.horizontalCenter; text: qsTr("当前独有（覆盖将丢失）"); font.pixelSize: 10; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                            }
                        }
                    }

                    Text { text: qsTr("选择恢复策略"); font.pixelSize: 13; font.bold: true; color: root.themeTokens ? root.themeTokens.textPrimary : "#202020" }

                    HusRadioBlock {
                        id: strategyRadio
                        initCheckedIndex: 0
                        model: [
                            { label: qsTr("覆盖"), value: 0 },
                            { label: qsTr("合并"), value: 1 },
                            { label: qsTr("仅导入新增"), value: 2 }
                        ]
                    }

                    Text {
                        width: parent.width
                        font.pixelSize: 11
                        wrapMode: Text.WordWrap
                        color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                        text: {
                            var idx = strategyRadio.currentCheckedIndex
                            if (idx === 0) return qsTr("⚠ 覆盖：当前数据将被完全替换为备份内容。恢复前会自动备份当前数据，但恢复后所有当前独有便签会丢失。")
                            if (idx === 1) return qsTr("合并：冲突便签保留较新版（按更新时间）；新增便签导入；分类按名称去重导入。当前独有便签保留。")
                            return qsTr("仅导入新增：只导入当前库中不存在的便签；所有冲突便签保留当前版本。")
                        }
                    }
                }

                // 进度条
                Column {
                    width: parent.width - 48
                    spacing: 4
                    visible: progressBar.visible

                    ProgressBar {
                        id: progressBar
                        width: parent.width
                        visible: false
                        value: 0
                    }
                    Text {
                        id: progressText
                        width: parent.width
                        font.pixelSize: 11
                        color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                        elide: Text.ElideRight
                    }
                }

                Text {
                    id: statusText
                    width: parent.width
                    font.pixelSize: 12
                    wrapMode: Text.WordWrap
                    color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                }
            }
        }

        Rectangle { width: parent.width; height: 1; color: root.themeTokens ? root.themeTokens.divider : "#14A0897D" }

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
                    color: cancelArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                        : "transparent"
                    border.width: 1
                    border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                    Text { anchors.centerIn: parent; text: qsTr("取消"); font.pixelSize: 13; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                    MouseArea { id: cancelArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; enabled: !root.busy; onClicked: root.close() }
                }

                Rectangle {
                    id: nextBtn
                    visible: step < 2
                    width: 80; height: 34; radius: 6
                    color: nextArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                        : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")
                    Behavior on color { ColorAnimation { duration: 120 } }
                    Text { anchors.centerIn: parent; text: root.busy ? qsTr("处理中") : qsTr("下一步"); font.pixelSize: 13; font.bold: true; color: "#FFFFFF" }
                    MouseArea {
                        id: nextArea
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        enabled: !root.busy
                        onClicked: {
                            if (step === 0) root._probeAndProceed()
                            else if (step === 1) root._extractAndAnalyze(passwordInput.text)
                        }
                    }
                }

                Rectangle {
                    id: executeBtn
                    visible: step === 2
                    width: 80; height: 34; radius: 6
                    color: execArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                        : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")
                    Behavior on color { ColorAnimation { duration: 120 } }
                    Text { anchors.centerIn: parent; text: root.busy ? qsTr("处理中") : qsTr("开始恢复"); font.pixelSize: 13; font.bold: true; color: "#FFFFFF" }
                    MouseArea { id: execArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; enabled: !root.busy; onClicked: root._execute() }
                }
            }
        }
    }

    FileDialog {
        id: openFileDialog
        fileMode: FileDialog.OpenFile
        title: qsTr("选择备份文件")
        nameFilters: [qsTr("QNote 备份 (*.7z)"), qsTr("所有文件 (*)")]
        onAccepted: {
            root.selectedFile = selectedFile.toString().replace("file:///", "")
            statusText.text = ""
        }
    }
}
