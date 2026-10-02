# Local Patches to the Vendored WinUIRichEditor

The copy under `src/third-party/WinUIRichEditor/` tracks upstream **byte-identical**
except for the explicit patches documented here (owner policy: track LATEST; see
`AGENTS.md` / `.trellis/spec/guides/project-context.md` §Vendored third-party).
Count these on every upstream sync and re-apply them.

**Diff inventory vs upstream HEAD** (verified 2026-10-01, hash-compared, excluding
`bin|obj`): 2 new files (`Controls/OverflowRowPanel.cs`,
`Controls/RichEditorIconRenderer.cs`) and 9 changed files
(`Controls/PathMarkup.cs`, `Controls/RichEditor.BlockSelection.cs`,
`Controls/RichEditor.ContextMenu.cs`, `Controls/RichEditor.Input.cs`,
`Controls/RichEditorIcons.cs`, `Controls/RichEditor.TableDraw.cs`,
`Controls/RichEditor.Tables.cs`, `Controls/RichEditorToolbar.cs`,
`Controls/RichEditorToolbar.PageFile.cs`). Nothing else differs.
(BlockSelection.cs joined the changed list 2026-10-03 with P4.)

## P1 — Toolbar dark-mode active faces (`Controls/RichEditorToolbar.cs`)

**Why.** The toolbar's "active" (toggled-on) face, hover face, ink, inactive
marker, and "no highlight" swatch were hardcoded to light-theme colors
(`#DDE7F3` / `#CBDAEC` / black / `#BFC3C7` / `#DDDDDD`). On QNote's dark chrome
the active-state highlight glares; the spike flagged it as an appearance defect
(`…/09-29-editor-library-evaluation/research/qnote-embed-spike.md`).

**What.** The static brush caches now resolve their color from the toolbar's
effective theme:

- `EnsureThemeBrushes` drops the per-thread cached brushes when the theme flips;
  the brush properties call it before (re)creating their brush and pick a light
  or dark color accordingly.
- `CurrentThemeImpl` (a `Func<bool>`, installed by the first toolbar instance)
  reads `ActualTheme == ElementTheme.Dark`, so the static caches stay free of a
  hard WinUI dependency (unit tests that never paint a toolbar are unaffected).
- The constructor subscribes `ActualThemeChanged` and rebuilds the strip, so an
  already-built toolbar recolors on a live Light↔Dark switch.

Editor-canvas table colors (the rubber-band preview in
`RichEditor.TableDraw.cs`, the canvas border grays) are **not** patched: the
editor body is host territory (QNote pins the canvas through the three
appearance DPs). The toolbar's own table grid picker IS theme-aware now (P3).

**Upstream-sync impact.** Low. The change is confined to the brush helpers and
the constructor; upstream has been actively reworking the same area (toolbar
density levels), so expect a small manual re-merge. If upstream adopts
`ActualTheme`-aware brushes natively, drop P1 entirely.

## P2 — `PathMarkup`: full Bézier + compact number parsing (`Controls/PathMarkup.cs`)

**Why.** The parser only accepted `M L H V Q A Z`, so any icon path using the
SVG cubic/smooth forms (`C`, `S`, `T` — which Lucide and CM_EDITOR-style packs
use everywhere) threw `FormatException` from a toolbar constructor →
`XamlParseException` killed app startup. A second latent bug: the number
scanner greedily consumed digits AND dots, so the SVG idiom `.5.5` (=
`0.5, 0.5`) parsed as one malformed token.

**What.** (+65/−2 lines, commit `4989206`)
- Added `C` (cubic) plus `S`/`T` (smooth), each reflecting the previous
  control point per the SVG spec.
- A second `.` (or a leading `-`/`+`) now terminates the current number.

**Upstream-sync impact.** MEDIUM-HIGH visibility: this is a general-purpose
parser fix inside a file upstream also touches. On conflict, prefer the
vendored side unless upstream grew its own C/S/T support — then drop the patch.

## P3 — Toolbar restyle + fixed two rows + high contrast

**Why.** Task `09-30-editor-toolbar-restyle`: the stock toolbar (bare 26×28
buttons, faint stock hover, single wrapping row, `ToolbarWrapPanel` wrap) was
rebuilt to the user's reference UI (ChordMail/HarmonyOS editor): a **fixed
two-row bar** — row 1 = icon buttons, row 2 = every dropdown — with
theme-aware rounded hover/pressed/active faces, group separators, border+radius
combos, a searchable font picker, and a per-row trailing "More ⋮" that folds
whatever does not fit into that row's own flyout. High contrast (PRD D11):
every hand-mixed face maps to the live `SystemColor*` set — no alpha washes.

**What.**
- `Controls/RichEditorToolbar.cs` (+677/−126): button visual spec (30×30,
  `CornerRadius(4)`, hover `#1A000000`/`#1AFFFFFF`, pressed, active accent
  10%/18%, 150 ms fade + 1.06 icon scale), `SeparatorBrush`/`ComboBorderBrush`,
  paragraph-style + alignment combos as "left icon + right text", the FontPicker
  (Button + Flyout with search box), A⁺/A⁻ buttons, **find button removed**, the
  fixed-two-row `Build()` with `OverflowRowPanel` rows and per-row More flyouts
  (the old More①/More② responsive machinery and the `ToolbarWrapPanel` strip
  wrapping were deleted; `ToolbarWrapPanel` itself survives — the color grid
  still uses it), the table grid picker theme-aware, and the **high-contrast
  adaptation**: brush getters branch on `IsHighContrast` (installed via
  `AccessibilitySettings`, `HighContrastChanged` rebuilds through the
  dispatcher; `EnsureThemeBrushes` keys on both knobs) and map to
  `UISettings.UIElementColor` (hover/active → `Highlight`, pressed →
  `ButtonFace`, ink → `WindowText`, checked ink → `HighlightText`, separator +
  combo border → `WindowText`, dim ink → `GrayText`).
