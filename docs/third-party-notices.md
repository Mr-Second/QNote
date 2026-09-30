# Third-Party Notices

Assets and embedded data QNote ships that originate from third-party projects.

## CM_EDITOR icon font — `src/QNote/Assets/Fonts/CM_EDITOR.ttf`

Extracted (2026-09-30) from the HarmonyOS a reference editor editor's bundled KindEditor
plugin (`products/pc/.../libs/KindEditor-plugin/theme.css`, where the TTF is
inlined as base64; internal family name `iconfont`). KindEditor is distributed
under the LGPL-2.1; the icon set follows the license of the project it ships
with. Used as the editor toolbar's icon font.

## Lucide icons — `src/third-party/WinUIRichEditor/Controls/RichEditorToolbar.cs`

The paragraph-style combo's heading glyphs (`heading-1..4`, `type`) embed
Lucide v1.49.0 path data as code constants. Lucide is ISC-licensed and carries
Feather Icons (MIT) ancestry — both permissive for embedding.

## Material Design Icons — `src/QNote/Controls/QNoteIcons.cs`

The table context menu's row/column operation icons embed path data from
Material Design Icons (`@mdi/svg` 7.4.47, Templated/Pictogrammers). MDI 7.x is
distributed under the Pictogrammers Free License (free to use and distribute;
derived from Google's Material icon set, originally Apache-2.0). Seven paths
are embedded: `table-row-plus-before/after`, `table-row-remove`,
`table-column-plus-before/after`, `table-column-remove`, `table-remove`.
