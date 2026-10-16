#!/usr/bin/env python3
"""QNoteIcons icon-font generator (fontTools + svgpathtools + pyclipper).

Synthesizes src/QNote/Assets/Fonts/QNoteIcons.ttf from the hmos editor icon SVG
set in scripts/icon-font/src/ (project-owned: the author's own ChordMail
KindEditor-plugin icon skin). One glyph per unique SVG; code points are
assigned deterministically from U+F500 over the mapped RichEditorIcon slot
names in alphabetical order (EditLink/InsertLink share link.svg's glyph).

Pipeline per SVG (24x24 viewBox):
  - paths are collected with inherited paint attributes (the root <svg> sets
    fill="none"); the leading full-viewBox background placeholder rect
    ("M0 0h24v24H0z" and its 16x16 / 24x19 siblings) is skipped;
    fill-opacity / stroke-opacity are ignored (monochrome ink)
  - fill paths: subpaths are adaptively flattened, then boolean-unioned per
    path element honoring that element's fill-rule (evenodd / nonzero)
  - stroke paths: offset by strokeWidth/2 with round joins (linecap honored)
    into filled outlines, then unioned in; CLOSED stroke paths take a
    two-pass outer(+delta)/inner(-delta) offset so the ring keeps its hole
  - resulting contours are Douglas-Peucker simplified and mapped into the em
    box (24x24 viewBox -> upm 1024, centered on the baseline); outer contours
    end up clockwise, holes counter-clockwise (TrueType nonzero convention)

Outputs:
  - src/QNote/Assets/Fonts/QNoteIcons.ttf
  - scripts/icon-font/codepoints.json
  - a ready-to-paste C# constants block on stdout (for
    src/QNote/Controls/QNoteIconGlyphs.cs)

Re-running is byte-stable (head table timestamps are pinned). Dependencies:
fontTools, svgpathtools, pyclipper ("pip install fonttools svgpathtools
pyclipper").
"""

from __future__ import annotations

import json
import math
import re
from pathlib import Path
import xml.etree.ElementTree as ET

import pyclipper
from svgpathtools import Arc, CubicBezier, Line, QuadraticBezier, parse_path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen

REPO = Path(__file__).resolve().parents[2]
SRC_DIR = Path(__file__).resolve().parent / "src"
OUT_TTF = REPO / "src" / "QNote" / "Assets" / "Fonts" / "QNoteIcons.ttf"
OUT_JSON = Path(__file__).resolve().parent / "codepoints.json"

FAMILY = "QNoteIcons"
UPM = 1024
EM_HALF = UPM // 2
ADVANCE = 1000
FIRST_CP = 0xF500
VIEWBOX = 24.0
CLIP_SCALE = 100          # svg units -> clipper integer units
FLATTEN_TOL = 0.005       # svg units, adaptive curve flattening
SIMPLIFY_TOL = 1.2        # clipper units (~0.012 svg units), Douglas-Peucker
# Fixed build date (TrueType longDateTime = mac epoch seconds, 1904-01-01)
# so rebuilds are byte-identical.
HEAD_TIMESTAMP = 3_874_435_200  # 2026-10-10T00:00:00Z

