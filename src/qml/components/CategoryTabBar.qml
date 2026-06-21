import QtQuick
import QtQuick.Controls
import QtQuick.Window
import QtQuick.Dialogs
import HuskarUI.Basic

Item {
    id: root

    property var themeTokens
    property alias currentCategory: categoryListView.currentIndex
    readonly property bool _compact: themeTokens ? themeTokens.isCompact : false
    property int _editingIndex: -1  // 当前正在行内编辑的分类 index,-1 表示无
    property int _pendingCurrentIndex: -1  // model 重建后需恢复的 currentIndex
    property var categories: categoryManager ? categoryManager.categories : [
        { key: "all", name: qsTr("全部"), color: "#8B8B8B" },
        { key: "work", name: qsTr("工作"), color: "#1677b3" },
        { key: "life", name: qsTr("生活"), color: "#40a070" },
        { key: "important", name: qsTr("重要"), color: "#ed5126" }
    ]

    property string currentColor: categories.length > 0
        ? categories[Math.min(categoryListView.currentIndex, categories.length - 1)].color
        : "#8B8B8B"

    signal categorySelected(string key, string displayName)
    signal categoryDeleted(string key)
    signal categoryUpdated(string oldName, string newName)
    signal categoryRenameFailed(string attemptedName)

    // 查找分类对象(按 name)
    function _findCategory(name) {
        if (!categoryManager) return null
        var cats = categoryManager.categories
        for (var i = 0; i < cats.length; i++) {
            if (cats[i].name === name) return cats[i]
        }
        return null
    }

    // 提交分类(仅新建模式,修改走行内编辑)
    function _submitCategory() {
        var name = newCategoryModal.inputName.trim()
        if (name.length === 0) return
        categoryManager.addCategory(name, newCategoryModal.selectedColor, newCategoryModal.selectedIcon)
        _resetCategoryModal()
    }

    // 外部点击时提交正在行内编辑的分类(显式触发,不依赖焦点机制)
    function _commitEditingCategory() {
        if (root._editingIndex < 0) return
        var item = categoryListView.itemAtIndex(root._editingIndex)
        if (item && item.editing) {
            item._commitRename()
        }
    }

    function _resetCategoryModal() {
        newCategoryModal.inputName = ""
        newCategoryModal.selectedColor = "#126bae"
        newCategoryModal.selectedIcon = ""
        newCategoryModal.customIconPath = ""
        newCategoryModal.isEditing = false
        newCategoryModal.editingCategoryName = ""
        newCategoryModal.close()
    }

    function toHexColor(c) {
        return "#" + ("00" + Math.round(c.r * 255).toString(16)).slice(-2)
                   + ("00" + Math.round(c.g * 255).toString(16)).slice(-2)
                   + ("00" + Math.round(c.b * 255).toString(16)).slice(-2)
    }

    implicitWidth: 110
    implicitHeight: categoryListView.contentHeight


    property var _presetColors: [
        "#1677b3", "#40a070", "#ed5126", "#e6a23c",
        "#9b59b6", "#1abc9c", "#e74c3c", "#3498db",
        "#f39c12", "#2ecc71", "#e91e63", "#00bcd4"
    ]

    // 精选快速色：默认平铺展示，第 12 格留给调色盘触发器
    property var _quickColors: [
        "#eb261a", "#e16723", "#fca106", "#f9d367", "#1ba784", "#3c9566",
        "#126bae", "#4e7ca1", "#9b59b6", "#a35c8f", "#5c3719"
    ]

    // 预设分类图标:用 HusIcon 枚举名。独立窗口 8 列网格展示,共 56 个(7 行)
    // + 号上传入口在 GridView 上方独立按钮,grid 内全是图标(每行严格 8 个)
    property var _presetIcons: [
        // 工作类
        "AppstoreOutlined", "RadiusSettingOutlined", "HomeOutlined", "StarOutlined",
        "HeartOutlined", "FireOutlined", "BulbOutlined", "BellOutlined",
        // 文档/办公
        "BookOutlined", "CalendarOutlined", "CameraOutlined", "CarOutlined",
        "CoffeeOutlined", "ContainerOutlined", "EnvironmentOutlined", "FileOutlined",
        "FolderOutlined", "GiftOutlined", "MoonOutlined", "RestOutlined",
        // 生活/消费
        "ShopOutlined", "SoundOutlined", "ThunderboltOutlined", "TrophyOutlined",
        "ToolOutlined", "CloudOutlined", "ExperimentOutlined", "MoneyCollectOutlined",
        // 自然/符号
        "ClockCircleOutlined", "CodeOutlined", "DashboardOutlined", "DatabaseOutlined",
        "DesktopOutlined", "DollarOutlined", "DownloadOutlined", "EditOutlined",
        "EyeOutlined", "FilterOutlined", "GlobalOutlined", "Html5Outlined",
        // 更多
        "InfoCircleOutlined", "KeyOutlined", "LaptopOutlined", "LinkOutlined",
        "LockOutlined", "MailOutlined", "MessageOutlined", "MobileOutlined",
        "NotificationOutlined", "PayCircleOutlined", "PictureOutlined", "PoweroffOutlined",
        "PrinterOutlined", "ProfileOutlined", "ReadOutlined", "ReloadOutlined"
    ]

    HusModal {
        id: newCategoryModal
        width: 420
        closable: false
        confirmText: ''
        cancelText: ''
        colorOverlay: Qt.rgba(0, 0, 0, 0.35)

        property string selectedColor: "#126bae"
        property string selectedColorName: ""
        property string selectedIcon: ""  // 图标 key(预设枚举名 或 "custom:<file>")
        property string customIconPath: ""  // 用户上传的源文件路径(临时,确认时再 import)
        property bool isEditing: false  // true=修改分类 false=新建分类
        property string editingCategoryName: ""  // 修改时的原始分类名
        property string inputName: ""  // 名称输入框文本(桥接 contentDelegate 内部 id 到 root 作用域)

        // FileDialog:选择上传的图标文件
        FileDialog {
            id: iconFileDialog
            title: qsTr("选择图标")
            nameFilters: [qsTr("图片文件 (*.svg *.png *.jpg *.jpeg *.gif *.bmp *.ico *.webp)")]
            onAccepted: {
                var imported = iconManager.importIcon(Qt.resolvedUrl(iconFileDialog.currentFile).toString().replace("file:///", ""))
                if (imported.length > 0) {
                    newCategoryModal.selectedIcon = imported
                    newCategoryModal.customIconPath = imported
                }
            }
        }

        contentDelegate: Column {
            width: newCategoryModal.width
            spacing: 0

            // 内边距容器
            Column {
                width: parent.width
                leftPadding: 28
                rightPadding: 28
                topPadding: 20
                bottomPadding: 20
                spacing: 0

                // 顶部：标题（含实时色点预览，左）+ 关闭按钮（右）
                Item {
                    width: parent.width - 56
                    height: 28

                    Row {
                        anchors.left: parent.left
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 8

                        Rectangle {
                            width: 16
                            height: 16
                            radius: 8
                            color: newCategoryModal.selectedColor
                            anchors.verticalCenter: parent.verticalCenter

                            Behavior on color { ColorAnimation { duration: 150 } }
                        }

                        Text {
                            text: newCategoryModal.isEditing ? qsTr("修改分类") : qsTr("新建分类")
                            font.pixelSize: 16
                            font.bold: true
                            color: root.themeTokens ? root.themeTokens.titleText : "#1A1A1A"
                            anchors.verticalCenter: parent.verticalCenter
                        }
                    }

                    Rectangle {
                        anchors.right: parent.right
                        anchors.top: parent.top
                        width: 26
                        height: 26
                        radius: 13
                        color: modalCloseMouse.containsMouse
                            ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                            : "transparent"

                        Text {
                            anchors.centerIn: parent
                            text: "×"
                            font.pixelSize: 18
                            color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                        }

                        MouseArea {
                            id: modalCloseMouse
                            anchors.fill: parent
                            hoverEnabled: true
                            cursorShape: Qt.PointingHandCursor
                            onClicked: root._resetCategoryModal()
                        }
                    }
                }

                Item { width: parent.width; height: 16 }

                // 名称
                Column {
                    width: parent.width - 56
                    spacing: 8

                    Text {
                        text: qsTr("名称")
                        font.pixelSize: 12
                        font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    HusInput {
                        id: categoryNameInput
                        width: parent.width
                        placeholderText: qsTr("输入分类名称")
                        text: newCategoryModal.inputName
                        onTextEdited: newCategoryModal.inputName = text
                        onAccepted: root._submitCategory()
                    }
                }

                Item { width: parent.width; height: 14 }

                // 颜色：11 个精选色块 + 第 12 格调色盘触发器
                Column {
                    width: parent.width - 56
                    spacing: 8

                    Text {
                        text: qsTr("颜色")
                        font.pixelSize: 12
                        font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    Grid {
                        id: quickColorGrid
                        columns: 6
                        spacing: 8
                        width: parent.width

                        Repeater {
                            model: root._quickColors

                                Rectangle {
                                    width: (quickColorGrid.width - 8 * 5) / 6
                                    height: width
                                    radius: 6
                                    color: modelData
                                    border.width: newCategoryModal.selectedColor.toLowerCase() === modelData.toLowerCase() ? 3 : 0
                                    border.color: root.themeTokens ? root.themeTokens.titleText : "#202020"
                                    scale: newCategoryModal.selectedColor.toLowerCase() === modelData.toLowerCase()
                                        ? 1.08
                                        : (quickColorMouse.containsMouse ? 1.1 : 1.0)

                                Behavior on scale { NumberAnimation { duration: 120; easing.type: Easing.OutQuad } }

                                MouseArea {
                                    id: quickColorMouse
                                    anchors.fill: parent
                                    hoverEnabled: true
                                    cursorShape: Qt.PointingHandCursor
                                    onClicked: newCategoryModal.selectedColor = modelData
                                }
                            }
                        }

                        // 第 12 格：调色盘触发器（自定义色选中时显示选中态）
                        Item {
                            width: (quickColorGrid.width - 8 * 5) / 6
                            height: width

                            property bool isCustomColor: {
                                var sel = newCategoryModal.selectedColor.toLowerCase()
                                for (var i = 0; i < root._quickColors.length; i++) {
                                    if (root._quickColors[i].toLowerCase() === sel) return false
                                }
                                return true
                            }

                            Rectangle {
                                anchors.fill: parent
                                radius: 6
                                gradient: Gradient {
                                    orientation: Gradient.Horizontal
                                    GradientStop { position: 0.0; color: "#eb261a" }
                                    GradientStop { position: 0.2; color: "#fca106" }
                                    GradientStop { position: 0.4; color: "#1ba784" }
                                    GradientStop { position: 0.6; color: "#126bae" }
                                    GradientStop { position: 0.8; color: "#9b59b6" }
                                    GradientStop { position: 1.0; color: "#5c3719" }
                                }
                                border.width: parent.isCustomColor ? 3 : 1
                                border.color: parent.isCustomColor
                                    ? (root.themeTokens ? root.themeTokens.titleText : "#202020")
                                    : (root.themeTokens ? root.themeTokens.divider : "#E0E0E0")
                                scale: parent.isCustomColor ? 1.08 : (customColorTriggerMouse.containsMouse ? 1.1 : 1.0)

                                Behavior on scale { NumberAnimation { duration: 120; easing.type: Easing.OutQuad } }

                                // 调色盘图标
                                Text {
                                    anchors.centerIn: parent
                                    text: "🎨"
                                    font.pixelSize: Math.min(parent.width * 0.5, 14)
                                }

                                MouseArea {
                                    id: customColorTriggerMouse
                                    anchors.fill: parent
                                    hoverEnabled: true
                                    cursorShape: Qt.PointingHandCursor
                                    onClicked: customColorWindow.show()
                                }
                            }
                        }
                    }
                }

                Item { width: parent.width; height: 14 }

                // 预览区:复用 CategoryTabButton 真实样式(selected=true)
                Column {
                    width: parent.width - 56
                    spacing: 8

                    Text {
                        text: qsTr("预览")
                        font.pixelSize: 12
                        font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                    }

                    CategoryTabButton {
                        width: parent.width
                        height: 46
                        text: {
                            var n = newCategoryModal.inputName.trim()
                            return n.length > 0 ? n : qsTr("分类名称")
                        }
                        tabColor: newCategoryModal.selectedColor
                        iconSource: {
                            var key = newCategoryModal.selectedIcon
                            if (key.length === 0) return themeTokens ? themeTokens.categoryIcon("", 3) : HusIcon.StarOutlined
                            if (key.startsWith("custom:")) {
                                return iconManager ? iconManager.resolveIconUrl(key) : 0
                            }
                            return HusIcon[key] !== undefined ? HusIcon[key] : HusIcon.StarOutlined
                        }
                        selected: false
                        themeTokens: root.themeTokens
                        // 预览用,不可点击
                        hoverEnabled: false
                        cursorShape: Qt.ArrowCursor
                    }
                }

                Item { width: parent.width; height: 14 }

                // 图标分区:右侧 ">" 按钮弹独立窗口
                Row {
                    width: parent.width - 56
                    spacing: 8

                    Text {
                        text: qsTr("图标")
                        font.pixelSize: 12
                        font.bold: true
                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                        anchors.verticalCenter: parent.verticalCenter
                    }

                    Item {
                        width: 24
                        height: 24
                        anchors.verticalCenter: parent.verticalCenter

                        Rectangle {
                            anchors.fill: parent
                            radius: 4
                            color: iconPickMouse.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                : "transparent"

                            HusIconText {
                                anchors.centerIn: parent
                                iconSource: HusIcon.RightOutlined
                                iconSize: 14
                                colorIcon: root.themeTokens ? root.themeTokens.accentBase : "#0078D4"
                            }

                            MouseArea {
                                id: iconPickMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: iconPickerWindow.show()
                            }
                        }
                    }
                }

                Item { width: parent.width; height: 4 }

                // 图标选择独立窗口:脱离 HusModal,滚轮原生支持
                Window {
                    id: iconPickerWindow
                    width: 360
                    height: 420
                    flags: Qt.Dialog | Qt.FramelessWindowHint
                    color: "transparent"
                    modality: Qt.NonModal
                    visible: false

                    // 主弹窗关闭时,连带隐藏图标选择窗口
                    Connections {
                        target: newCategoryModal
                        function onOpenedChanged() {
                            if (!newCategoryModal.opened) iconPickerWindow.hide()
                        }
                    }

                    Rectangle {
                        anchors.fill: parent
                        radius: 12
                        color: root.themeTokens ? root.themeTokens.listPanelBg : "#FFFFFF"
                        border.width: 1
                        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"

                        Column {
                            anchors.fill: parent
                            spacing: 0

                            // 标题栏(可拖动)+ 右侧上传入口
                            Item {
                                width: parent.width
                                height: 40

                                Rectangle {
                                    anchors.fill: parent
                                    color: "transparent"

                                    Text {
                                        anchors.left: parent.left
                                        anchors.leftMargin: 16
                                        anchors.verticalCenter: parent.verticalCenter
                                        text: qsTr("选择图标")
                                        font.pixelSize: 13
                                        font.bold: true
                                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                                    }

                                    // 右侧"上传自定义"按钮
                                    Rectangle {
                                        anchors.right: parent.right
                                        anchors.rightMargin: 12
                                        anchors.verticalCenter: parent.verticalCenter
                                        width: 84
                                        height: 24
                                        radius: 4
                                        color: uploadIconBtnMouse.containsMouse
                                            ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.08))
                                            : (root.themeTokens
                                                ? (root.darkTheme ? Qt.rgba(1, 1, 1, 0.04) : Qt.rgba(0, 0, 0, 0.03))
                                                : Qt.rgba(0, 0, 0, 0.03))
                                        border.width: newCategoryModal.selectedIcon.startsWith("custom:") ? 1 : 0
                                        border.color: root.themeTokens ? root.themeTokens.titleText : "#202020"

                                        Row {
                                            anchors.centerIn: parent
                                            spacing: 4

                                            HusIconText {
                                                iconSource: HusIcon.PlusOutlined
                                                iconSize: 12
                                                colorIcon: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                                                anchors.verticalCenter: parent.verticalCenter
                                            }

                                            Text {
                                                text: qsTr("上传")
                                                font.pixelSize: 11
                                                color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                                                anchors.verticalCenter: parent.verticalCenter
                                            }
                                        }

                                        MouseArea {
                                            id: uploadIconBtnMouse
                                            anchors.fill: parent
                                            hoverEnabled: true
                                            cursorShape: Qt.PointingHandCursor
                                            onClicked: iconFileDialog.open()
                                        }
                                    }

                                    DragHandler {
                                        target: null
                                        onActiveChanged: if (active) iconPickerWindow.startSystemMove()
                                    }
                                }
                            }

                            GridView {
                                id: iconGrid
                                width: parent.width - 16
                                height: parent.height - 40 - 56 - 16
                                anchors.horizontalCenter: parent.horizontalCenter
                                clip: true
                                cellWidth: Math.floor(width / 8)
                                cellHeight: cellWidth
                                topMargin: 4
                                bottomMargin: 4
                                leftMargin: 4
                                rightMargin: 4
                                boundsBehavior: Flickable.StopAtBounds
                                // grid 内全是预设图标(上传入口在顶部按钮)
                                model: root._presetIcons

                                delegate: Rectangle {
                                    width: iconGrid.cellWidth - 4
                                    height: width
                                    radius: 5
                                    property string iconKey: modelData
                                    property bool isSelected: newCategoryModal.selectedIcon === iconKey

                                    color: iconCellMouse.containsMouse
                                        ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                        : "transparent"
                                    border.width: isSelected ? 1 : 0
                                    border.color: root.themeTokens ? root.themeTokens.titleText : "#202020"
                                    scale: isSelected ? 1.08 : (iconCellMouse.containsMouse ? 1.1 : 1.0)
                                    Behavior on scale { NumberAnimation { duration: 120; easing.type: Easing.OutQuad } }

                                    HusIconText {
                                        anchors.centerIn: parent
                                        iconSource: HusIcon[iconKey] !== undefined ? HusIcon[iconKey] : 0
                                        iconSize: Math.min(parent.width * 0.55, 16)
                                        colorIcon: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                                    }

                                    MouseArea {
                                        id: iconCellMouse
                                        anchors.fill: parent
                                        hoverEnabled: true
                                        cursorShape: Qt.PointingHandCursor
                                        onClicked: newCategoryModal.selectedIcon = iconKey
                                    }
                                }

                                ScrollBar.vertical: HusScrollBar { }
                            }

                            // 底部确定按钮
                            Item {
                                width: parent.width
                                height: 48

                                Rectangle {
                                    anchors.right: parent.right
                                    anchors.rightMargin: 16
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: 72
                                    height: 32
                                    radius: 6
                                    color: iconPickerConfirmMouse.containsMouse
                                        ? (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")
                                        : (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.08))

                                    Text {
                                        anchors.centerIn: parent
                                        text: qsTr("确定")
                                        font.pixelSize: 13
                                        color: root.themeTokens ? root.themeTokens.titleText : "#202020"
                                    }

                                    MouseArea {
                                        id: iconPickerConfirmMouse
                                        anchors.fill: parent
                                        hoverEnabled: true
                                        cursorShape: Qt.PointingHandCursor
                                        onClicked: iconPickerWindow.close()
                                    }
                                }
                            }
                        }
                    }

                    onVisibleChanged: {
                        if (visible) {
                            var mainWin = root.Window.window
                            if (mainWin) {
                                iconPickerWindow.x = mainWin.x + newCategoryModal.x + newCategoryModal.width + 8
                                iconPickerWindow.y = mainWin.y + newCategoryModal.y
                            }
                        }
                    }
                }
                // 取色器独立窗口：真正的 OS 级窗口，不受主窗口尺寸/裁剪限制
                Window {
                    id: customColorWindow
                                    width: 360
                                    height: 420
                                    flags: Qt.Dialog | Qt.FramelessWindowHint
                                    color: "transparent"
                                    modality: Qt.NonModal
                                    visible: false

                                    // 独立窗口内容
                                    Rectangle {
                                        anchors.fill: parent
                                        radius: 12
                                        color: root.themeTokens ? root.themeTokens.listPanelBg : "#FFFFFF"
                                        border.width: 1
                                        border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"

                                        Column {
                                            anchors.fill: parent
                                            spacing: 0

                                            // 标题栏（可拖动）
                                            Item {
                                                width: parent.width
                                                height: 40

                                                Rectangle {
                                                    anchors.fill: parent
                                                    color: "transparent"

                                                    Text {
                                                        anchors.left: parent.left
                                                        anchors.leftMargin: 16
                                                        anchors.verticalCenter: parent.verticalCenter
                                                        text: qsTr("选择颜色")
                                                        font.pixelSize: 13
                                                        font.bold: true
                                                        color: root.themeTokens ? root.themeTokens.textPrimary : "#202020"
                                                    }

                                                    // 拖动区域:Window 不是 Item,不能用 DragHandler.target 移动,
                                                    // 改 target:null + onActiveChanged 调 startSystemMove 让 OS 接管
                                                    DragHandler {
                                                        target: null
                                                        onActiveChanged: if (active) customColorWindow.startSystemMove()
                                                    }
                                                }
                                            }

                            HusColorPickerPanel {
                                width: parent.width
                                height: parent.height - 40 - 48
                                defaultValue: newCategoryModal.selectedColor
                                alphaEnabled: false
                                onChange: function(color) {
                                    newCategoryModal.selectedColor = root.toHexColor(color)
                                }
                            }

                            // 底部确定按钮
                            Item {
                                width: parent.width
                                height: 48

                                Rectangle {
                                    anchors.right: parent.right
                                    anchors.rightMargin: 16
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: 72
                                    height: 32
                                    radius: 6
                                    color: colorPickerConfirmMouse.containsMouse
                                        ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                                        : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")

                                    Behavior on color { ColorAnimation { duration: 120 } }

                                    Text {
                                        anchors.centerIn: parent
                                        text: qsTr("确定")
                                        font.pixelSize: 13
                                        font.bold: true
                                        color: "#FFFFFF"
                                    }

                                    MouseArea {
                                        id: colorPickerConfirmMouse
                                        anchors.fill: parent
                                        hoverEnabled: true
                                        cursorShape: Qt.PointingHandCursor
                                        onClicked: customColorWindow.close()
                                    }
                                }
                            }
                        }
                    }

                    // 定位：基于主窗口在屏幕上的位置，弹在右侧
                    function reposition() {
                        var mainWin = root.Window.window
                        if (mainWin) {
                            customColorWindow.x = mainWin.x + newCategoryModal.x + newCategoryModal.width + 8
                            customColorWindow.y = mainWin.y + newCategoryModal.y
                        }
                    }

                    onVisibleChanged: {
                        if (visible) reposition()
                    }

                    // 主弹窗关闭时,连带隐藏取色器独立窗口(避免悬空)
                    // 用 Connections 监听 Popup.opened 属性变化(比 closed 信号可靠)
                    Connections {
                        target: newCategoryModal
                        function onOpenedChanged() {
                            if (!newCategoryModal.opened) customColorWindow.hide()
                        }
                    }
                }

                Item { width: parent.width; height: 16 }

                // 操作按钮
                Item {
                    width: parent.width - 56
                    height: 34

                    Row {
                        anchors.right: parent.right
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 10

                        Rectangle {
                            width: 64
                            height: 34
                            radius: 6
                            color: cancelBtnMouse.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHoverBg : Qt.rgba(0, 0.47, 0.83, 0.05))
                                : "transparent"
                            border.width: 1
                            border.color: root.themeTokens ? root.themeTokens.divider : "#E0E0E0"

                            Text {
                                anchors.centerIn: parent
                                text: qsTr("取消")
                                font.pixelSize: 13
                                color: root.themeTokens ? root.themeTokens.textSecondary : "#5F5F5F"
                            }

                            MouseArea {
                                id: cancelBtnMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: root._resetCategoryModal()
                            }
                        }

                        Rectangle {
                            width: 80
                            height: 34
                            radius: 6
                            color: createBtnMouse.containsMouse
                                ? (root.themeTokens ? root.themeTokens.accentHover : "#1A86D9")
                                : (root.themeTokens ? root.themeTokens.accentBase : "#0078D4")

                            Behavior on color { ColorAnimation { duration: 120 } }

                            Text {
                                anchors.centerIn: parent
                                text: newCategoryModal.isEditing ? qsTr("保存修改") : qsTr("创建分类")
                                font.pixelSize: 13
                                font.bold: true
                                color: "#FFFFFF"
                            }

                            MouseArea {
                                id: createBtnMouse
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: root._submitCategory()
                            }
                        }
                    }
                }
            }
        }
    }

    Column {
        anchors.fill: parent
        spacing: 12

        Item {
            id: topHeader
            width: parent.width
            height: root._compact ? 32 : 52

            // compact 模式：仅显示缩写 "Q" 居中
            Text {
                anchors.centerIn: parent
                text: "Q"
                font.pixelSize: 16
                font.bold: true
                color: themeTokens ? themeTokens.titleText : "#4B4038"
                visible: root._compact
            }

            // normal 模式：QNote + 便签整理 双行
            Column {
                anchors.left: parent.left
                anchors.leftMargin: 6
                anchors.verticalCenter: parent.verticalCenter
                spacing: 4
                visible: !root._compact

                Text {
                    text: "QNote"
                    font.pixelSize: 16
                    font.bold: true
                    color: themeTokens ? themeTokens.titleText : "#4B4038"
                }

                Text {
                    text: qsTr("便签整理")
                    font.pixelSize: 11
                    color: themeTokens ? themeTokens.textMuted : "#9A8C7D"
                }
            }
        }

        ListView {
            id: categoryListView
            width: parent.width
            height: parent.height - topHeader.height - footerActions.height - parent.spacing * 2
            spacing: 8
            model: root.categories
            highlightMoveDuration: 180
            highlightResizeDuration: 0
            boundsBehavior: Flickable.StopAtBounds
            onModelChanged: {
                // model 重建会重置 currentIndex 为 0,若有 pending 则恢复
                if (root._pendingCurrentIndex >= 0) {
                    var idx = root._pendingCurrentIndex
                    root._pendingCurrentIndex = -1
                    categoryListView.currentIndex = idx
                }
            }

            delegate: CategoryTabButton {
                width: root.width
                text: modelData.name
                tabColor: modelData.color
                selected: ListView.isCurrentItem
                themeTokens: root.themeTokens
                iconSource: themeTokens ? themeTokens.categoryIcon(modelData.icon || "", index) : HusIcon.StarOutlined
                // "全部"分类(index=0)不可删除/改名
                deletable: index > 0
                // 进入编辑时记录 index,退出时清除(供根 TapHandler 判断点击外部提交)
                onEditingChanged: {
                    root._editingIndex = editing ? index : (root._editingIndex === index ? -1 : root._editingIndex)
                }
                // 左键选中分类(右键由 CategoryTabButton 内部处理弹出菜单)
                onLeftClicked: {
                    if (!editing) {
                        categoryListView.currentIndex = index
                        var key = modelData.key || modelData.name
                        root.categorySelected(key, modelData.name)
                    }
                }
                onDeleteClicked: {
                    var key = modelData.key || modelData.name
                    root.categoryDeleted(key)
                }
                onRenameSubmitted: function(newName) {
                    // 改名触发 categoriesChanged 同步重建 model,会重置 currentIndex
                    // 必须在 updateCategory 调用前设 pending(onModelChanged 在其内部同步触发)
                    root._pendingCurrentIndex = categoryListView.currentIndex
                    var ok = categoryManager.updateCategory(modelData.name, newName, modelData.color, modelData.icon || "")
                    if (ok) {
                        root.categoryUpdated(modelData.name, newName)
                    } else {
                        root.categoryRenameFailed(newName)
                    }
                }
            }
        }

        Item {
            id: footerActions
            width: parent.width
            height: root._compact ? (root.themeTokens ? root.themeTokens.sidebarFooterHeight : 56) : 76

            Column {
                anchors.fill: parent
                spacing: root._compact ? 4 : 8

                Rectangle {
                    width: parent.width
                    height: 1
                    color: themeTokens ? themeTokens.divider : "#18A39283"
                }

                Item {
                    width: parent.width
                    height: root._compact ? 24 : 30

                    Rectangle {
                        anchors.fill: parent
                        radius: 8
                        color: newCatMouse.containsMouse
                            ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
                            : "transparent"
                    }

                    Row {
                        anchors.left: parent.left
                        anchors.leftMargin: 8
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 10

                        HusIconText {
                            anchors.verticalCenter: parent.verticalCenter
                            iconSource: HusIcon.PlusOutlined
                            iconSize: 14
                            colorIcon: newCatMouse.containsMouse
                                ? (themeTokens ? themeTokens.accentBase : "#0078D4")
                                : (themeTokens ? themeTokens.iconMuted : "#8E7E71")
                        }

                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            text: qsTr("新建分类")
                            font.pixelSize: 12
                            color: newCatMouse.containsMouse
                                ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
                                : (themeTokens ? themeTokens.sidebarMutedText : "#5F5F5F")
                            visible: !root._compact
                        }
                    }

                    MouseArea {
                        id: newCatMouse
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            // 确保从"新建"模式打开(reset editing 状态)
                            newCategoryModal.isEditing = false
                            newCategoryModal.editingCategoryName = ""
                            newCategoryModal.open()
                        }

                        HusToolTip {
                            visible: root._compact && newCatMouse.containsMouse
                            text: qsTr("新建分类")
                            position: HusToolTip.Position_Right
                        }
                    }
                }

                Item {
                    width: parent.width
                    height: root._compact ? 24 : 30

                    Rectangle {
                        anchors.fill: parent
                        radius: 8
                        color: settingsMouse.containsMouse
                            ? (themeTokens ? themeTokens.accentHoverBg : Qt.rgba(0.0, 0.47, 0.83, 0.05))
                            : "transparent"
                    }

                    Row {
                        anchors.left: parent.left
                        anchors.leftMargin: 8
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 10

                        HusIconText {
                            anchors.verticalCenter: parent.verticalCenter
                            iconSource: HusIcon.SettingOutlined
                            iconSize: 14
                            colorIcon: settingsMouse.containsMouse
                                ? (themeTokens ? themeTokens.accentBase : "#0078D4")
                                : (themeTokens ? themeTokens.iconMuted : "#8E7E71")
                        }

                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            text: qsTr("设置")
                            font.pixelSize: 12
                            color: settingsMouse.containsMouse
                                ? (themeTokens ? themeTokens.titleText : "#1A1A1A")
                                : (themeTokens ? themeTokens.sidebarMutedText : "#5F5F5F")
                            visible: !root._compact
                        }
                    }

                    MouseArea {
                        id: settingsMouse
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            settingsDialog.showDialog()
                        }

                        HusToolTip {
                            visible: root._compact && settingsMouse.containsMouse
                            text: qsTr("设置")
                            position: HusToolTip.Position_Right
                        }
                    }
                }
            }
        }
    }
}
