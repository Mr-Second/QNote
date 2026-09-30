# Local Patches to the Vendored WinUIRichEditor

The copy under `src/third-party/WinUIRichEditor/` tracks upstream **byte-identical**
except for the explicit patches documented here (owner policy: track LATEST; see
`AGENTS.md` / `.trellis/spec/guides/project-context.md` §Vendored third-party).
Count these on every upstream sync and re-apply them.

## P1 — Toolbar dark-mode active faces (`Controls/RichEditorToolbar.cs`)

**Why.** The toolbar's "active" (toggled-on) face, hover face, ink, inactive
marker, and "no highlight" swatch were hardcoded to light-theme colors
(`#DDE7F3` / `#CBDAEC` / black / `#BFC3C7` / `#DDDDDD`). On QNote's dark chrome
the active-state highlight glares; the spike flagged it as an appearance defect
(`…/09-29-editor-library-evaluation/research/qnote-embed-spike.md`).

**What.** The static brush caches now resolve their color from the toolbar's
effective theme:

- `EnsureThemeBrushes(bool isDark)` drops the per-thread cached brushes when the
  theme flips; `ActiveBrush` / `ActiveHoverBrush` / `BlackInk` / `DimInk` /
  `NoColorBrush` call it before (re)creating their brush and pick a light or dark
  color accordingly.
- `CurrentThemeImpl` (a `Func<bool>`, installed by the first toolbar instance)
  reads `ActualTheme == ElementTheme.Dark`, so the static caches stay free of a
  hard WinUI dependency (unit tests that never paint a toolbar are unaffected).
- The constructor subscribes `ActualThemeChanged` and rebuilds the strip, so an
  already-built toolbar recolors on a live Light↔Dark switch.

Table-draw preview colors (`RichEditorToolbar.cs` ~line 522) and the divider/
border grays are **not** patched: tables and those chrome affordances are out of
QNote's md subset (`AllowTables="False"`), so they never render.

**Upstream-sync impact.** Low. The change is confined to the brush helpers and
the constructor; upstream has been actively reworking the same area (toolbar
density levels), so expect a small manual re-merge. If upstream adopts
`ActualTheme`-aware brushes natively, drop P1 entirely.

_Last verified against the vendored HEAD adopted 2026-09-30._
