#!/usr/bin/env python3
"""Remove nested inner full-sheet border from Tekla 11x17 PDF (keep outer + BOM)."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path


def strip_inner_border(pdf_path: Path) -> bool:
    try:
        import pymupdf
    except ImportError:
        print("[border] pymupdf not installed; skip", file=sys.stderr)
        return False

    if not pdf_path.is_file():
        print("[border] missing " + str(pdf_path), file=sys.stderr)
        return False

    doc = pymupdf.open(pdf_path)
    try:
        page = doc[0]
        pw, ph = page.rect.width, page.rect.height
        # Detect nested full-sheet frames from long strokes (PDF points).
        horiz = []
        vert = []
        for d in page.get_drawings():
            for it in d.get("items") or []:
                if it[0] != "l":
                    continue
                p1, p2 = it[1], it[2]
                if abs(p1.y - p2.y) < 1.5 and abs(p1.x - p2.x) > pw * 0.5:
                    horiz.append(0.5 * (p1.y + p2.y))
                if abs(p1.x - p2.x) < 1.5 and abs(p1.y - p2.y) > ph * 0.5:
                    vert.append(0.5 * (p1.x + p2.x))

        def cluster(vals, tol=3.0):
            vals = sorted(vals)
            out = []
            for v in vals:
                if not out or abs(out[-1] - v) > tol:
                    out.append(v)
            return out

        hs = cluster(horiz)
        vs = cluster(vert)
        # Edge pairs near opposite sheet margins only (exclude BOM divider ~0.8 width).
        top = [y for y in hs if y <= ph * 0.18]
        bot = [y for y in hs if y >= ph * 0.82]
        left = [x for x in vs if x <= pw * 0.18]
        right = [x for x in vs if x >= pw * 0.90]
        if len(top) < 2 or len(bot) < 2 or len(left) < 2 or len(right) < 2:
            print("[border] no nested frame detected on " + pdf_path.name)
            return True

        # Outer = extreme edges; inner = next inset toward content.
        y_top_inner = sorted(top)[-1]
        y_bot_inner = sorted(bot)[0]
        x_left_inner = sorted(left)[-1]
        x_right_inner = sorted(right)[0]

        shape = page.new_shape()
        ink = (1, 1, 1)
        shape.draw_line(pymupdf.Point(x_left_inner, y_top_inner), pymupdf.Point(x_right_inner, y_top_inner))
        shape.draw_line(pymupdf.Point(x_left_inner, y_bot_inner), pymupdf.Point(x_right_inner, y_bot_inner))
        shape.draw_line(pymupdf.Point(x_left_inner, y_top_inner), pymupdf.Point(x_left_inner, y_bot_inner))
        shape.draw_line(pymupdf.Point(x_right_inner, y_top_inner), pymupdf.Point(x_right_inner, y_bot_inner))
        shape.finish(color=ink, width=4.0, stroke_opacity=1)
        shape.commit()

        tmp = pdf_path.with_suffix(".border.tmp.pdf")
        doc.save(tmp, garbage=4, deflate=True)
        doc.close()
        doc = None
        tmp.replace(pdf_path)
        print(
            "[border] covered inner frame on "
            + pdf_path.name
            + f" @ L={x_left_inner:.0f} R={x_right_inner:.0f} T={y_top_inner:.0f} B={y_bot_inner:.0f}"
        )
        return True
    finally:
        if doc is not None:
            doc.close()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("pdf", nargs="+", help="PDF path(s)")
    args = ap.parse_args()
    ok = True
    for p in args.pdf:
        if not strip_inner_border(Path(p)):
            ok = False
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