- `Controls/OverflowRowPanel.cs` (NEW, ~1 file): the per-row justify + overflow
  panel (visible children justified left-left/right-right, overflow tail
  re-parented into the row's own flyout host; reentrancy + idempotence guards).
- `Controls/RichEditorIconRenderer.cs` (NEW, public): theme-aware vector-icon
  renderer reusing the vendored `PathMarkup` so hosts can inject SVG `d` icons
  through `RichEditorIcons.Provider` (AOT-safe; no XamlReader). Ink follows the
  theme hook and, in high contrast, the system `WindowText`.
- `Controls/RichEditorIcons.cs`: `RichEditorIcon` enum gains `More`,
  `AlignJustify`, `MoreVertical` — **appended only**, preserving shipped
  ordinals.
- `Controls/RichEditor.ContextMenu.cs`: a host provider element that is not an
  `IconElement` (QNote's FontIcons/Viewboxes) now falls through to the Segoe
  glyph instead of blanking the menu icon (cast-order fix). **Table menu
  amendments (user decisions 2026-10-01):** the Merge/Unmerge items are REMOVED
  (GFM storage has no merge syntax — a merge silently reverted to a plain grid
  on reload; the model keeps MergeCells for HTML paste with rowspan, the save
  side degrades it to a dense grid keeping the text), the context menu's table
  grid picker got the same theme/HC-aware square colors as the toolbar's, and
  both grid pickers now insert DIRECTLY at the caret via
  `InsertOrDrawTable` instead of always arming the crosshair drag.
- `Controls/RichEditor.Input.cs` + `Controls/RichEditor.TableDraw.cs`: the
  `InsertOrDrawTable` amendment — a sticky `_caretEstablished` flag (set on
  canvas GotFocus, cleared in `OnDocumentAssigned`) lets the grid pickers
  insert at the caret when the user has one in THIS document, and fall back to
  the draw-to-place mode only for a note never clicked into.
- `Controls/RichEditor.Tables.cs` + toolbar Sync + the context menu's
  InsertTable item: `internal bool CaretInTableCell` — QNote's GFM storage
  keeps cells inline-only, so a nested table or divider inside a cell cannot
  survive a save/reload; the toolbar's table/divider buttons and the menu's
  InsertTable item grey out while the caret sits in a cell instead of offering
  a construct that would silently vanish (same ruling as the merge removal;
  images stay enabled — inline refs DO persist in cells).
- `Controls/RichEditorToolbar.PageFile.cs` (+4/−2): `BuildPageControls` /
  `BuildFileActions` widened from `ToolbarWrapPanel` to `Panel` (the restyled
  strip is no longer a `ToolbarWrapPanel`).

**Host-side companions (NOT vendored, listed for orientation):** QNote installs
`RichEditorIcons.Provider` from `QNoteIcons.cs` (CM_EDITOR icon font) in
`App.xaml.cs`; icons come from `Assets/Fonts/CM_EDITOR.ttf` (family alias
`iconfont`), which is why the enum extensions exist.

**Upstream-sync impact.** HIGH — this patch owns most of `RichEditorToolbar.cs`
(≈42% of the file changed). Treat upstream toolbar changes as a semantic
re-merge of the visual spec, not a textual one: re-apply the *decisions*
(Visual Spec in the task PRD `09-30-editor-toolbar-restyle/prd.md`), not the
hunks. If upstream ships its own density/restyle system, evaluate replacing the
visual half while keeping the high-contrast mapping (which upstream lacks).

## P4 — Drag-resize now fires the edit notification (`Controls/RichEditor.BlockSelection.cs`)

**Why.** A drag-resize of a block/inline image pushed an undo step
(`PushDragUndoOnce`) and wrote `Width`/`Height` — i.e. it was an edit in every
respect except notification: `FinishImageResize` never called `AfterEdit`, so
`TextChanged` never fired. Hosts whose dirty tracking keys off `TextChanged`
(QNote) then missed a resize-ONLY edit entirely — Ctrl+S became a no-op until
some other edit dirtied the note. The context-menu resizes
(`ResetInlineImageNatural` / `ScaleInlineImage` / block twins) all call
`AfterEdit`; the drag path now does too (2026-10-03; exposed once QNote's
Markdown bridge began persisting image display sizes, making a resize an
actual content change).

**What.** `TryResizeImage` sets `_imageResized = true` after a size write;
`FinishImageResize` calls `AfterEdit()` only when a write actually happened
(a press-and-release on a handle without moving is not an edit). The Esc
mid-drag path (`RichEditor.Input.cs`) runs the same `FinishImageResize`, and
the already-written partial size is a real edit there too.

**Upstream-sync impact.** LOW — three lines around `FinishImageResize`; any
upstream rework of the resize drag should be checked for whether it now calls
`AfterEdit` itself (drop the patch if so).

_Last verified against the vendored HEAD adopted 2026-09-30 (re-verified
2026-10-01)._
