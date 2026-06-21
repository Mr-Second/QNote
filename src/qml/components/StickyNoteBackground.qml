import QtQuick
import HuskarUI.Basic

Item {
    id: root

    property alias contentItem: noteEditor
    property bool showContent: true
    property string editorText: ""
    property color noteColor: "#FFF9C4"
    property color accentColor: noteColor
    property var themeTokens
    property bool darkTheme: themeTokens ? themeTokens.isDark : false
    property string noteTime: ""
    property string noteCategory: ""
    property string noteTitle: ""
    property bool titleManual: false
    signal textEdited(string text)
    signal deleteNoteRequested()
    signal titleEdited(string title)

    function mixChannel(base, target, amount) {
        return base * (1 - amount) + target * amount
    }

    onAccentColorChanged: paperBg.requestPaint()
    onDarkThemeChanged: {
        paperBg.requestPaint()
        emptyIcon.requestPaint()
    }
    onWidthChanged: paperBg.requestPaint()
    onHeightChanged: paperBg.requestPaint()

    Canvas {
        id: paperBg
        anchors.fill: parent
        z: 0

        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()

            var w = width
            var h = height
            if (w <= 0 || h <= 0)
                return

            var cr = 12

            var baseR = root.accentColor.r
            var baseG = root.accentColor.g
            var baseB = root.accentColor.b

            var paperTarget = root.themeTokens ? root.themeTokens.paperBase : Qt.rgba(1, 1, 1, 1)
            var warmTarget = root.themeTokens ? root.themeTokens.paperWarm : Qt.rgba(0.96, 0.94, 0.90, 1)
            var foldTarget = root.themeTokens ? root.themeTokens.paperFoldTarget : Qt.rgba(0.84, 0.82, 0.78, 1)
            var paperMix = root.themeTokens ? root.themeTokens.paperMix : 0.86
            var warmMix = root.themeTokens ? root.themeTokens.paperWarmMix : 0.78
            var paperR = root.mixChannel(baseR, paperTarget.r, paperMix)
            var paperG = root.mixChannel(baseG, paperTarget.g, paperMix)
            var paperB = root.mixChannel(baseB, paperTarget.b, paperMix)
            var warmR = root.mixChannel(baseR, warmTarget.r, warmMix)
            var warmG = root.mixChannel(baseG, warmTarget.g, warmMix)
            var warmB = root.mixChannel(baseB, warmTarget.b, warmMix)

            var grad = ctx.createLinearGradient(0, 0, w, h)
            grad.addColorStop(0.0, Qt.rgba(root.mixChannel(paperR, paperTarget.r, root.darkTheme ? 0.08 : 0.03), root.mixChannel(paperG, paperTarget.g, root.darkTheme ? 0.08 : 0.03), root.mixChannel(paperB, paperTarget.b, root.darkTheme ? 0.08 : 0.03), 1))
            grad.addColorStop(0.58, Qt.rgba(paperR, paperG, paperB, 1))
            grad.addColorStop(1.0, Qt.rgba(warmR, warmG, warmB, 1))

            ctx.beginPath()
            ctx.moveTo(cr, 0)
            ctx.lineTo(w - cr, 0)
            ctx.arcTo(w, 0, w, cr, cr)
            ctx.lineTo(w, h - cr)
            ctx.arcTo(w, h, w - cr, h, cr)
            ctx.lineTo(cr, h)
            ctx.arcTo(0, h, 0, h - cr, cr)
            ctx.lineTo(0, cr)
            ctx.arcTo(0, 0, cr, 0, cr)
            ctx.closePath()
            ctx.fillStyle = grad
            ctx.fill()

            var highlight = ctx.createLinearGradient(0, 0, 0, h * 0.32)
            highlight.addColorStop(0.0, root.themeTokens ? root.themeTokens.paperHighlightTop : Qt.rgba(1, 1, 1, 0.24))
            highlight.addColorStop(1.0, root.themeTokens ? root.themeTokens.paperHighlightBottom : Qt.rgba(1, 1, 1, 0.0))
            ctx.fillStyle = highlight
            ctx.fill()

            ctx.strokeStyle = root.themeTokens ? root.themeTokens.paperStroke : Qt.rgba(0.35, 0.28, 0.22, 0.1)
            ctx.lineWidth = 1
            ctx.stroke()

            ctx.beginPath()
            ctx.moveTo(cr + 10, 4)
            ctx.lineTo(w - cr - 6, 4)
            ctx.strokeStyle = root.themeTokens ? root.themeTokens.paperTopLine : Qt.rgba(1, 1, 1, 0.46)
            ctx.lineWidth = 1
            ctx.stroke()

            // 边缘高光：沿纸张左/上边缘补一层亮线，让纸张边缘更明确
            ctx.save()
            ctx.beginPath()
            ctx.moveTo(cr * 0.5, 1)
            ctx.lineTo(w - cr - 2, 1)
            ctx.strokeStyle = root.themeTokens ? root.themeTokens.paperEdgeHighlight : Qt.rgba(1, 1, 1, 0.42)
            ctx.lineWidth = 0.5
            ctx.stroke()
            ctx.beginPath()
            ctx.moveTo(1, cr * 0.5)
            ctx.lineTo(1, h - cr * 0.5)
            ctx.strokeStyle = root.themeTokens ? root.themeTokens.paperEdgeHighlight : Qt.rgba(1, 1, 1, 0.42)
            ctx.lineWidth = 0.5
            ctx.stroke()
            ctx.restore()

            ctx.save()
            ctx.globalAlpha = root.themeTokens ? root.themeTokens.paperRuleAlpha : 0.028
            ctx.strokeStyle = root.themeTokens ? root.themeTokens.paperRuleColor : "#6E6054"
            ctx.lineWidth = 0.5
            var lineSpacing = 30
            var startY = 74
            while (startY < h - 24) {
                ctx.beginPath()
                ctx.moveTo(24, startY)
                ctx.lineTo(w - 28, startY)
                ctx.stroke()
                startY += lineSpacing
            }
            ctx.restore()

            ctx.save()
            var dustBright = root.themeTokens ? root.themeTokens.paperDustBright : Qt.rgba(1, 1, 1, 1)
            var dustDark = root.themeTokens ? root.themeTokens.paperDustDark : Qt.rgba(0.72, 0.62, 0.54, 1)
            for (var i = 0; i < 170; ++i) {
                var px = ((i * 53) % Math.max(1, w - 40)) + 20
                var py = ((i * 97) % Math.max(1, h - 48)) + 16
                var size = 0.6 + ((i * 17) % 4) * 0.35
                var alpha = (root.themeTokens ? root.themeTokens.paperDustBrightAlpha : 0.012) + (((i * 29) % 8) * 0.002)
                if (i % 2 === 0) {
                    ctx.fillStyle = Qt.rgba(dustBright.r, dustBright.g, dustBright.b, alpha)
                } else {
                    ctx.fillStyle = Qt.rgba(dustDark.r, dustDark.g, dustDark.b, alpha * (root.darkTheme ? 0.9 : 0.6))
                }
                ctx.fillRect(px, py, size, size)
            }
            ctx.restore()
        }

        Component.onCompleted: requestPaint()
    }

    NoteEditor {
        id: noteEditor
        anchors.fill: parent
        anchors.leftMargin: 26
        anchors.topMargin: 24
        anchors.rightMargin: 30
        anchors.bottomMargin: 28
        visible: root.showContent
        noteTime: root.noteTime
        noteCategory: root.noteCategory
        noteTitle: root.noteTitle
        titleManual: root.titleManual
        accentColor: root.accentColor
        themeTokens: root.themeTokens
        onTextEdited: function(t) { root.textEdited(t) }
        onDeleteNoteRequested: root.deleteNoteRequested()
        onTitleEdited: function(title) { root.titleEdited(title) }
    }

    Item {
        anchors.fill: parent
        visible: !root.showContent

        Canvas {
            id: emptyIcon
            anchors.centerIn: parent
            anchors.verticalCenterOffset: -20
            width: 64
            height: 64

            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()

                ctx.strokeStyle = root.themeTokens ? root.themeTokens.emptyStateStroke : "#D5D5D5"
                ctx.lineWidth = 2
                ctx.fillStyle = root.themeTokens ? root.themeTokens.emptyStateFill : "#FAFAFA"

                var w = width
                var h = height
                var fold = 14

                ctx.beginPath()
                ctx.moveTo(8, 4)
                ctx.lineTo(w - fold, 4)
                ctx.lineTo(w - 4, fold)
                ctx.lineTo(w - 4, h - 4)
                ctx.lineTo(8, h - 4)
                ctx.closePath()
                ctx.fill()
                ctx.stroke()

                ctx.beginPath()
                ctx.moveTo(w - fold, 4)
                ctx.lineTo(w - fold, fold)
                ctx.lineTo(w - 4, fold)
                ctx.closePath()
                ctx.fillStyle = root.themeTokens ? root.themeTokens.emptyStateFold : "#EBEBEB"
                ctx.fill()
                ctx.stroke()

                ctx.strokeStyle = root.themeTokens ? root.themeTokens.emptyStateRule : "#E0E0E0"
                ctx.lineWidth = 1
                for (var i = 0; i < 3; i++) {
                    ctx.beginPath()
                    ctx.moveTo(16, 24 + i * 10)
                    ctx.lineTo(w - 16, 24 + i * 10)
                    ctx.stroke()
                }
            }

            Component.onCompleted: requestPaint()
        }

        Text {
            anchors.top: emptyIcon.bottom
            anchors.topMargin: 12
            anchors.horizontalCenter: parent.horizontalCenter
            text: qsTr("选择或创建一个便签")
            font.pixelSize: 14
            color: themeTokens ? themeTokens.emptyStateText : "#9F9286"
        }
    }

    onEditorTextChanged: {
        if (showContent && noteEditor.text !== editorText) {
            noteEditor._ready = false
            noteEditor.text = editorText
            noteEditor._ready = true
            noteEditor.contentReady()
            // 空内容(新建便签/Ctrl+A 删除)时,清除 cursor 残留的 charFormat/blockFormat,
            // 防止上次粘贴的背景色/字体污染新输入。非空内容保留 HTML 自带合法格式。
            if (editorText.length === 0 && textFormatHelper) {
                textFormatHelper.clearCharFormat()
            }
        }
    }

    onShowContentChanged: {
        if (showContent) {
            noteEditor._ready = false
            noteEditor.text = editorText
            noteEditor._ready = true
            noteEditor.contentReady()
            if (editorText.length === 0 && textFormatHelper) {
                textFormatHelper.clearCharFormat()
            }
        }
    }
}