# RichEditorIcon slot -> hmos SVG (alphabetical slot order == code point order).
# TodoRepeat* slots (added 2026-10-15) are NOT RichEditorIcon members — they are
# the todo page's repeat-menu calendar set (user-supplied, path-only, stroke
# style; TodoRepeatWeekend is carried as a spare, no menu item uses it yet).
SLOT_SVG = {
    "AlignCenter": "justifycenter.svg",
    "AlignJustify": "justifyfull.svg",
    "AlignLeft": "justifyleft.svg",
    "AlignRight": "justifyright.svg",
    "BodyText": "p.svg",
    "Bold": "bold.svg",
    "BulletList": "xUL.svg",
    "ClearFormatting": "removeformat.svg",
    "Copy": "copy.svg",
    "Cut": "cut.svg",
    "Delete": "cancel.svg",
    "DeleteColumn": "tablecoldelete.svg",
    "DeleteRow": "tablerowdelete.svg",
    "DeleteTable": "tabledelete.svg",
    "EditLink": "link.svg",
    "Find": "search.svg",
    "FontSizeDecrease": "fontsizedecrease.svg",
    "FontSizeIncrease": "fontsizeincrease.svg",
    "FormatPainter": "formatpainter.svg",
    "Heading1": "h1.svg",
    "Heading2": "h2.svg",
    "Heading3": "h3.svg",
    "Heading4": "h4.svg",
    "Highlight": "hilitecolor.svg",
    "IndentDecrease": "outdent.svg",
    "IndentIncrease": "indent.svg",
    "InsertColumnLeft": "tablecolinsertleft.svg",
    "InsertColumnRight": "tablecolinsertright.svg",
    "InsertDivider": "hr.svg",
    "InsertImage": "insertimage.svg",
    "InsertLink": "link.svg",
    "InsertRowAbove": "tablerowinsertabove.svg",
    "InsertRowBelow": "tablerowinsertbelow.svg",
    "InsertTable": "table.svg",
    "Italic": "italic.svg",
    "LineSpacing": "lineheight.svg",
    "MergeCells": "table-merge-cells.svg",
    "More": "more.svg",
    "NumberedList": "xOL.svg",
    "Paste": "paste.svg",
    "Quote": "blockquote.svg",
    "Redo": "redo.svg",
    "ReplaceImage": "image.svg",
    "Strikethrough": "strikethrough.svg",
    "TextColor": "forecolor.svg",
    "TodoRepeatCustom": "custom.svg",
    "TodoRepeatDaily": "everyday.svg",
    "TodoRepeatMonthly": "month.svg",
    "TodoRepeatWeekdays": "workday.svg",
    "TodoRepeatWeekend": "weekend.svg",
    "TodoRepeatWeekly": "week.svg",
    "TodoRepeatYearly": "year.svg",
    # TodoTools* slots (added 2026-10-16) are the todo page's list-options
    # menu set (user-supplied AI-generated, path-only; stroke style at 1.5
    # except the two fill icons dot.svg/theme.svg). Slot names sort AFTER
    # TodoRepeat* on purpose: the repeat-menu codepoints are HARD-CODED in
    # TodoPage.xaml (F52D..F533 literals) and must never shift; Underline/
    # Undo/UnmergeCells follow via named constants.
    "TodoToolsDots": "dot.svg",
    "TodoToolsEmail": "email.svg",
    "TodoToolsEyeOff": "eyeoff.svg",
    "TodoToolsHideCompleted": "finish.svg",
    "TodoToolsPin": "pin.svg",
    "TodoToolsPrint": "print.svg",
    "TodoToolsSort": "sort.svg",
    "TodoToolsSortByCreated": "create_time.svg",
    "TodoToolsSortByDueDate": "deadline.svg",
    "TodoToolsSortByImportance": "importance.svg",
    "TodoToolsSortByTitle": "sort_by_letter.svg",
    "TodoToolsTheme": "theme.svg",
    "Underline": "underline.svg",
    "Undo": "undo.svg",
    "UnmergeCells": "table-split-cells.svg",
}

# Full-viewBox background placeholder rect ("M0 0h{w}v{h}H0z" in any spelling;
# the hmos sources use 24x24, 16x16 and 24x19 view boxes).
_BG_RECT = re.compile(r"^m0+h(\d+)v(\d+)h0z$")
# Subtrees that define resources, not rendered ink (clipPath children carry
# fill="#fff" background rects that must never become glyph outlines).
NON_RENDERING = {"defs", "clipPath", "mask", "pattern", "marker", "symbol",
                 "linearGradient", "radialGradient"}


# --------------------------------------------------------------------------
# SVG parsing

def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _norm_d(d: str) -> str:
    return "".join(c for c in d.lower() if not c.isspace() and c not in ",-")


def _visible_paths(svg_path: Path) -> list[tuple[str, dict[str, str]]]:
    """Collect (d, effective attrs) for every visible <path>, walking the tree
    with inherited paint attributes (fill / stroke / fill-rule / stroke-width /
    stroke-linecap). Invisible paths (background placeholder, fill=none +
    stroke=none) are dropped."""
    root = ET.parse(svg_path).getroot()
    vb = root.get("viewBox", "0 0 24 24").split()
    vb_w, vb_h = float(vb[2]), float(vb[3])
    inheritable = ("fill", "stroke", "fill-rule", "stroke-width", "stroke-linecap")
    out: list[tuple[str, dict[str, str]]] = []

    def walk(el: ET.Element, attrs: dict[str, str]) -> None:
        if _local_name(el.tag) in NON_RENDERING:
            return
        merged = dict(attrs)
        for key in inheritable:
            value = el.get(key)
            if value is not None:
                merged[key] = value
        if _local_name(el.tag) == "path":
            d = el.get("d", "")
            m = _BG_RECT.match(_norm_d(d))
            if not (m and float(m.group(1)) == vb_w and float(m.group(2)) == vb_h):
                out.append((d, merged))
        for child in el:
            walk(child, merged)

    walk(root, {})
    return out


