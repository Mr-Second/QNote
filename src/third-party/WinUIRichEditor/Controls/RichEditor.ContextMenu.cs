using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Right-click context menus (per-object: text / link / inline-image / image / table), organized into
// Character-format and Paragraph submenus and carrying RichEditorIcons (Segoe Fluent FontIcons), to match
// the AvaloniaRichEditor original. Built in code (no XAML).
public partial class RichEditor
{
    private void SetupContextMenu() => _canvas.RightTapped += OnCanvasRightTapped;

    private static string Loc(string k) => RichEditorLocalization.GetString(k);

    // Context-menu item font size, in DIPs (px) — WinUI's FontSize unit. 12px gives a compact, HWP-like menu,
    // deliberately smaller than the WinUI/Windows 11 default (ControlContentThemeFontSize = 14px, which reads
    // large like Explorer's menu). FontSize is not an inherited DP, so every menu item sets this explicitly.
    private const double MenuFontSize = 12;

    // Built-in icon for a menu slot (host override > Segoe Fluent FontIcon). Only IconElements fit the
    // MenuFlyoutItem.Icon slot, so a non-icon host element is ignored.
    private static IconElement? MenuIcon(RichEditorIcon icon)
        => (RichEditorIcons.TryCreate(icon) ?? ToolbarIcons.Create(icon)) as IconElement;

    private static MenuFlyoutItem Mi(string text, Action act, bool enabled = true, RichEditorIcon? icon = null, string? accel = null)
    {
        var mi = new MenuFlyoutItem { Text = text, IsEnabled = enabled, FontSize = MenuFontSize };
        if (icon is { } k && MenuIcon(k) is { } ic) mi.Icon = ic;
        // Display-only shortcut hint. The editor handles keys itself (OnKeyDown), so this must not be a real
        // KeyboardAccelerator (that would double-fire and, with the flyout closed, never trigger anyway).
        if (accel != null) mi.KeyboardAcceleratorTextOverride = accel;
        mi.Click += (_, _) => act();
        return mi;
    }

    private static MenuFlyoutSubItem Sub(string text, RichEditorIcon? icon, params MenuFlyoutItemBase[] children)
    {
        var sub = new MenuFlyoutSubItem { Text = text, FontSize = MenuFontSize };
        if (icon is { } k && MenuIcon(k) is { } ic) sub.Icon = ic;
        foreach (var c in children) sub.Items.Add(c);
        return sub;
    }

    private static MenuFlyoutSeparator Sep() => new();

    private Point _ctxMenuPos; // where the last context menu opened (for the table-insert grid flyout)

    // Which context menu a right-click opens. Split out of OnCanvasRightTapped because the decision is
    // the part worth pinning and the handler is the part that cannot be tested — it needs a
    // RightTappedRoutedEventArgs, which has no public constructor. The handler now does the hit-testing
    // and the acting; this decides, and it decides from plain values.
    internal enum ContextMenuKind { BlockImage, InlineImage, InlineTable, BlockTable, ReadOnlyText, Link, Text, CellBlock }

    /// The priority order, which is a contract and not an accident:
    /// an object under the pointer wins over any text menu (you right-clicked the object, not the line) —
    /// a table's left/top border band counts as the table, the band a click selects it on;
    /// read-only wins over everything textual (a viewer offers copy, never edit); and the concise link
    /// menu only replaces the full one when there is no selection — with a selection the user is acting
    /// on the selection, not on the link under the pointer.
    internal static ContextMenuKind ChooseContextMenu(
        bool onBlockImage, bool onInlineImage, bool onInlineTableEdge,
        bool isReadOnly, bool hasSelection, string? linkUri, bool onBlockTableEdge = false, bool onCellBlock = false)
    {
        if (onBlockImage) return ContextMenuKind.BlockImage;
        if (onInlineImage) return ContextMenuKind.InlineImage;
        if (onInlineTableEdge) return ContextMenuKind.InlineTable;
        if (onBlockTableEdge) return ContextMenuKind.BlockTable;
        if (isReadOnly) return ContextMenuKind.ReadOnlyText;
        // A cell block (F5, a drag, Shift+arrow) is the table's business: the table menu, as for a table held whole
        // (user decision, 2026-09-14 — a menu that fits what was clicked). It opened the text menu.
        if (onCellBlock) return ContextMenuKind.CellBlock;
        if (!hasSelection && !string.IsNullOrEmpty(linkUri)) return ContextMenuKind.Link;
        return ContextMenuKind.Text;
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _canvas.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        var pos = e.GetPosition(_canvas);             // view space — where the menu opens
        var menu = BuildContextMenuAt(pos);
        Compact(menu.Items);
        if (menu.Items.Count > 0) menu.ShowAt(_canvas, pos);
        e.Handled = true;
    }

    // Denser rows, as AvaloniaRichEditor's menu (user request, 2026-09-14): WinUI's default item padding
    // (11,9,11,10 measured) sizes rows for touch — 37px rows, 9px separators with a 12px font. Upstream's rows
    // are 10,2,10,2 (18px there, 1px separators; measured in its headless host), so the same numbers here.
    // Applied to every item, submenu item and separator on the way out, so no builder has to remember it.
    private static readonly Thickness MenuItemPadding = new(10, 2, 10, 2);
    private static readonly Thickness MenuSeparatorPadding = new(12, 0, 12, 0);

    private static void Compact(System.Collections.Generic.IList<MenuFlyoutItemBase> items)
    {
        foreach (var item in items)
        {
            item.Padding = item is MenuFlyoutSeparator ? MenuSeparatorPadding : MenuItemPadding;
            if (item is MenuFlyoutSubItem sub) Compact(sub.Items);
        }
    }

