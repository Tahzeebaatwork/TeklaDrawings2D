"""Paper layout of an engineer page-1 PDF. Read-only.

Prints the PDF page box as measured. Does not assume 17x11 or 18x12.
Digit width is glyph width / font size. Row and column pitch are the median
gaps of dimension numbers, converted with 25.4/72 from the PDF's own points.
"""
import sys
from collections import Counter
from pathlib import Path

import pymupdf

PT_TO_MM = 25.4 / 72.0


def cluster(values, tol):
    groups = []
    for value in sorted(values):
        if not groups or value - groups[-1][-1] > tol:
            groups.append([value])
        else:
            groups[-1].append(value)
    return [sum(g) / len(g) for g in groups]


def median(values):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def main() -> int:
    pdf = Path(sys.argv[1] if len(sys.argv) > 1 else
               r"Export/CivilDrawings/new with macros/PDF/P22-132.W10-78.Rev 2.pdf")
    doc = pymupdf.open(pdf)
    page = doc[0]
    page_w, page_h = page.rect.width, page.rect.height
    widths = []
    sizes = Counter()
    fonts = Counter()
    digit_y = []
    digit_x_band = []
    paint = "PAINT LEGEND" in (page.get_text() or "").upper()
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                text = (span.get("text") or "").strip()
                size = float(span.get("size") or 0)
                if size <= 0 or not text.isdigit():
                    continue
                bb = span.get("bbox") or line.get("bbox")
                if not bb:
                    continue
                w = float(bb[2]) - float(bb[0])
                if w <= 0:
                    continue
                sizes[round(size, 2)] += 1
                fonts[span.get("font") or ""] += 1
                widths.append(w / size / max(1, len(text)))
                if len(text) >= 3:
                    cy = (float(bb[1]) + float(bb[3])) / 2.0
                    cx = (float(bb[0]) + float(bb[2])) / 2.0
                    if 60.0 < cy < page_h - 80.0:
                        digit_y.append(cy)
                    if 350.0 < cy < 430.0:
                        digit_x_band.append(cx)
    doc.close()
    if not widths:
        print("no digit glyphs")
        return 1
    row_centers = cluster(digit_y, 4.0)
    row_gaps = [row_centers[i + 1] - row_centers[i] for i in range(len(row_centers) - 1)]
    ladder = [g for g in row_gaps if 20.0 < g < 40.0]
    col_centers = cluster(digit_x_band, 4.0)
    col_gaps = [col_centers[i + 1] - col_centers[i] for i in range(len(col_centers) - 1)]
    col_tight = [g for g in col_gaps if g < 40.0]
    row_mm = median(ladder)
    col_mm = median(col_tight)
    font = sizes.most_common(1)[0][0]
    print(f"page_pt={page_w:.1f}x{page_h:.1f}")
    print(f"page_mm={page_w * PT_TO_MM:.2f}x{page_h * PT_TO_MM:.2f}")
    print(f"pdf_font={fonts.most_common(1)[0][0] if fonts else '?'}")
    print(f"font_pt={font:.2f}")
    print(f"digit_width_over_font={median(widths):.2f}")
    print(f"row_pitch_mm={row_mm * PT_TO_MM:.2f}" if row_mm else "row_pitch_mm=n/a")
    print(f"column_pitch_mm={col_mm * PT_TO_MM:.2f}" if col_mm else "column_pitch_mm=n/a")
    print(f"paint_legend={'yes' if paint else 'no'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