# --------------------------------------------------------------------------
# Flattening (adaptive; tolerance in svg units)

def _dist_to_chord(p: complex, a: complex, b: complex) -> float:
    d = b - a
    if d == 0:
        return abs(p - a)
    length_sq = (d.real * d.real) + (d.imag * d.imag)
    t = (((p - a).real * d.real) + ((p - a).imag * d.imag)) / length_sq
    t = max(0.0, min(1.0, t))
    return abs(p - (a + t * d))


def _flatten_cubic(p0: complex, p1: complex, p2: complex, p3: complex,
                   tol: float, out: list[complex]) -> None:
    if max(_dist_to_chord(p1, p0, p3), _dist_to_chord(p2, p0, p3)) <= tol:
        out.append(p3)
        return
    a = (p0 + p1) / 2
    b = (p1 + p2) / 2
    c = (p2 + p3) / 2
    ab = (a + b) / 2
    bc = (b + c) / 2
    mid = (ab + bc) / 2
    _flatten_cubic(p0, a, ab, mid, tol, out)
    _flatten_cubic(mid, bc, c, p3, tol, out)


def _flatten_quad(p0: complex, p1: complex, p2: complex,
                  tol: float, out: list[complex]) -> None:
    if _dist_to_chord(p1, p0, p2) <= tol:
        out.append(p2)
        return
    a = (p0 + p1) / 2
    b = (p1 + p2) / 2
    mid = (a + b) / 2
    _flatten_quad(p0, a, mid, tol, out)
    _flatten_quad(mid, b, p2, tol, out)


def _arc_point(c: complex, rx: float, ry: float, phi: float, theta: float) -> complex:
    """Parametric ellipse point (SVG spec F.6.3.1)."""
    return c + complex(rx * math.cos(theta) * math.cos(phi) - ry * math.sin(theta) * math.sin(phi),
                       rx * math.cos(theta) * math.sin(phi) + ry * math.sin(theta) * math.cos(phi))


def _arc_derivative(rx: float, ry: float, phi: float, theta: float) -> complex:
    return complex(-rx * math.sin(theta) * math.cos(phi) - ry * math.cos(theta) * math.sin(phi),
                   -rx * math.sin(theta) * math.sin(phi) + ry * math.cos(theta) * math.cos(phi))


def _flatten_arc(start: complex, end: complex, radius: complex, rotation_deg: float,
                 large_arc: bool, sweep: bool, tol: float, out: list[complex]) -> None:
    """SVG elliptical arc -> cubic beziers -> adaptive flatten.

    Replaces svgpathtools' Arc.as_cubic_curves, which DROPS the large-arc flag
    (a 278deg arc flattens as its 82deg complement — the repeat-year circular
    arrow rendered as a bare hook). Endpoint->center conversion per SVG spec
    F.6.5, then per-<=90deg-segment cubic approximation (k = 4/3*tan(d/4))."""
    rx, ry = abs(radius.real), abs(radius.imag)
    phi = math.radians(rotation_deg)
    if rx == 0 or ry == 0 or start == end:
        out.append(end)
        return
    # F.6.5.1: transform to center-origin space
    dx, dy = (start.real - end.real) / 2, (start.imag - end.imag) / 2
    cos_phi, sin_phi = math.cos(phi), math.sin(phi)
    x1p = cos_phi * dx + sin_phi * dy
    y1p = -sin_phi * dx + cos_phi * dy
    # F.6.6.2: radii correction
    lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
    if lam > 1:
        s = math.sqrt(lam)
        rx, ry = rx * s, ry * s
    # F.6.5.2: center
    num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    coef = math.sqrt(max(0.0, num / den)) if den != 0 else 0.0
    if large_arc == sweep:
        coef = -coef
    cxp = coef * (rx * y1p) / ry
    cyp = coef * (-ry * x1p) / rx
    c = complex(cos_phi * cxp - sin_phi * cyp + (start.real + end.real) / 2,
                sin_phi * cxp + cos_phi * cyp + (start.imag + end.imag) / 2)
    # F.6.5.6-7: start angle + sweep
    def ang(ux, uy, vx, vy):
        n = math.hypot(ux, uy) * math.hypot(vx, vy)
        if n == 0:
            return 0.0
        cos_t = max(-1.0, min(1.0, (ux * vx + uy * vy) / n))
        t = math.acos(cos_t)
        return -t if ux * vy - uy * vx < 0 else t

    theta1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    dtheta = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    dtheta %= 2 * math.pi
    if not sweep and dtheta > 0:
        dtheta -= 2 * math.pi
    elif sweep and dtheta < 0:
        dtheta += 2 * math.pi
    # split into <=90deg segments, cubic-approximate each, flatten adaptively
    n_seg = max(1, int(math.ceil(abs(dtheta) / (math.pi / 2))))
    step = dtheta / n_seg
    k = 4 / 3 * math.tan(step / 4)
    p0 = _arc_point(c, rx, ry, phi, theta1)
    for i in range(n_seg):
        t1 = theta1 + i * step
        t2 = t1 + step
        p3 = _arc_point(c, rx, ry, phi, t2)
        p1 = p0 + k * _arc_derivative(rx, ry, phi, t1)
        p2 = p3 - k * _arc_derivative(rx, ry, phi, t2)
        _flatten_cubic(p0, p1, p2, p3, tol, out)
        p0 = p3