    // Everything a right-click does except opening the flyout — hit-testing, moving the caret or selecting
    // the object, building the menu — so it can be driven from a plain point: the handler's
    // RightTappedRoutedEventArgs has no public constructor, which kept this whole path untested.
    internal MenuFlyout BuildContextMenuAt(Point pos)
    {
        var menu = BuildContextMenuCore(pos);
        // The right-click moved the caret or selected what it landed on: tell the host now, not with whatever
        // input comes next — a table selected by its border raised neither StatusChanged nor SelectionChanged
        // (measured 2026-09-14), so a toolbar kept showing the state from before the click.
        RaiseStatusChanged();
        return menu;
    }

    private MenuFlyout BuildContextMenuCore(Point pos)
    {
        CancelTableDraw(); // a right-click abandons an armed table-draw
        _ctxMenuPos = pos;
        var ipt = ViewToDoc(new Point(pos.X, pos.Y)); // doc space — for hit-testing

        // Hit-test the per-object targets first; the decision below turns these into a menu choice.
        ImageBlock? hitBlockImage = null;
        (Paragraph p, InlineImage img)? hitInlineImage = null;
        (Paragraph host, InlineTable it)? hitInlineTable = null;

        foreach (var (img, rect) in BlockImageRects())
            if (rect.Contains(ipt)) { hitBlockImage = img; break; }
        // A block image inside a TABLE CELL is not in the block layout map — it has its own registry,
        // filled from the rect it was actually DRAWN at. The pointer path (TryBeginImageInteraction)
        // and the handle path (BlockImageHandleRects) both consult it; this one did not, so a cell
        // image could be clicked, selected, dragged and resized, and still right-clicked as plain text.
        if (hitBlockImage == null)
            foreach (var (img, rect) in _cellImageRects)
                if (rect.Contains(ipt)) { hitBlockImage = img; break; }
        if (hitBlockImage == null)
            foreach (var (img, v) in _inlineImageRects)
                if (v.rect.Contains(ipt)) { hitInlineImage = (v.p, img); break; }
        if (hitBlockImage == null && hitInlineImage == null)
            foreach (var (it, v) in _inlineTableRects)
                if (OnEdgeBorder(v.rect, ipt)) { hitInlineTable = (v.host, it); break; }
        // A top-level table's left/top border band — where a click selects the table. A right-click there
        // opened the text menu (the band lies partly outside the grid, and the caret moved to whatever was
        // nearest), so the table a click had just selected could not be copied from the menu (live check,
        // 2026-09-13).
        TableBlock? edgeTable = null;
        if (hitBlockImage == null && hitInlineImage == null && hitInlineTable == null
            && OnTableSelectBorder(ipt, out var et)) edgeTable = et;

        // Right-clicking outside an existing selection moves the caret there first (Word/VS behavior).
        // Only when no object was hit — those select the object instead.
        bool onObject = hitBlockImage != null || hitInlineImage != null || hitInlineTable != null || edgeTable != null;
        // Not on an object: one selected earlier (a table held by its border) lets go, as a left press does. It
        // stayed selected while the caret moved to the text, and Delete then removed it (upstream round 34; the
        // read-only branch below already dropped it).
        if (!onObject && HasBlockSelection) { ClearObjectSelection(); InvalidateCanvas(); }
        // On a link, the caret goes just past the character UNDER the pointer — inside the link — so the link
        // menu and its caret-based actions (open/edit/remove) act on the link the pointer is on. The nearest
        // boundary alone put the left half of a link's first character before the link (the text menu) and a
        // point just past its end at its end (the link menu) — half a character off on both edges, the defect
        // the click path had (measured 2026-09-13). Upstream hit-tests the character (GetLinkRunAtPoint).
        if (!onObject && !HasSelection && GetPositionFromPoint(ipt) is { } tp)
        {
            _caret = LinkHitAtPoint(ipt) is { } hit ? CaretJustPast(hit.p, hit.ch) : tp;
            CollapseSelectionToCaret(); InvalidateCanvas();
        }

        // The caret may have just moved, so read the selection and the link AFTER it. Without a selection the
        // link is the one under the pointer; with one, the menu acts on the selection and the link items on
        // the caret's link, as they always have.
        bool hasSel = HasSelection;
        string? linkUri = onObject ? null : hasSel ? CurrentLinkUri() : LinkHitAtPoint(ipt)?.run.NavigateUri;
        var cellBlock = onObject ? null : CellBlockSelection();
        var kind = ChooseContextMenu(hitBlockImage != null, hitInlineImage != null, hitInlineTable != null,
                                     IsReadOnly, hasSel, linkUri, edgeTable != null, cellBlock != null);

        var menu = new MenuFlyout();
        switch (kind)
        {
            case ContextMenuKind.BlockImage:
                _selectedInline = null; _selectedBlock = hitBlockImage; CollapseSelectionToCaret(); InvalidateCanvas();
                return BuildImageMenu(hitBlockImage, null);

            case ContextMenuKind.InlineImage:
                _selectedBlock = null; _selectedInline = hitInlineImage; CollapseSelectionToCaret(); InvalidateCanvas();
                return BuildImageMenu(null, hitInlineImage!.Value.img);

            // A table held whole by its border names no cell: the cell items (셀 선택, rows, columns, merge, cell
            // alignment and background) are greyed — which cell would they act on? (user decision, 2026-09-14). The
            // band reaches into the grid, so taking the cell under the pointer made them act on an edge cell nobody chose.
            case ContextMenuKind.InlineTable:
                ClearObjectSelection(); _selectedInlineTable = hitInlineTable; CollapseSelectionToCaret(); InvalidateCanvas();
                return BuildInlineTableMenu(hitInlineTable!.Value.host, hitInlineTable.Value.it);

            case ContextMenuKind.BlockTable:
                ClearObjectSelection(); _selectedBlock = edgeTable; CollapseSelectionToCaret(); InvalidateCanvas();
                return BuildBlockTableMenu(edgeTable!);

            case ContextMenuKind.CellBlock:
            {
                // The block stays selected; its row/column items act on the cell under the pointer, else its corner.
                var cb = cellBlock!.Value;
                var (pr, pc) = PointerCell(ipt, cb.tb);
                if (pr < 0) (pr, pc) = (cb.r0, cb.c0);
                return BuildTableMenu(cb.tb, pr, pc, DeleteTableAction(cb.tb), DeleteCellBlockContent);
            }

            case ContextMenuKind.ReadOnlyText: // a viewer: copy + select-all only
            {
                // Right-clicked in a table with nothing selected: Copy takes the TABLE, as right-clicking an
                // image copies the image. The generic Copy acts on the selection — empty here — so a viewer
                // got a greyed-out item and no way to take the table (live check, 2026-09-12). The table is
                // the cell's the caret just moved into (its border band is the BlockTable case above).
                var table = hasSel ? null
                    : _caret.Paragraph is { } cp && FindCell(cp) is { } at ? at.tb : null;
                // ...and it is shown selected, so the viewer sees what Copy will take (live check, 2026-09-13).
                if (table != null) SelectTableObject(table);
                else if (HasBlockSelection) { ClearObjectSelection(); InvalidateCanvas(); } // a table shown selected earlier must not linger
                menu.Items.Add(Mi(Loc("Copy"), () => _ = table != null ? CopyTableToClipboard(table) : CopyAsync(),
                                  hasSel || table != null, RichEditorIcon.Copy, "Ctrl+C"));
                menu.Items.Add(Mi(Loc("SelectAll"), SelectAll, true, RichEditorIcon.SelectAll, "Ctrl+A"));
                return menu;
            }

            case ContextMenuKind.Link:
                BuildLinkMenu(menu, linkUri!);
                return menu;

            default:
                BuildTextMenu(menu, hasSel, linkUri);
                return menu;
        }
    }

