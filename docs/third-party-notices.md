# Third-Party Notices

Assets and embedded data QNote ships that originate from third-party projects.

## QNoteIcons icon font — `src/QNote/Assets/Fonts/QNoteIcons.ttf`

A purpose-built icon font (internal family name `QNoteIcons`), synthesized (2026-10-10)
by `scripts/icon-font/build.py` from the hmos editor icon set — SVG sources checked in at
`scripts/icon-font/src/` (43 `RichEditorIcon` slots → 42 glyphs). The source set originates
from the author's own ChordMail KindEditor-plugin (same author as QNote) and is
project-owned; no third-party license applies. Rebuild with `python scripts/icon-font/build.py`
(code points are recorded in `scripts/icon-font/codepoints.json`).

## Lucide icons — `src/third-party/WinUIRichEditor/Controls/RichEditorToolbar.cs`

The paragraph-style combo's heading glyphs (`heading-1..4`, `type`) embed
Lucide v1.49.0 path data as code constants. Lucide is ISC-licensed and carries
Feather Icons (MIT) ancestry — both permissive for embedding.

## vscode-codicons font — `src/QNote/Assets/Fonts/codicon.ttf`

The find/replace bar's icons use the official Microsoft vscode-codicons font
(https://github.com/microsoft/vscode-codicons, from the `@vscode/codicons` npm
dist; internal family name `codicon`), referenced at runtime as
`ms-appx:///Assets/Fonts/codicon.ttf#codicon`. vscode-codicons is distributed
under the Creative Commons Attribution 4.0 International license
(https://creativecommons.org/licenses/by/4.0/). Only the find-widget glyph
subset is exercised: chevron-right / chevron-down (replace toggle),
case-sensitive (match case), arrow-up / arrow-down (previous / next match),
replace / replace-all, and widget-close.