def _flatten_subpaths(d: str, tol: float) -> list[list[complex]]:
    path = parse_path(d)
    subpaths: list[list[complex]] = []
    for sp in path.continuous_subpaths():
        points = [sp[0].start]
        for seg in sp:
            if isinstance(seg, Line):
                points.append(seg.end)
            elif isinstance(seg, QuadraticBezier):
                _flatten_quad(seg.start, seg.control, seg.end, tol, points)
            elif isinstance(seg, CubicBezier):
                _flatten_cubic(seg.start, seg.control1, seg.control2, seg.end, tol, points)
            elif isinstance(seg, Arc):
                _flatten_arc(seg.start, seg.end, seg.radius, seg.rotation,
                             seg.large_arc, seg.sweep, tol, points)
            else:
                raise TypeError(f"unsupported SVG segment: {type(seg).__name__}")
        subpaths.append(points)
    return subpaths


# --------------------------------------------------------------------------
# Boolean ops (pyclipper; integer clip space = svg units * CLIP_SCALE)

def _to_clip(points: list[complex]) -> list[tuple[int, int]]:
    return [(int(round(p.real * CLIP_SCALE)), int(round(p.imag * CLIP_SCALE)))
            for p in points]


def _union(paths: list[list[tuple[int, int]]], fill_type: int) -> list[list[tuple[int, int]]]:
    if not paths:
        return []
    clipper = pyclipper.Pyclipper()
    clipper.AddPaths(paths, pyclipper.PT_SUBJECT, True)
    return clipper.Execute(pyclipper.CT_UNION, fill_type, fill_type)


