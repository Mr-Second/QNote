import QtQuick
import HuskarUI.Basic

QtObject {
    id: root

    property bool isDark: HusTheme.isDark

    property color windowColor: isDark ? "#1E1E1E" : "#F3F3F3"

    // 桌面背景渐变：中性灰层（外壳中性化）
    property color rootGradientStart: isDark ? "#2A2A2A" : "#F5F5F5"
    property color rootGradientMid: isDark ? "#232323" : "#EDEDED"
    property color rootGradientEnd: isDark ? "#1A1A1A" : "#E3E3E3"

    // 桌面柔光层：覆盖在渐变之上，制造画板氛围
    property color rootGlow: isDark ? "#0EFFFFFF" : "#14FFFFFF"
    property color rootAmbientLight: isDark ? "#0A000000" : "#08000000"
    property real rootAmbientRadius: isDark ? 0.55 : 0.62

    // 左栏（导航壳）：最轻、最透明
    property color sidebarShellBg: isDark ? "#14FFFFFF" : "#FFFFFF"
    property color sidebarShellBorder: isDark ? "#1CFFFFFF" : "#22FFFFFF"
    property color sidebarShellInnerVeil: isDark ? "#08FFFFFF" : "#0EFFFFFF"
    property color sidebarSelectionBase: isDark ? "#2D2D2D" : "#FFFFFF"

    // 中栏（摘要面板）：居中强度
    property color listPanelBg: isDark ? "#2D2D2D" : "#FFFFFF"
    property color listPanelBorder: isDark ? "#3A3A3A" : "#E0E0E0"
    property color listPanelInnerVeil: isDark ? "#0AFFFFFF" : "#10FFFFFF"

    // 右栏（主编辑区）：最完整、最有层次
    property color editorPanelBg: isDark ? "#323232" : "#FFFFFF"
    property color editorPanelBorder: isDark ? "#404040" : "#E0E0E0"
    property color editorPanelInnerVeil: isDark ? "#10FFFFFF" : "#1CFFFFFF"
    property color editorPanelTopGlow: isDark ? "#10FFFFFF" : "#28FFFFFF"
    property color noteSelectionBase: isDark ? "#333333" : "#FFFFFF"
    property color noteSelectionBorderBase: isDark ? "#4A4A4A" : "#E0E0E0"
    property color noteHoverBg: isDark ? "#10FFFFFF" : "#0CA0897D"
    property color noteHoverBorder: isDark ? "#2AFFFFFF" : "#E7D8C9"

    property color titleText: isDark ? "#F0F0F0" : "#1A1A1A"
    property color textPrimary: isDark ? "#E8E8E8" : "#202020"
    property color textSecondary: isDark ? "#B0B0B0" : "#5F5F5F"
    property color textTertiary: isDark ? "#909090" : "#757575"
    property color textMuted: isDark ? "#787878" : "#8C8C8C"
    property color sidebarMutedText: isDark ? "#A0A0A0" : "#5F5F5F"
    property color textOnAccent: "#FFF8F2"
    property color iconMuted: isDark ? "#A8A8A8" : "#616161"

    // 品牌蓝（Accent Color）：贯穿全局选中态、链接、hover
    property color accentBase: isDark ? "#4A9EFF" : "#0078D4"
    property color accentHover: isDark ? "#5CABFF" : "#1A86D9"
    property color accentPressed: isDark ? "#3A8FE6" : "#006AB8"
    property color accentOnAccent: "#FFFFFF"
    property color accentSelectionBg: isDark ? Qt.rgba(0.29, 0.62, 1.0, 0.14) : Qt.rgba(0.0, 0.47, 0.83, 0.10)
    property color accentHoverBg: isDark ? Qt.rgba(1, 1, 1, 0.06) : Qt.rgba(0.0, 0.47, 0.83, 0.05)
    property color accentSelectionBorder: isDark ? Qt.rgba(0.29, 0.62, 1.0, 0.30) : Qt.rgba(0.0, 0.47, 0.83, 0.22)
    property color textSelectionBg: isDark ? Qt.rgba(0.29, 0.62, 1.0, 0.30) : Qt.rgba(0.0, 0.47, 0.83, 0.25)

    property color divider: isDark ? "#22FFFFFF" : "#14000000"
    property color scrollbarActive: isDark ? "#888888" : "#A0A0A0"
    property color scrollbarInactive: isDark ? "#555555" : "#CCCCCC"

    property color actionBubbleBg: isDark ? "#10FFFFFF" : "#18A0897D"
    property color actionBubbleIcon: isDark ? "#BBAEA4" : "#A18F81"
    property color dangerBubbleBg: isDark ? "#24B96B4E" : "#16F8E9DB"
    property color dangerBubbleBgHover: isDark ? "#30CA7A5A" : "#FFF3E8"
    property color dangerBubbleBorder: isDark ? "#44D28768" : "#22B78C6E"
    property color dangerBubbleBorderHover: isDark ? "#66E59B79" : "#66C27E5D"
    property color dangerBubbleIcon: isDark ? "#F0B29A" : "#C27E5D"
    property color dangerBubbleIconHover: isDark ? "#FFD0BA" : "#B86843"

    property color paperBase: isDark ? "#403632" : "#FFFFFF"
    property color paperWarm: isDark ? "#342D2A" : "#F2E7D8"
    property color paperFoldTarget: isDark ? "#5A4E47" : "#D6C5B4"
    property real paperMix: isDark ? 0.76 : 0.86
    property real paperWarmMix: isDark ? 0.70 : 0.78
    property color paperHighlightTop: Qt.rgba(1, 1, 1, isDark ? 0.08 : 0.24)
    property color paperHighlightBottom: Qt.rgba(1, 1, 1, 0.0)
    property color paperStroke: isDark ? Qt.rgba(1, 1, 1, 0.08) : Qt.rgba(0.35, 0.28, 0.22, 0.1)
    property color paperTopLine: Qt.rgba(1, 1, 1, isDark ? 0.16 : 0.46)

    // 纸张顶部装饰：胶带感高光 / 压边
    property color paperTapeVeil: Qt.rgba(1, 1, 1, isDark ? 0.10 : 0.34)
    property color paperTapeEdge: Qt.rgba(1, 1, 1, isDark ? 0.18 : 0.55)
    property color paperTapeShadow: Qt.rgba(0, 0, 0, isDark ? 0.18 : 0.10)
    property real paperTapeInsetX: 0.41
    property real paperTapeWidth: 0.18
    property real paperTapeHeight: 14

    // 折角强化：阴影 + 边缘高光
    property color paperFoldLine: Qt.rgba(0, 0, 0, isDark ? 0.26 : 0.16)
    property color paperFoldShadow: Qt.rgba(0, 0, 0, isDark ? 0.30 : 0.14)
    property color paperEdgeHighlight: Qt.rgba(1, 1, 1, isDark ? 0.12 : 0.42)

    property color paperRuleColor: isDark ? "#F4E6D8" : "#6E6054"
    property real paperRuleAlpha: isDark ? 0.05 : 0.028
    property color paperDustBright: "#FFFFFF"
    property real paperDustBrightAlpha: isDark ? 0.018 : 0.012
    property color paperDustDark: isDark ? "#66574F" : "#726458"
    property real paperDustDarkAlpha: isDark ? 0.010 : 0.007
    property color paperHeaderVeil: isDark ? "#0AFFFFFF" : "#14FFFFFF"

    property color emptyStateStroke: isDark ? "#746860" : "#D5D5D5"
    property color emptyStateFill: isDark ? "#2B2522" : "#FAFAFA"
    property color emptyStateFold: isDark ? "#3A332F" : "#EBEBEB"
    property color emptyStateRule: isDark ? "#584E47" : "#E0E0E0"
    property color emptyStateText: isDark ? "#A6988B" : "#9F9286"

    property real panelShadowOpacity: isDark ? 0.18 : 0.12
    property real panelShadowScale: isDark ? 1.025 : 1.018
    property real editorShadowOpacity: isDark ? 0.20 : 0.15
    property real editorShadowScale: isDark ? 1.03 : 1.02

    // 三栏壳层阴影强度差异：左轻 / 中实 / 右最完整
    property real sidebarShadowOpacity: isDark ? 0.18 : 0.10
    property real sidebarShadowScale: isDark ? 1.02 : 1.015

    // 响应式断点：窗口尺寸由 Main.qml 绑定到 windowWidth/windowHeight
    property int windowWidth: 940
    property int windowHeight: 620
    readonly property int compactBreakpoint: 720
    readonly property int heightTightBreakpoint: 520
    readonly property bool isCompact: windowWidth < compactBreakpoint
    readonly property bool isHeightTight: windowHeight < heightTightBreakpoint

    // 主窗口最小尺寸：保证缩放下限可用
    property int windowMinWidth: 600
    property int windowMinHeight: 440

    // 三栏列宽策略：首选宽度 / 最小宽度（compact 下进一步压缩）
    // compact 下左栏仅显示图标（无文字），所以 min 收到 48
    property int categoryColumnPrefWidth: isCompact ? 48 : 148
    property int categoryColumnMinWidth: isCompact ? 48 : 130
    property int listColumnPrefWidth: isCompact ? 180 : 270
    property int listColumnMinWidth: isCompact ? 150 : 220
    property int editorColumnMinWidth: isCompact ? 260 : 280

    // 编辑区/标题区紧凑常量
    readonly property int editorSideMargin: isCompact ? 14 : 22
    readonly property int editorTitleRowHeight: isCompact ? 42 : 48
    readonly property int editorTitleFontSize: isCompact ? 16 : 18

    // 列表 delegate 紧凑
    readonly property int listDelegateHeight: isCompact ? 64 : 76
    readonly property int listDelegateVMargin: isCompact ? 8 : 12

    // 左栏 footer 紧凑
    readonly property int sidebarFooterHeight: isCompact ? 56 : 76

    // 字体数据源（SettingsDialog 与 NoteEditor 共享）
    // 已改为系统字体方案：不再打包思源黑体/霞鹜文楷
    readonly property var availableFonts: [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "SimSun",
        "SimHei",
        "KaiTi",
        "FangSong",
        "Consolas",
        "Arial"
    ]
    readonly property var availableFontDisplayNames: ({
        "Microsoft YaHei UI": qsTr("微软雅黑 UI"),
        "Microsoft YaHei": qsTr("微软雅黑"),
        "SimSun": qsTr("宋体"),
        "SimHei": qsTr("黑体"),
        "KaiTi": qsTr("楷体"),
        "FangSong": qsTr("仿宋"),
        "Consolas": "Consolas",
        "Arial": "Arial"
    })
    readonly property var availableUiFontSizes: [12, 14, 16, 18]
    readonly property var availableEditorFontSizes: [12, 14, 16, 18, 20, 24, 28, 32]

    // 分类图标:iconKey 非空时按 key 解析(HusIcon 枚举名 / 自定义图标 URL),
    // 否则按 fallbackIndex 返回默认图标(向后兼容)
    function categoryIcon(iconKey, fallbackIndex) {
        if (iconKey && iconKey.length > 0) {
            if (iconKey.startsWith("custom:")) {
                if (iconManager) {
                    return iconManager.resolveIconUrl(iconKey)
                }
                return ""
            }
            // 预设图标:HusIcon[iconKey]
            if (HusIcon[iconKey] !== undefined) return HusIcon[iconKey]
        }
        // fallback by index (原逻辑,向后兼容无 iconKey 的老分类)
        if (fallbackIndex === 0) return HusIcon.AppstoreOutlined
        if (fallbackIndex === 1) return HusIcon.RadiusSettingOutlined
        if (fallbackIndex === 2) return HusIcon.HomeOutlined
        return HusIcon.StarOutlined
    }

    function clamp01(value) {
        return Math.max(0, Math.min(1, value))
    }

    function withAlpha(colorValue, alpha) {
        return Qt.rgba(colorValue.r, colorValue.g, colorValue.b, clamp01(alpha))
    }

    function mixColor(baseColor, targetColor, amount, alpha) {
        const clamped = clamp01(amount)
        return Qt.rgba(
            baseColor.r * (1 - clamped) + targetColor.r * clamped,
            baseColor.g * (1 - clamped) + targetColor.g * clamped,
            baseColor.b * (1 - clamped) + targetColor.b * clamped,
            alpha === undefined ? 1 : clamp01(alpha)
        )
    }
}
