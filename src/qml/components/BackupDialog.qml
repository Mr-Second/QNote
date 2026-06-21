import QtQuick
import QtQuick.Controls
import QtQuick.Dialogs
import HuskarUI.Basic

Popup {
    id: root
    width: 440
    height: encryptSwitch.checked ? 530 : 380
    parent: Overlay.overlay
    anchors.centerIn: parent
    modal: true
    dim: true
    focus: true
    padding: 0
    closePolicy: Popup.CloseOnEscape

    property var themeTokens: null
    property string selectedPath: ""
    property bool busy: false

    background: Rectangle {
        radius: 12
        color: root.themeTokens ? root.themeTokens.listPanelBg : "#FFFFFF"
        border.width: 1
        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
    }

    function showDialog() {
        selectedPath = ""
        encryptSwitch.checked = false
        passwordInput.text = ""
        confirmInput.text = ""
        statusText.text = ""
        progressBar.visible = false
        progressText.text = ""
        busy = false
        startBtn.enabled = true
        open()
    }

    function _defaultFileName() {
        var ts = Qt.formatDateTime(new Date(), "yyyyMMdd-HHmmss")
        return "QNote-backup-" + ts + ".7z"
    }

    function _doBackup() {
        if (root.selectedPath === "") {
            statusText.text = qsTr("请先选择保存路径")
            statusText.color = "#D33"
            return
        }
        if (encryptSwitch.checked) {
            if (passwordInput.text.length === 0) {
                statusText.text = qsTr("请输入密码")
                statusText.color = "#D33"
                return
            }
            if (passwordInput.text !== confirmInput.text) {
                statusText.text = qsTr("两次输入的密码不一致")
                statusText.color = "#D33"
                return
            }
        }

        busy = true
        startBtn.enabled = false
        progressBar.visible = true
        progressBar.value = 0
        progressText.text = qsTr("准备中...")
        statusText.text = ""

        var pwd = encryptSwitch.checked ? passwordInput.text : ""
        backupManager.createBackupAsync(root.selectedPath, pwd)
    }

    Connections {
        target: backupManager
        function onProgress(percent, message) {
            progressBar.value = percent / 100.0
            if (message.length > 0) progressText.text = message
        }
        function onBackupFinished(result) {
            root.busy = false
            startBtn.enabled = true
            if (result.success) {
                progressBar.value = 1.0
                progressText.text = qsTr("完成")
                statusText.text = qsTr("✓ 备份成功")
                statusText.color = "#1A8A4A"
                Qt.callLater(function() { root.close() })
            } else {
                progressBar.visible = false
                statusText.text = qsTr("✗ 备份失败：") + (result.error || qsTr("未知错误"))
                if (result.detail) statusText.text += "\n" + result.detail
                statusText.color = "#D33"
            }
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
                text: qsTr("备份数据")
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

        Column {
            width: parent.width
            spacing: 16
            leftPadding: 24
            rightPadding: 24
            topPadding: 20
            bottomPadding: 20

            Column {
                width: parent.width - 48
                spacing: 8

                Text { text: qsTr("保存路径"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }

                Row {
                    width: parent.width
                    spacing: 8

                    Rectangle {
                        width: parent.width - 90 - 8
                        height: 32
                        radius: 6
                        border.width: 1
                        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"
                        color: "transparent"

                        Text {
                            anchors.left: parent.left
                            anchors.leftMargin: 10
                            anchors.verticalCenter: parent.verticalCenter
                            text: root.selectedPath === ""
                                  ? _defaultFileName() + qsTr("（点击右侧按钮选择目录）")
                                  : root.selectedPath
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
                        MouseArea { id: browseArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: saveFileDialog.open() }
                    }
                }
            }

            Row {
                width: parent.width - 48
                spacing: 10

                Text {
                    width: parent.width - 50
                    anchors.verticalCenter: parent.verticalCenter
                    text: qsTr("加密备份（AES-256）")
                    font.pixelSize: 12
                    color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                    wrapMode: Text.WordWrap
                }
                HusSwitch { id: encryptSwitch }
            }

            Text {
                width: parent.width - 48
                visible: encryptSwitch.checked
                text: qsTr("⚠ 密码无法找回，请妥善保管。加密后文件名也被加密，必须用 QNote 或 7-Zip + 密码才能查看内容。")
                font.pixelSize: 11
                color: "#B27500"
                wrapMode: Text.WordWrap
            }

            Column {
                width: parent.width - 48
                spacing: 10
                visible: encryptSwitch.checked

                Column {
                    width: parent.width
                    spacing: 4
                    Text { text: qsTr("密码"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                    HusInput { id: passwordInput; width: parent.width; echoMode: TextInput.Password; placeholderText: qsTr("输入密码") }
                }

                Column {
                    width: parent.width
                    spacing: 4
                    Text { text: qsTr("确认密码"); font.pixelSize: 12; color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F" }
                    HusInput { id: confirmInput; width: parent.width; echoMode: TextInput.Password; placeholderText: qsTr("再次输入密码") }
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
                width: parent.width - 48
                font.pixelSize: 12
                wrapMode: Text.WordWrap
                color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
            }
        }

        Item {
            width: parent.width
            height: 56

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
                    MouseArea { id: cancelArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: root.close() }
                }

                Rectangle {
                    id: startBtn
                    width: 80; height: 34; radius: 6
                    color: okArea.containsMouse
                        ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                        : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")
                    Behavior on color { ColorAnimation { duration: 120 } }
                    Text { anchors.centerIn: parent; text: root.busy ? qsTr("处理中") : qsTr("开始备份"); font.pixelSize: 13; font.bold: true; color: "#FFFFFF" }
                    MouseArea { id: okArea; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; enabled: !root.busy; onClicked: root._doBackup() }
                }
            }
        }
    }

    FileDialog {
        id: saveFileDialog
        fileMode: FileDialog.SaveFile
        defaultSuffix: "7z"
        title: qsTr("选择备份文件保存位置")
        onAccepted: { root.selectedPath = selectedFile.toString().replace("file:///", "") }
    }
}