def _stroke_outline(clip_pts: list[tuple[int, int]], width: float,
                    linecap: str) -> list[list[tuple[int, int]]]:
    offsetter = pyclipper.PyclipperOffset()
    closed = len(clip_pts) >= 4 and clip_pts[0] == clip_pts[-1]
    delta = int(round(width * CLIP_SCALE / 2))
    if closed:
        offsetter.AddPath(clip_pts[:-1], pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
        # A positive offset on a CLOSED polygon returns only the expanded outer
        # bound — the contracted inner bound needs a second negative pass, and
        # must be REVERSED so the downstream NONZERO union punches it as a hole
        # (single-pass = every closed stroke shape renders as a filled
        # silhouette; latent until the stroke-based repeat icons arrived).
        outer = offsetter.Execute(delta)
        inner = offsetter.Execute(-delta)
        return outer + [list(reversed(p)) for p in inner]
    end_type = {"round": pyclipper.ET_OPENROUND,
                "square": pyclipper.ET_OPENSQUARE}.get(linecap, pyclipper.ET_OPENBUTT)
    offsetter.AddPath(clip_pts, pyclipper.JT_ROUND, end_type)
    return offsetter.Execute(delta)


def _point_line_dist(p: tuple[int, int], a: tuple[int, int], b: tuple[int, int]) -> float:
    px, py = p
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    length_sq = dx * dx + dy * dy
    if length_sq == 0:
        return math.hypot(px - ax, py - ay)
    t = (((px - ax) * dx) + ((py - ay) * dy)) / length_sq
    t = max(0.0, min(1.0, t))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def _douglas_peucker(points: list[tuple[int, int]], tol: float) -> list[tuple[int, int]]:
    n = len(points)
    if n < 3:
        return list(points)
    keep = [False] * n
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1:
            continue
        best, best_dist = -1, -1.0
        for k in range(i + 1, j):
            dist = _point_line_dist(points[k], points[i], points[j])
            if dist > best_dist:
                best, best_dist = k, dist
        if best_dist > tol:
            keep[best] = True
            stack.append((i, best))
            stack.append((best, j))
    return [p for p, k in zip(points, keep) if k]


def _simplify_ring(points: list[tuple[int, int]], tol: float) -> list[tuple[int, int]]:
    simplified = _douglas_peucker(points + [points[0]], tol)
    if len(simplified) >= 2 and simplified[0] == simplified[-1]:
        simplified = simplified[:-1]
    return simplified if len(simplified) >= 3 else points


def _contours_with_holes(paths: list[list[tuple[int, int]]]) -> list[tuple[list[tuple[int, int]], bool]]:
    """Final union via PolyTree so every contour carries an explicit hole flag."""
    clipper = pyclipper.Pyclipper()
    clipper.AddPaths(paths, pyclipper.PT_SUBJECT, True)
    tree = clipper.Execute2(pyclipper.CT_UNION, pyclipper.PFT_NONZERO, pyclipper.PFT_NONZERO)
    contours: list[tuple[list[tuple[int, int]], bool]] = []

    def walk(node) -> None:
        for child in node.Childs:
            if len(child.Contour) >= 3:
                contours.append((child.Contour, child.IsHole))
            walk(child)

    walk(tree)
    return contours


def build_glyph_contours(svg_path: Path) -> list[tuple[list[tuple[int, int]], bool]]:
    """Full per-SVG pipeline -> list of (clip-space contour, is_hole)."""
    regions: list[list[tuple[int, int]]] = []
    for d, attrs in _visible_paths(svg_path):
        subpaths = [sp for sp in _flatten_subpaths(d, FLATTEN_TOL) if len(sp) >= 2]
        fill = attrs.get("fill", "black")
        stroke = attrs.get("stroke", "none")
        if fill not in ("none", "transparent"):
            clip_polys = [p for p in (_to_clip(sp) for sp in subpaths) if len(p) >= 3]
            rule = (pyclipper.PFT_EVENODD if attrs.get("fill-rule") == "evenodd"
                    else pyclipper.PFT_NONZERO)
            regions.extend(_union(clip_polys, rule))
        if stroke not in ("none", "transparent"):
            width = float(attrs.get("stroke-width", "1"))
            linecap = attrs.get("stroke-linecap", "butt")
            for sp in subpaths:
                regions.extend(_stroke_outline(_to_clip(sp), width, linecap))
    if not regions:
        raise ValueError(f"{svg_path.name}: no visible geometry")
    contours = _contours_with_holes([r for r in regions if len(r) >= 3])
    simplified = [(_simplify_ring(c, SIMPLIFY_TOL), hole) for c, hole in contours]
    return [(c, hole) for c, hole in simplified if len(c) >= 3]


# --------------------------------------------------------------------------
# Font assembly

def _to_font(points: list[tuple[int, int]],
             center: tuple[float, float]) -> list[tuple[int, int]]:
    """Clip space -> font space: uniform 24-grid scaling (24 units == upm),
    y-flip, ink centered in the ADVANCE cell on the baseline. `center` is the
    source viewBox center in clip units (some hmos sources are 16x16 / 24x19,
    not 24x24). Horizontal: the pen draws ink relative to the run origin, and
    FontIcon centers the run box [0, ADVANCE] — so the ink must sit at
    ADVANCE/2, NOT at the origin (origin-centered ink renders ADVANCE/2 units
    left of the button center)."""
    scale = UPM / (VIEWBOX * CLIP_SCALE)
    cx, cy = center
    return [(int(round((x - cx) * scale + ADVANCE / 2)), int(round(-(y - cy) * scale))) for x, y in points]


def _signed_area(points: list[tuple[int, int]]) -> float:
    total = 0
    for (x0, y0), (x1, y1) in zip(points, points[1:] + points[:1]):
        total += (x0 * y1) - (x1 * y0)
    return total / 2


def _oriented(points: list[tuple[int, int]], is_hole: bool) -> list[tuple[int, int]]:
    """TrueType nonzero convention: outers clockwise (negative signed area in
    y-up coords), holes counter-clockwise."""
    area = _signed_area(points)
    if (is_hole and area < 0) or (not is_hole and area > 0):
        return list(reversed(points))
    return points


def _viewbox_center(svg_path: Path) -> tuple[float, float]:
    root = ET.parse(svg_path).getroot()
    x0, y0, w, h = (float(v) for v in root.get("viewBox").split())
    return ((x0 + w / 2) * CLIP_SCALE, (y0 + h / 2) * CLIP_SCALE)


def _font_contours(svg_path: Path) -> list[list[tuple[int, int]]]:
    center = _viewbox_center(svg_path)
    return [_oriented(_to_font(c, center), hole) for c, hole in build_glyph_contours(svg_path)]


def build_font() -> tuple[dict[str, int], dict[str, str]]:
    slots = sorted(SLOT_SVG)
    codepoints = {slot: FIRST_CP + i for i, slot in enumerate(slots)}
    glyph_of_slot = {slot: Path(SLOT_SVG[slot]).stem for slot in slots}
    glyph_names = sorted(set(glyph_of_slot.values()))

    contours: dict[str, list[list[tuple[int, int]]]] = {}
    for name in glyph_names:
        svg_path = SRC_DIR / f"{name}.svg"
        if not svg_path.is_file():
            raise FileNotFoundError(f"missing SVG source: {svg_path}")
        contours[name] = _font_contours(svg_path)

    glyph_order = [".notdef"] + glyph_names
    glyphs = {}
    metrics = {}
    for name in glyph_order:
        pen = TTGlyphPen(None)
        if name != ".notdef":
            for contour in contours[name]:
                pen.moveTo(contour[0])
                for point in contour[1:]:
                    pen.lineTo(point)
                pen.closePath()
        glyphs[name] = pen.glyph()
        if name == ".notdef":
            metrics[name] = (ADVANCE, 0)
        else:
            metrics[name] = (ADVANCE, min(p[0] for c in contours[name] for p in c))

    fb = FontBuilder(unitsPerEm=UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap({codepoints[slot]: glyph_of_slot[slot] for slot in slots})
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=EM_HALF, descent=-EM_HALF, lineGap=0)
    fb.setupNameTable({
        1: FAMILY,
        2: "Regular",
        3: f"{FAMILY};QNote;project-internal hmos editor icon set",
        4: FAMILY,
        5: "1.0",
        6: f"{FAMILY}-Regular",
    })
    fb.setupOS2(
        sTypoAscender=EM_HALF,
        sTypoDescender=-EM_HALF,
        sTypoLineGap=0,
        usWinAscent=EM_HALF,
        usWinDescent=EM_HALF,
        usWeightClass=400,
        usWidthClass=5,
        fsSelection=0x40,  # REGULAR
        fsType=0,
        ulUnicodeRange2=1 << 25,  # bit 57: Private Use Area
    )
    fb.setupPost(keepGlyphNames=False)
    fb.setupHead(fontRevision=1.0, unitsPerEm=UPM)
    fb.font["head"].created = HEAD_TIMESTAMP
    fb.font["head"].modified = HEAD_TIMESTAMP
    OUT_TTF.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(OUT_TTF))

    json_mapping = {slot: f"U+{codepoints[slot]:04X}" for slot in slots}
    OUT_JSON.write_text(json.dumps(json_mapping, indent=2) + "\n", encoding="utf-8")
    return codepoints, glyph_of_slot


def print_csharp_block(codepoints: dict[str, int], glyph_of_slot: dict[str, str]) -> None:
    slots = sorted(codepoints)
    print("// ---- paste into src/QNote/Controls/QNoteIconGlyphs.cs ----")
    for slot in slots:
        print(f"    public const int {slot} = 0x{codepoints[slot]:04X};")
    print()
    for slot in slots:
        print(f"        [\"{slot}\"] = {slot},")
    print(f"// {len(slots)} slots, {len(set(glyph_of_slot.values()))} glyphs")
    print(f"// font: {OUT_TTF} ({OUT_TTF.stat().st_size:,} bytes), "
          f"codepoints U+{codepoints[slots[0]]:04X}..U+{codepoints[slots[-1]]:04X}")


def main() -> None:
    codepoints, glyph_of_slot = build_font()
    print_csharp_block(codepoints, glyph_of_slot)


if __name__ == "__main__":
    main()
