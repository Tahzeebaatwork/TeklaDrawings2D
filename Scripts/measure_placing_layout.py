"""Paper pitches on the engineer placing sheet. Read-only.

The placing page is the booklet page with TOP IN FORM and the bar marks.
Lengths are PDF points times 25.4/72. Nothing here is written into src/.
"""
import re
import sys
from pathlib import Path

import pymupdf

PT_TO_MM = 25.4 / 72.0
DRAWING_X = 0.78
DEFAULTS = [
    Path(r"Export/CivilDrawings/new with macros/PDF/P22-132.W10-78.Rev 2.pdf"),
    Path(r"Export/CivilDrawings/new with macros/PDF/P22-132.W10-175.Rev 2.pdf"),
]


def median(values):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[len(ordered) // 2]


def line_text(line):
    return "".join(span.get("text") or "" for span in line.get("spans", [])).strip()


def placing_page(doc):
    best = None
    for index, page in enumerate(doc):
        text = (page.get_text() or "").upper()
        if "REINFORCING BILL" in text or "TOP IN FORM" not in text:
            continue
        count = text.count("10M") + text.count("15M") + text.count("RB")
        if best is None or count > best[0]:
            best = (count, index, page)
    return None if best is None else (best[1], best[2])


def captions(page):
    found = []
    limit = page.rect.width * DRAWING_X
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = line_text(line).upper()
            kind = None
            if "TOP IN FORM" in text:
                kind = "plan"
            elif "END" in text:
                kind = "end"
            elif "VIEW A" in text or "A-A" in text:
                kind = "viewA"
            elif "VIEW B" in text:
                kind = "viewB"
            if kind is None:
                continue
            box = line["bbox"]
            cx = (box[0] + box[2]) / 2.0
            if cx > limit:
                continue
            found.append((kind, cx, (box[1] + box[3]) / 2.0, float(line["spans"][0].get("size") or 0)))
    return found


def mark_boxes(page):
    rows = []
    for block in page.get_text("dict")["blocks"]:
        blob = " ".join(line_text(line) for line in block.get("lines", []))
        if not re.search(r"\d+M\s*-", blob, re.IGNORECASE) and not re.search(r"RB\d+M", blob, re.IGNORECASE):
            continue
        box = block["bbox"]
        rows.append(box)
    return rows


def assign_boxes(boxes, views, page_width):
    limit = page_width * DRAWING_X
    plan_ys = [v[2] for v in views if v[0] == "plan"]
    plan_bottom = (max(plan_ys) + 20.0) if plan_ys else 0.0
    section = [v[0] for v in views if v[0] in ("viewA", "viewB")]
    right_name = section[-1] if section else "viewA"
    centers = []
    for box in boxes:
        mx = (box[0] + box[2]) / 2.0
        my = (box[1] + box[3]) / 2.0
        if mx > limit:
            continue
        centers.append((mx, my, box))
    lower = sorted((my for _, my, _ in centers if my >= plan_bottom))
    bands = []
    for y in lower:
        if not bands or y - bands[-1][0] > 6.0:
            bands.append([y, 1])
        else:
            bands[-1][1] += 1
    header_y = [y for y, n in bands if n >= 6]
    out = {}
    for mx, my, box in centers:
        if my < plan_bottom:
            region = "plan"
        elif any(abs(my - y) <= 18.0 for y in header_y):
            region = right_name
        else:
            region = "end"
        out.setdefault(region, []).append(box)
    return out


def cluster_centers(values, tol):
    groups = []
    for value in sorted(values):
        if not groups or value - groups[-1][-1] > tol:
            groups.append([value])
        else:
            groups[-1].append(value)
    return [sum(g) / len(g) for g in groups]


def gaps(centers, lo, hi):
    ordered = sorted(centers)
    out = []
    for i in range(len(ordered) - 1):
        gap = ordered[i + 1] - ordered[i]
        if lo < gap < hi:
            out.append(gap)
    return out


def mm(value):
    return "n/a" if value is None else f"{value * PT_TO_MM:.2f}"


def piece_name(path):
    match = re.search(r"(W10-\d+)", path.name, re.IGNORECASE)
    return match.group(1).upper() if match else path.stem


def report(path):
    doc = pymupdf.open(path)
    chosen = placing_page(doc)
    piece = piece_name(path)
    if chosen is None:
        print(f"piece={piece} placing page not found")
        doc.close()
        return
    index, page = chosen
    views = captions(page)
    boxes = [b for b in mark_boxes(page) if (b[0] + b[2]) / 2.0 <= page.rect.width * DRAWING_X]
    by_region = assign_boxes(boxes, views, page.rect.width)
    sizes = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            for span in line.get("spans", []):
                size = float(span.get("size") or 0)
                text = (span.get("text") or "").strip()
                if size > 0 and (re.search(r"\d+M", text) or text.isdigit()):
                    sizes.append(size)
    heights = [b[3] - b[1] for b in boxes]
    end_y = [(b[1] + b[3]) / 2.0 for b in by_region.get("end", [])]
    view_x = [(b[0] + b[2]) / 2.0 for b in by_region.get("viewA", [])]
    plan = [v for v in views if v[0] == "plan"]
    plan_y = max((v[2] for v in plan), default=None)
    dim_rows = []
    if plan_y is not None:
        limit = page.rect.width * DRAWING_X
        for block in page.get_text("dict")["blocks"]:
            for line in block.get("lines", []):
                text = line_text(line)
                box = line["bbox"]
                if box[0] > limit or box[1] > plan_y - 8:
                    continue
                if re.search(r"\d", text) and not re.search(r"[A-Za-z]", text):
                    dim_rows.append((box[1] + box[3]) / 2.0)
        dim_rows = sorted(set(round(y, 0) for y in dim_rows))
    nearest = None
    if plan_y is not None and dim_rows:
        above = [y for y in dim_rows if y < plan_y]
        if above:
            nearest = plan_y - above[-1]
    end_rows = cluster_centers(end_y, 8.0)
    view_rows = cluster_centers(view_x, 4.0)
    print(f"piece={piece} placing_pdf_page={index + 1}")
    print(f"page_mm={page.rect.width * PT_TO_MM:.2f}x{page.rect.height * PT_TO_MM:.2f}")
    print(f"text_height_mm={mm(median(sizes))}")
    print(f"box_height_mm={mm(median(heights))}")
    print(f"end_row_pitch_mm={mm(median(gaps(end_rows, 10.0, 50.0)))} rows={len(end_rows)}")
    print(f"viewA_chain_pitch_mm={mm(median(gaps(view_rows, 4.0, 40.0)))} chains={len(view_rows)}")
    print(f"plan_rows={len(by_region.get('plan', []))} plan_to_dim_gap_mm={mm(nearest)}")
    doc.close()


def main(argv):
    paths = [Path(p) for p in argv[1:]] or DEFAULTS
    missing = [str(p) for p in paths if not p.exists()]
    if missing:
        print("missing " + ", ".join(missing))
        return 1
    for path in paths:
        report(path)
        print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