    // Shows `tb` selected as an object — the chrome a border click draws: a top-level table, a table in a
    // cell (which has its chrome since 2026-09-13), or an inline table.
    private void SelectTableObject(TableBlock tb)
    {
        ClearObjectSelection();
        if (tb.Parent is FlowDocument or TableCell) _selectedBlock = tb;
        else if (tb.Parent is InlineTable it && it.Parent is Paragraph host) _selectedInlineTable = (host, it);
        InvalidateCanvas();
    }

    // The caret-position text menu, shared by top-level paragraphs and table cells.
    // HWP-style arrangement: edit block → separator → 글자 모양 / 문단 모양 / 목록 / 제목 grouping submenus →
    // hyperlink → select/undo → inserts. Flattened vs. the old layout — alignment and margin now live directly
    // under 문단 모양 (no extra nesting level), and list/heading are promoted to top level. Windows-standard
    // clipboard terms (잘라내기/복사/붙여넣기) are kept; only the layout changed.
    internal void BuildTextMenu(MenuFlyout menu, bool hasSel, string? linkUri)
    {
        void Item(MenuFlyoutItemBase i) => menu.Items.Add(i);
        var fmt = GetCaretFormat(); // current caret state → radio/check reflection

        // ── Edit (clipboard) ──
        Item(Mi(Loc("Cut"), () => _ = CutAsync(), hasSel, RichEditorIcon.Cut, "Ctrl+X"));
        Item(Mi(Loc("Copy"), () => _ = CopyAsync(), hasSel, RichEditorIcon.Copy, "Ctrl+C"));
        Item(Mi(Loc("Paste"), () => _ = PasteAsync(), true, RichEditorIcon.Paste, "Ctrl+V"));
        Item(Mi(Loc("Delete"), () => { if (HasSelection) { PushUndo(null); DeleteSelection(); AfterEdit(); } }, hasSel, RichEditorIcon.Delete, "Del"));
        Item(Sep());

        if (ShowFormattingMenu)
        {
            // ── 글자 모양 (character shape) ── quick toggles + font size larger/smaller + clear.
            // Precise pickers (specific size, text color, highlight, font family) live on the toolbar.
            // Always enabled: without a selection ApplyStyleToSelection targets the caret word, or arms a
            // pending format for the next typed text — same as the slim menu and the keyboard shortcuts.
            // The four toggles are CHECK items reflecting the caret, like the slim menu's: as plain items
            // this, the fuller menu, was the one that could not show whether bold was already on.
            Item(Sub(Loc("CharacterFormat"), RichEditorIcon.CharacterFormat,
                BulletToggle(Loc("Bold"), fmt.Bold, ToggleBold, RichEditorShortcuts.Display(RichEditorShortcutId.Bold), RichEditorIcon.Bold),
                BulletToggle(Loc("Italic"), fmt.Italic, ToggleItalic, RichEditorShortcuts.Display(RichEditorShortcutId.Italic), RichEditorIcon.Italic),
                BulletToggle(Loc("Underline"), fmt.Underline, ToggleUnderline, RichEditorShortcuts.Display(RichEditorShortcutId.Underline), RichEditorIcon.Underline),
                BulletToggle(Loc("Strikethrough"), fmt.Strike, ToggleStrikethrough, RichEditorShortcuts.Display(RichEditorShortcutId.Strikethrough), RichEditorIcon.Strikethrough),
                Sep(),
                Mi(Loc("FontSizeIncrease"), IncreaseFontSize, true, RichEditorIcon.FontSizeIncrease, "Ctrl+Shift+."),
                Mi(Loc("FontSizeDecrease"), DecreaseFontSize, true, RichEditorIcon.FontSizeDecrease, "Ctrl+Shift+,"),
                Sep(),
                Mi(Loc("ClearFormatting"), ClearFormatting, true, RichEditorIcon.ClearFormatting)));

            // ── 문단 모양 (paragraph shape) — alignment radios + indent + margin, flattened ──
            Item(BuildParagraphFormatSub(fmt));

            // ── 목록 (list) — promoted to top level ──
            Item(Sub(Loc("List"), RichEditorIcon.BulletList,
                BulletToggle(Loc("BulletList"), fmt.List == ListKind.Bullet, ToggleBullet, RichEditorShortcuts.Display(RichEditorShortcutId.BulletList)),
                // Style submenus list STYLES only; a list is turned off by its own toggle (which clears the whole
                // list state). The labelled "목록 제거" was a duplicate door and went (user decision, 2026-09-14).
                Sub(Loc("BulletStyle"), null,
                    Mi("•", () => SetListStyle(ListMarkerStyle.Disc)), Mi("◦", () => SetListStyle(ListMarkerStyle.Circle)),
                    Mi("▪", () => SetListStyle(ListMarkerStyle.Square)), Mi("–", () => SetListStyle(ListMarkerStyle.Dash))),
                BulletToggle(Loc("NumberedList"), fmt.List == ListKind.Ordered, ToggleNumbering, RichEditorShortcuts.Display(RichEditorShortcutId.NumberedList)),
                Sub(Loc("NumberStyle"), null,
                    Mi("1.", () => SetListStyle(ListMarkerStyle.Decimal)), Mi("1)", () => SetListStyle(ListMarkerStyle.DecimalParen)),
                    Mi("a)", () => SetListStyle(ListMarkerStyle.LowerAlpha)), Mi("A)", () => SetListStyle(ListMarkerStyle.UpperAlpha)),
                    Mi("i)", () => SetListStyle(ListMarkerStyle.LowerRoman))),
                Sep(),
                BulletToggle(Loc("Quote"), fmt.Quote, ToggleQuote)));

            // ── 제목 (heading / style) — promoted to top level, radio-checked ──
            Item(Sub(Loc("Heading"), null,
                HeadingItem(Loc("Heading1"), 1, fmt.Heading), HeadingItem(Loc("Heading2"), 2, fmt.Heading),
                HeadingItem(Loc("Heading3"), 3, fmt.Heading), HeadingItem(Loc("Heading4"), 4, fmt.Heading),
                HeadingItem(Loc("Heading5"), 5, fmt.Heading), HeadingItem(Loc("Heading6"), 6, fmt.Heading),
                Sep(),
                HeadingItem(Loc("BodyText"), 0, fmt.Heading)));
        }
        else
        {
            // Slim (default): just the quick character toggles, checked to reflect the caret. Full formatting
            // lives on the toolbar; a standalone host can opt into the rich groups via ShowFormattingMenu.
            Item(BulletToggle(Loc("Bold"), fmt.Bold, ToggleBold, "Ctrl+B"));
            Item(BulletToggle(Loc("Italic"), fmt.Italic, ToggleItalic, "Ctrl+I"));
            Item(BulletToggle(Loc("Underline"), fmt.Underline, ToggleUnderline, "Ctrl+U"));
        }

        Item(Sep());

        // ── Hyperlink ──
        if (!string.IsNullOrEmpty(linkUri))
        {
            // Disabled for a link it would refuse to launch (see IsLaunchableLink) rather than silently doing nothing.
            Item(Mi(Loc("OpenLink"), () => _ = OpenLinkAtCaretAsync(), IsLaunchableLink(linkUri, out _), RichEditorIcon.OpenLink));
            Item(Mi(Loc("EditLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.EditLink, RichEditorShortcuts.Display(RichEditorShortcutId.InsertLink)));
            Item(Mi(Loc("RemoveLink"), () => SetHyperlink(null), true, RichEditorIcon.RemoveLink));
        }
        else
            // Enabled without a selection too: the link applies to the caret word (ApplyStyleToSelection).
            Item(Mi(Loc("InsertLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.InsertLink, RichEditorShortcuts.Display(RichEditorShortcutId.InsertLink)));

        Item(Sep());
        Item(Mi(Loc("SelectAll"), SelectAll, true, RichEditorIcon.SelectAll, "Ctrl+A"));
        Item(Mi(Loc("Undo"), Undo, CanUndo, RichEditorIcon.Undo, "Ctrl+Z"));
        Item(Mi(Loc("Redo"), Redo, CanRedo, RichEditorIcon.Redo, "Ctrl+Y"));

        // Inserts (gated by feature flags)
        if (AllowTables || AllowImages) Item(Sep());
        if (AllowTables) Item(Mi(Loc("InsertTable"), () => ShowInsertTableGrid(_ctxMenuPos), true, RichEditorIcon.InsertTable));
        if (AllowTables || AllowImages) Item(Mi(Loc("InsertDivider"), InsertDivider, true, RichEditorIcon.InsertDivider));

        // Inside a cell: the table-structure operations in a "Table" submenu.
        var cellLoc = _caret.Paragraph != null ? FindCell(_caret.Paragraph) : null;
        if (cellLoc is { } loc)
        {
            Item(Sep());
            Item(SelectCellItem(loc.tb, loc.r, loc.c)); // in reach while editing the cell, not only in the submenu
            menu.Items.Add(BuildTableSubmenu(loc.tb, loc.r, loc.c));
        }
    }

    // 문단 모양 submenu: alignment as a radio group (current value checked) + indent +/- + per-side margin.
    // Flattens the old 문단 ▸ 정렬 ▸ / 여백 nesting into one level, HWP-style.
    private MenuFlyoutSubItem BuildParagraphFormatSub(CaretFormat fmt)
    {
        RadioMenuFlyoutItem Align(string key, TextAlignment a, RichEditorShortcutId sc)
        {
            var ri = new RadioMenuFlyoutItem { Text = Loc(key), GroupName = "ctxAlign", IsChecked = fmt.Align == a, FontSize = MenuFontSize };
            ri.KeyboardAcceleratorTextOverride = RichEditorShortcuts.Display(sc);
            ri.Click += (_, _) => SetTextAlignment(a);
            return ri;
        }
        var sub = new MenuFlyoutSubItem { Text = Loc("ParagraphFormat"), FontSize = MenuFontSize };
        sub.Items.Add(Align("AlignLeft", TextAlignment.Left, RichEditorShortcutId.AlignLeft));
        sub.Items.Add(Align("AlignCenter", TextAlignment.Center, RichEditorShortcutId.AlignCenter));
        sub.Items.Add(Align("AlignRight", TextAlignment.Right, RichEditorShortcutId.AlignRight));
        sub.Items.Add(Align("AlignJustify", TextAlignment.Justify, RichEditorShortcutId.AlignJustify));
        sub.Items.Add(Sep());
        sub.Items.Add(Mi(Loc("IndentIncrease"), () => Indent(20), true, RichEditorIcon.IndentIncrease, RichEditorShortcuts.Display(RichEditorShortcutId.IndentIncrease)));
        sub.Items.Add(Mi(Loc("IndentDecrease"), () => Indent(-20), true, RichEditorIcon.IndentDecrease, RichEditorShortcuts.Display(RichEditorShortcutId.IndentDecrease)));
        // Margin (top-level paragraphs only — cell paragraphs lay out inside the cell).
        if (_caret.Paragraph is { } mp && Document != null && Document.Blocks.IndexOf(mp) >= 0)
        {
            sub.Items.Add(Sep());
            sub.Items.Add(MarginMenu(mp));
        }
        return sub;
    }

    // A checkable toggle item for an on/off caret state — list/quote, or a character format (checked
    // reflects the caret). The icon is optional: the full 글자 모양 group had icons as plain items and must
    // keep them now that its items are checkable.
    private static ToggleMenuFlyoutItem BulletToggle(string text, bool isChecked, Action act, string? accel = null, RichEditorIcon? icon = null)
    {
        var t = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked, FontSize = MenuFontSize };
        if (icon is { } k && MenuIcon(k) is { } ic) t.Icon = ic;
        if (accel != null) t.KeyboardAcceleratorTextOverride = accel;
        t.Click += (_, _) => act();
        return t;
    }

    // A heading level as a radio item within the 제목 group (current level checked).
    private RadioMenuFlyoutItem HeadingItem(string text, int level, int current)
    {
        var ri = new RadioMenuFlyoutItem { Text = text, GroupName = "ctxHeading", IsChecked = current == level, FontSize = MenuFontSize };
        // Heading1..6 are consecutive enum values; level 0 = body text.
        var sc = level == 0 ? RichEditorShortcutId.BodyText : (RichEditorShortcutId)((int)RichEditorShortcutId.Heading1 + level - 1);
        ri.KeyboardAcceleratorTextOverride = RichEditorShortcuts.Display(sc);
        ri.Click += (_, _) => SetHeading(level);
        return ri;
    }


    // The same 8×10 grid picker the toolbar button uses (hover to choose rows×columns, click to insert at
    // the caret). WinUI menus can't host a grid, so the menu item opens this as a separate flyout.
    private void ShowInsertTableGrid(Point pos)
    {
        if (!AllowTables || IsReadOnly) return;
        const int rows = 8, cols = 10;
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterStyle = RichEditorToolbar.TightFlyoutPresenter() };
        var root = new StackPanel { Spacing = 4, Padding = new Thickness(2) };
        var label = new TextBlock { Text = "1 × 1", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var idle = new SolidColorBrush(Color.FromArgb(255, 0xE4, 0xE4, 0xE4));
        var hot = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xA0, 0xE0));
        var border = new SolidColorBrush(Color.FromArgb(255, 0xAA, 0xAA, 0xAA));
        var cells = new Border[rows, cols];

        void Highlight(int rr, int cc)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    cells[r, c].Background = (r <= rr && c <= cc) ? hot : idle;
            label.Text = $"{rr + 1} × {cc + 1}";
        }

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var cell = new Border
                {
                    Width = 16, Height = 16, Margin = new Thickness(1),
                    Background = idle, BorderBrush = border, BorderThickness = new Thickness(0.5),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                int rr = r, cc = c;
                cell.PointerEntered += (_, _) => Highlight(rr, cc);
                // Pick rows×cols here; then drag from the caret on the document to set the table's size.
                cell.Tapped += (_, _) => { BeginTableDraw(rr + 1, cc + 1); flyout.Hide(); };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }
        Highlight(0, 0);
        root.Children.Add(grid);
        root.Children.Add(label);
        flyout.Content = root;
        flyout.ShowAt(_canvas, new FlyoutShowOptions { Position = pos });
    }

    // Per-side margin presets for a block (radio-checked at the current value). Left maps to Block.Indent;
    // right exists for paragraphs only (nothing flows around images/tables).
    private MenuFlyoutSubItem MarginMenu(Block target)
    {
        // `auto`: lead with Block.AutoTopMargin (NaN, one line gap) — the default top of a table, picture or divider,
        // which no px preset shows checked and nothing else could restore once a preset was picked (upstream).
        MenuFlyoutSubItem Side(string label, Func<double> get, Action<double> set, bool auto = false)
        {
            var s = new MenuFlyoutSubItem { Text = label, FontSize = MenuFontSize };
            foreach (double v in auto ? new[] { Block.AutoTopMargin, 0d, 5, 10, 20, 30 } : new[] { 0d, 5, 10, 20, 30 })
            {
                double vv = v;
                var ri = new RadioMenuFlyoutItem
                {
                    Text = double.IsNaN(vv) ? Loc("MarginAuto") : $"{vv:0} px", GroupName = label,
                    IsChecked = double.IsNaN(vv) ? double.IsNaN(get()) : Math.Abs(get() - vv) < 0.5, FontSize = MenuFontSize,
                };
                ri.Click += (_, _) => PickMargin(get, set, vv);
                s.Items.Add(ri);
            }
            return s;
        }
        var sub = new MenuFlyoutSubItem { Text = Loc("Margin"), FontSize = MenuFontSize };
        sub.Items.Add(Side(Loc("MarginTop"), () => target.MarginTop, v => target.MarginTop = v, auto: target is not Paragraph));
        sub.Items.Add(Side(Loc("MarginBottom"), () => target.MarginBottom, v => target.MarginBottom = v));
        sub.Items.Add(Side(Loc("MarginLeft"), () => target.Indent, v => target.Indent = v));
        if (target is Paragraph mp) sub.Items.Add(Side(Loc("MarginRight"), () => mp.MarginRight, v => mp.MarginRight = v));
        return sub;
    }

    // A margin preset's click (split out so a test can reach it — a flyout item cannot be clicked from code).
    private void PickMargin(Func<double> get, Action<double> set, double v)
    {
        if (get().Equals(v)) return; // already in force: no undo step that undoes nothing (as the cell v-align radio)
        if (Document != null) PushUndo(null);
        set(v);
        AfterFormat();
    }

    // Cell vertical-alignment radio submenu for the anchor cell at (r,c) — Top/Center/Bottom, checked
    // at the current value. Row heights are unchanged (alignment redistributes slack), so a repaint
    // plus the undo checkpoint is all that's needed.
    private MenuFlyoutSubItem BuildCellVAlignSub(TableBlock tb, int r, int c)
    {
        var (ar, ac) = tb.AnchorOf(r, c);
        var cell = tb.Cells[ar][ac];
        var sub = new MenuFlyoutSubItem { Text = Loc("CellVerticalAlign"), FontSize = MenuFontSize };
        void AddOpt(string key, CellVerticalAlignment va)
        {
            var ri = new RadioMenuFlyoutItem
            { Text = Loc(key), GroupName = "ctxCellVAlign", IsChecked = cell.VerticalAlignment == va, FontSize = MenuFontSize };
            ri.Click += (_, _) =>
            {
                if (Document == null || IsReadOnly || cell.VerticalAlignment == va) return;
                PushUndo(null);
                cell.VerticalAlignment = va;
                InvalidateCanvas();
                RaiseStatusChanged();
            };
            sub.Items.Add(ri);
        }
        AddOpt("VAlignTop", CellVerticalAlignment.Top);
        AddOpt("VAlignCenter", CellVerticalAlignment.Center);
        AddOpt("VAlignBottom", CellVerticalAlignment.Bottom);
        return sub;
    }

    // Concise menu for a right-clicked hyperlink: link actions + copy address, no formatting clutter.
    private void BuildLinkMenu(MenuFlyout menu, string uri)
    {
        menu.Items.Add(Mi(Loc("OpenLink"), () => _ = OpenLinkAtCaretAsync(), IsLaunchableLink(uri, out _), RichEditorIcon.OpenLink));
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep()); // navigation vs. edit
            menu.Items.Add(Mi(Loc("EditLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.EditLink));
            menu.Items.Add(Mi(Loc("RemoveLink"), () => SetHyperlink(null), true, RichEditorIcon.RemoveLink));
        }
        menu.Items.Add(Mi(Loc("CopyLink"), async () =>
        {
            var dp = new DataPackage();
            dp.SetText(uri);
            try { await RetryWhileClipboardBusyAsync(() => Clipboard.SetContent(dp)); }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
        }, true, RichEditorIcon.CopyLink));
        menu.Items.Add(Sep());
        menu.Items.Add(Mi(Loc("Copy"), () => _ = CopyAsync(), HasSelection, RichEditorIcon.Copy, "Ctrl+C"));
    }

    // Image context menu: size presets (fraction of natural), replace, save, margin, block↔inline, delete.
    internal MenuFlyout BuildImageMenu(ImageBlock? block, InlineImage? inline)
    {
        string L(string k) => RichEditorLocalization.GetString(k);
        var menu = new MenuFlyout();
        void Add(string text, Action act, bool enabled = true, RichEditorIcon? icon = null) => menu.Items.Add(Mi(text, act, enabled, icon));
        void Original() { if (block != null) ResetBlockImageNatural(block); else if (inline != null) ResetInlineImageNatural(inline); }
        void Scale(double f) { if (block != null) ScaleBlockImage(block, f); else if (inline != null) ScaleInlineImage(inline, f); }

        byte[]? raw = block?.RawBytes ?? inline?.RawBytes;
        string? mime = block?.MimeType ?? inline?.MimeType;
        var bmp = block?.Image ?? inline?.Image;
        bool hasNatural = NaturalImageSize(raw) != null;

        // ── Edit ── Copy the image to the system clipboard (works in-app and into other apps). Pass
        // inline-ness and the displayed size so an in-app paste restores them (see CopyImageToClipboardAsync).
        bool isInline = inline != null;
        double iw = block?.Width ?? inline?.Width ?? 0;
        double ih = block?.Height ?? inline?.Height ?? 0;
        menu.Items.Add(Mi(L("Copy"), () => _ = CopyImageToClipboardAsync(raw, bmp, isInline, iw, ih), raw != null || bmp != null, RichEditorIcon.Copy, "Ctrl+C"));

        // ── 개체 모양 (object shape): size preset, 글자처럼 취급, margin ──
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep());
            menu.Items.Add(Sub(L("ImageSize"), null,
                Mi(L("OriginalSize"), Original, hasNatural),
                Mi(L("HalfSize"), () => Scale(1.0 / 2)),
                Mi(L("ThirdSize"), () => Scale(1.0 / 3)),
                Mi(L("QuarterSize"), () => Scale(1.0 / 4))));
            // "Treat as character": checked while the image is inline; toggling converts block↔inline.
            var inlineToggle = new ToggleMenuFlyoutItem { Text = L("InlineWithText"), IsChecked = inline != null, FontSize = MenuFontSize };
            inlineToggle.Click += (_, _) =>
            {
                if (block != null) ConvertImageBlockToInline(block);
                else if (inline != null) ConvertInlineImageToBlock(inline.Parent as Paragraph ?? _caret.Paragraph!, inline);
            };
            menu.Items.Add(inlineToggle);
            if (block != null) menu.Items.Add(MarginMenu(block));
            // Accessibility description (HTML alt). Round-trips through JSON/.flow and HTML.
            menu.Items.Add(Mi(L("AltText"), () => _ = EditImageAltTextAsync(block, inline)));
        }

        // ── File ops: replace / save ──
        bool canReplace = !IsReadOnly && ImageReplacePicker != null;
        bool canSave = ImageSaveHandler != null;
        if (canReplace || canSave)
        {
            menu.Items.Add(Sep());
            if (canReplace) Add(L("ReplaceImage"), () => _ = ReplaceImageBytesAsync((object?)block ?? inline!), true, RichEditorIcon.ReplaceImage);
            if (canSave) Add(L("SaveImageAs"), () => _ = SaveImageBytesAsync(raw, mime), raw != null, RichEditorIcon.SaveImageAs);
        }

        // ── Delete ──
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep());
            Add(L("Delete"), DeleteSelectedObject, true, RichEditorIcon.Delete);
        }
        return menu;
    }

    // The cells a background goes on: the cell block when one is on this table — a ONE-cell block included, which
    // SelectedCellRange does not see (it painted the cell under the pointer instead) — else the cell at (r,c).
    internal IEnumerable<TableCell> CellBackgroundTargets(TableBlock tb, int r, int c)
    {
        if (CellBlockSelection() is { } cb && ReferenceEquals(cb.tb, tb))
        {
            foreach (var (rr, cc, cell) in tb.LogicalCells())
                if (rr >= cb.r0 && rr <= cb.r1 && cc >= cb.c0 && cc <= cb.c1) yield return cell;
        }
        else if (r >= 0 && c >= 0)
        {
            var (ar, ac) = tb.AnchorOf(r, c);
            yield return tb.Cells[ar][ac];
        }
    }

    // Cell-background palette flyout (the toolbar's 40 swatches + "none"), on CellBackgroundTargets.
    private void ShowCellBackgroundPalette(TableBlock tb, int r, int c)
    {
        if (IsReadOnly || Document == null) return;
        var flyout = new Flyout { FlyoutPresenterStyle = RichEditorToolbar.TightFlyoutPresenter() };

        void Apply(Color? color)
        {
            flyout.Hide();
            PushUndo(null);
            foreach (var cell in CellBackgroundTargets(tb, r, c)) cell.Background = color;
            InvalidateCanvas();
            RaiseStatusChanged();
        }

        var grid = new ToolbarWrapPanel { Width = 8 * 24, HorizontalSpacing = 0, VerticalSpacing = 0 };
        var borderBrush = new SolidColorBrush(Color.FromArgb(255, 0x80, 0x80, 0x80));
        foreach (var hex in RichEditorToolbar.Palette)
        {
            if (ColorUtil.Parse(hex) is not { } color) continue;
            var sw = new Button
            {
                Background = new SolidColorBrush(color),
                Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0),
                BorderThickness = new Thickness(1), BorderBrush = borderBrush,
            };
            sw.Click += (_, _) => Apply(color);
            grid.Children.Add(sw);
        }

        var none = new Button { Content = Loc("HighlightNone"), HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => Apply(null);

        var panel = new StackPanel { Spacing = 6, Width = 200, Padding = new Thickness(4) };
        panel.Children.Add(grid);
        panel.Children.Add(none);
        flyout.Content = panel;
        flyout.ShowAt(_canvas, new FlyoutShowOptions { Position = _ctxMenuPos });
    }

    // Menu for a whole inline table selected by its border — the table menu. A viewer's is Copy alone: an object
    // wins the right-click even in a viewer (ChooseContextMenu), which once gave it an EMPTY flyout.
    internal MenuFlyout BuildInlineTableMenu(Paragraph host, InlineTable it, int r = -1, int c = -1)
        => BuildTableMenu(it.Table, r, c, DeleteSelectedInlineTable, DeleteSelectedInlineTable);

    // Menu for a whole block table selected by its border (top-level, or a table in a cell) — the table menu.
    internal MenuFlyout BuildBlockTableMenu(TableBlock tb, int r = -1, int c = -1)
        => BuildTableMenu(tb, r, c, DeleteSelectedObject, DeleteSelectedObject);

    // The table menu: what a right-click opens on a table held whole (its border) or on a cell block — the same,
    // item for item, as AvaloniaRichEditor's (user decision, 2026-09-14: a menu that fits what was clicked — text,
    // table or image). The clipboard verbs, then the table's own items (AddTableStructureItems) for the cell (r, c):
    // the one under the pointer, or -1 when the right-click was on the border (those items greyed). Delete is
    // `deleteContent` — the whole table when it is held, the cells' content for a cell block, as the Delete key;
    // 표 삭제 is `deleteTable`. It replaced a short object menu (copy · cut · 글자처럼 취급 · 표 삭제) and, for a cell
    // block, the text menu with the table in a submenu. A viewer gets Copy alone.
    internal MenuFlyout BuildTableMenu(TableBlock tb, int r, int c, Action deleteTable, Action deleteContent)
    {
        var menu = new MenuFlyout();
        if (IsReadOnly)
        {
            menu.Items.Add(Mi(Loc("Copy"), () => _ = CopyAsync(), true, RichEditorIcon.Copy, "Ctrl+C"));
            return menu;
        }
        menu.Items.Add(Mi(Loc("Cut"), () => _ = CutAsync(), true, RichEditorIcon.Cut, "Ctrl+X"));
        menu.Items.Add(Mi(Loc("Copy"), () => _ = CopyAsync(), true, RichEditorIcon.Copy, "Ctrl+C"));
        menu.Items.Add(Mi(Loc("Paste"), () => _ = PasteAsync(), true, RichEditorIcon.Paste, "Ctrl+V"));
        menu.Items.Add(Mi(Loc("Delete"), deleteContent, true, RichEditorIcon.Delete, "Del"));
        menu.Items.Add(Sep());
        menu.Items.Add(SelectCellItem(tb, r, c));
        menu.Items.Add(Sep());
        AddTableStructureItems(menu.Items, tb, r, c, deleteTable);
        return menu;
    }

    // 셀 선택 (F5): the cell (r, c) as a one-cell block — Delete clears it, formatting and the background take all
    // of it, Copy takes it as a 1×1 table, Shift+arrow grows it by cells. In the table menu after the clipboard verbs,
    // and in a cell's text menu right above the "Table" submenu (user decision, 2026-09-14: in reach while editing).
    private MenuFlyoutItem SelectCellItem(TableBlock tb, int r, int c)
    {
        bool onCell = r >= 0 && c >= 0;
        return Mi(Loc("SelectCell"), () => { if (onCell) { var (ar, ac) = tb.AnchorOf(r, c); SelectCellAsBlock(tb.Cells[ar][ac]); } },
                  onCell, null, RichEditorShortcuts.Display(RichEditorShortcutId.SelectCell));
    }

    // Deleting `tb` from a menu: an inline table leaves its host line (DeleteTable removes blocks only).
    private Action DeleteTableAction(TableBlock tb)
        => tb.Parent is InlineTable it && it.Parent is Paragraph host
            ? () => { ClearObjectSelection(); _selectedInlineTable = (host, it); DeleteSelectedInlineTable(); }
            : () => DeleteTable(tb);

    // Delete on a cell block: its cells' content, the grid stays — as the Delete key.
    private void DeleteCellBlockContent()
    {
        if (!HasSelection) return;
        PushUndo(null);
        DeleteSelection();
        AfterEdit();
    }

    // The anchor cell of `tb` under a document point, or (-1, -1).
    private (int r, int c) PointerCell(Point docPt, TableBlock tb)
        => GetPositionFromPoint(docPt) is { Paragraph: { } p } && FindCell(p) is { } loc && ReferenceEquals(loc.tb, tb)
            ? tb.AnchorOf(loc.r, loc.c) : (-1, -1);

    // The "Table" submenu of the text menu, when editing inside a cell: the table menu's own items.
    internal MenuFlyoutSubItem BuildTableSubmenu(TableBlock tb, int r, int c)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc("TableOps"), FontSize = MenuFontSize };
        AddTableStructureItems(sub.Items, tb, r, c, DeleteTableAction(tb));
        return sub;
    }

    // The table's own items, shared by the table menu and the text menu's "Table" submenu, in AvaloniaRichEditor's
    // order: rows | columns | merge | 셀 세로 정렬 · 셀 배경 · 여백 · 글자처럼 취급 | 표 삭제 (셀 선택 stands before them,
    // placed by each menu — SelectCellItem). An item that does not apply is greyed, not dropped (user decision,
    // 2026-09-14), so the same items stand in the same places. "셀 복사" went: F5 then Copy does it.
    private void AddTableStructureItems(IList<MenuFlyoutItemBase> items, TableBlock tb, int r, int c, Action deleteTable)
    {
        bool onCell = r >= 0 && c >= 0;
        void Add(string text, Action act, bool enabled = true, RichEditorIcon? icon = null) => items.Add(Mi(text, act, enabled, icon));

        // ── Rows ──
        Add(Loc("InsertRowAbove"), () => TableInsertRow(tb, r), r >= 0, RichEditorIcon.InsertRowAbove);
        Add(Loc("InsertRowBelow"), () => TableInsertRow(tb, RowBelowIndex(tb, r, c)), r >= 0, RichEditorIcon.InsertRowBelow);
        Add(Loc("DeleteRow"), () => TableDeleteRow(tb, r), r >= 0 && tb.Rows > 1, RichEditorIcon.DeleteRow);
        items.Add(Sep());
        // ── Columns ──
        Add(Loc("InsertColumnLeft"), () => TableInsertColumn(tb, c), c >= 0, RichEditorIcon.InsertColumnLeft);
        Add(Loc("InsertColumnRight"), () => TableInsertColumn(tb, ColumnRightIndex(tb, r, c)), c >= 0, RichEditorIcon.InsertColumnRight);
        Add(Loc("DeleteColumn"), () => TableDeleteColumn(tb, c), c >= 0 && tb.Columns > 1, RichEditorIcon.DeleteColumn);
        items.Add(Sep());
        // ── Merge / split ──
        bool canMerge = SelectedCellRange(tb) is { } rg && IsCleanRect(tb, rg.r0, rg.c0, rg.r1, rg.c1);
        Add(Loc("MergeCells"), () => TableMergeSelected(tb), canMerge, RichEditorIcon.MergeCells);
        bool canUnmerge = onCell && (tb.SpanOf(r, c).cs > 1 || tb.SpanOf(r, c).rs > 1);
        Add(Loc("UnmergeCells"), () => TableUnmergeCell(tb, r, c), canUnmerge, RichEditorIcon.UnmergeCells);
        items.Add(Sep());
        // ── 표 모양 (table shape): cell vertical alignment + background + margin + 글자처럼 취급 ──
        items.Add(onCell ? BuildCellVAlignSub(tb, r, c)
                         : new MenuFlyoutSubItem { Text = Loc("CellVerticalAlign"), FontSize = MenuFontSize, IsEnabled = false });
        // Cell background: model + every serializer already round-trip TableCell.Background; this palette (shared
        // with the toolbar pickers) is the edit surface — on the cell block, else the cell (CellBackgroundTargets).
        Add(Loc("CellBackground"), () => ShowCellBackgroundPalette(tb, r, c), CellBackgroundTargets(tb, r, c).Any());
        items.Add(MarginMenu(tb));
        // 글자처럼 취급 converts a TOP-LEVEL table (ConvertTableBlockToInline anchors to the paragraphs around it) or
        // an inline one back; a table in a cell is offered no toggle that would do nothing.
        if (tb.Parent is FlowDocument)
        {
            var t = new ToggleMenuFlyoutItem { Text = Loc("InlineWithText"), IsChecked = false, FontSize = MenuFontSize };
            t.Click += (_, _) => ConvertTableBlockToInline(tb);
            items.Add(t);
        }
        else if (tb.Parent is InlineTable it && it.Parent is Paragraph host)
        {
            var t = new ToggleMenuFlyoutItem { Text = Loc("InlineWithText"), IsChecked = true, FontSize = MenuFontSize };
            t.Click += (_, _) => ConvertInlineTableToBlock(host, it);
            items.Add(t);
        }
        items.Add(Sep());
        Add(Loc("DeleteTable"), deleteTable, true, RichEditorIcon.DeleteTable);
    }
}
