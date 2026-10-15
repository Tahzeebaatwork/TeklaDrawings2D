"""Where each boxed bar mark sits on the engineer placing sheet. Read-only.

Picks the booklet page that has TOP IN FORM and the bar marks (the placing
sheet), not the page number in the title block. Joins each mark to the
reinforcing bill in the same booklet. Prints one row per mark and region.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

import pymupdf

DRAWING_X = 0.78
COUNT_RE = re.compile(
    r"(T\.\s*&\s*B\.|x\s*,\s*\d+\s*x|\b\d+\s*x\b)",
    re.IGNORECASE,
)
DEFAULTS = [
    Path(r"Export/CivilDrawings/new with macros/PDF/P22-132.W10-78.Rev 2.pdf"),
    Path(r"Export/CivilDrawings/new with macros/PDF/P22-132.W10-175.Rev 2.pdf"),
]


def line_text(line):
    return "".join(span.get("text") or "" for span in line.get("spans", [])).strip()


def captions(page):
    found = []
    limit = page.rect.width * DRAWING_X
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = line_text(line)
            upper = text.upper()
            kind = None
            if "TOP IN FORM" in upper:
                kind = "plan"
            elif "VIEW END" in upper or re.search(r"\bEND\s*[12]\b", upper):
                kind = "end"
            elif "VIEW B" in upper:
                kind = "viewB"
            elif "VIEW A" in upper or re.search(r"\bA-A\b", upper):
                kind = "viewA"
            if kind is None:
                continue
            box = line["bbox"]
            cx = (box[0] + box[2]) / 2.0
            if cx > limit:
                continue
            cy = (box[1] + box[3]) / 2.0
            found.append((kind, cx, cy))
    return found


def parse_mark(blob):
    bent = re.search(r"RB\d+M\s+\d+", blob, re.IGNORECASE)
    if bent:
        return re.sub(r"\s+", " ", bent.group(0).upper())
    size = re.search(r"(\d+M)\s*-", blob, re.IGNORECASE)
    if size is None:
        return None
    lengths = [int(n) for n in re.findall(r"\b(\d{3,5})\b", blob)]
    if not lengths:
        return None
    return size.group(1).upper() + " - " + str(max(lengths))


def classify_marks(marks, views, page_width):
    """Plan is above its caption. A horizontal rank of boxes is VIEW A. The rest below is END."""
    limit = page_width * DRAWING_X
    kept = [m for m in marks if m["x"] <= limit]
    plan_ys = [v[2] for v in views if v[0] == "plan"]
    plan_bottom = (max(plan_ys) + 20.0) if plan_ys else 0.0
    section = [v[0] for v in views if v[0] in ("viewA", "viewB")]
    right_name = section[-1] if section else "viewA"
    lower = [m for m in kept if m["y"] >= plan_bottom]
    bands = []
    for mark in sorted(lower, key=lambda m: m["y"]):
        if not bands or mark["y"] - bands[-1][0] > 6.0:
            bands.append([mark["y"], [mark]])
        else:
            bands[-1][1].append(mark)
    header_y = [y for y, group in bands if len(group) >= 6]
    out = {}
    for mark in kept:
        if mark["y"] < plan_bottom:
            region = "plan"
        elif any(abs(mark["y"] - y) <= 18.0 for y in header_y):
            region = right_name
        else:
            region = "end"
        out.setdefault(region, []).append(mark)
    return out


def mark_blocks(page):
    rows = []
    for block in page.get_text("dict")["blocks"]:
        lines = []
        for line in block.get("lines", []):
            text = line_text(line)
            if text:
                lines.append((line["bbox"], text))
        if not lines:
            continue
        blob = " ".join(text for _, text in lines)
        mark = parse_mark(blob)
        if mark is None:
            continue
        box = block["bbox"]
        count = ""
        counted = COUNT_RE.search(blob)
        if counted:
            count = re.sub(r"\s+", " ", counted.group(1))
        rows.append({
            "mark": mark,
            "x": (box[0] + box[2]) / 2.0,
            "y": (box[1] + box[3]) / 2.0,
            "box": box,
            "count": count,
        })
    return rows


def chain_direction(page, mark):
    digits = []
    x0, y0, x1, y1 = mark["box"]
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = line_text(line)
            if not re.fullmatch(r"[\d ]{2,}", text):
                continue
            box = line["bbox"]
            cx = (box[0] + box[2]) / 2.0
            cy = (box[1] + box[3]) / 2.0
            if abs(cx - mark["x"]) < 90.0 and abs(cy - mark["y"]) < 70.0:
                if y0 - 4 <= cy <= y1 + 4 and x0 - 4 <= cx <= x1 + 4:
                    continue
                digits.append((cx, cy))
    if len(digits) < 2:
        return "unknown"
    xs = [p[0] for p in digits]
    ys = [p[1] for p in digits]
    span_x = max(xs) - min(xs)
    span_y = max(ys) - min(ys)
    if span_x > span_y * 1.4:
        return "length"
    if span_y > span_x * 1.4:
        return "height"
    return "unknown"


def placing_page(doc):
    best = None
    for index, page in enumerate(doc):
        text = page.get_text() or ""
        upper = text.upper()
        if "REINFORCING BILL" in upper:
            continue
        if "TOP IN FORM" not in upper:
            continue
        count = len(mark_blocks(page))
        if best is None or count > best[0]:
            best = (count, index, page)
    return None if best is None else (best[1], best[2])


def bbs_page(doc):
    for index, page in enumerate(doc):
        if "REINFORCING BILL" in (page.get_text() or "").upper():
            return index, page
    return None


def parse_bbs(page):
    lines = [ln.strip() for ln in (page.get_text() or "").splitlines()]
    lines = [ln for ln in lines if ln]
    rows = {}
    i = 0
    while i < len(lines):
        bent = re.fullmatch(r"RB\d+M\s+\d+", lines[i], re.IGNORECASE)
        straight = re.fullmatch(r"\d+M", lines[i], re.IGNORECASE)
        if bent and i + 4 < len(lines) and re.fullmatch(r"\d+M", lines[i + 1], re.IGNORECASE):
            mark = re.sub(r"\s+", " ", lines[i].upper())
            shape = lines[i + 2]
            qty = lines[i + 3]
            length = lines[i + 4]
            if qty.isdigit() and re.fullmatch(r"[\d.]+", length):
                rows[mark] = (shape, qty, length)
                i += 5
                continue
        if straight and i + 4 < len(lines) and lines[i + 1].upper() == "STR":
            size = lines[i].upper()
            qty = lines[i + 2]
            length = lines[i + 3]
            if qty.isdigit() and length.isdigit():
                mark = size + " - " + length
                rows[mark] = ("STR", qty, length)
                i += 5
                continue
        i += 1
    return rows


def piece_name(path):
    match = re.search(r"(W10-\d+)", path.name, re.IGNORECASE)
    return match.group(1).upper() if match else path.stem


def report(path):
    doc = pymupdf.open(path)
    chosen = placing_page(doc)
    bill = bbs_page(doc)
    piece = piece_name(path)
    if chosen is None:
        print(f"piece={piece} placing page not found in {path}")
        doc.close()
        return
    index, page = chosen
    views = captions(page)
    bbs = parse_bbs(bill[1]) if bill else {}
    placed = classify_marks(mark_blocks(page), views, page.rect.width)
    print(f"piece={piece} placing_pdf_page={index + 1} captions={len(views)} bbs_rows={len(bbs)} file={path.name}")
    grouped = defaultdict(lambda: {"n": 0, "counts": set(), "chains": set()})
    for region, marks in placed.items():
        for mark in marks:
            key = (mark["mark"], region)
            bucket = grouped[key]
            bucket["n"] += 1
            if mark["count"]:
                bucket["counts"].add(mark["count"])
            bucket["chains"].add(chain_direction(page, mark))
    print(f"{'mark':<16} {'region':<8} {'n':>3} {'count':<12} {'chain':<10} {'bbs_shape':<10} {'bbs_qty':>7} {'bbs_len':>8}")
    for mark, region in sorted(grouped):
        bucket = grouped[(mark, region)]
        chains = ",".join(sorted(c for c in bucket["chains"] if c != "unknown")) or "unknown"
        counts = ",".join(sorted(bucket["counts"])) or "-"
        shape, qty, length = bbs.get(mark, ("-", "-", "-"))
        print(f"{mark:<16} {region:<8} {bucket['n']:3d} {counts:<12} {chains:<10} {shape:<10} {qty:>7} {length:>8}")
    by_mark = defaultdict(set)
    for mark, region in grouped:
        by_mark[mark].add(region)
    both = sorted(mark for mark, regions in by_mark.items() if len(regions) > 1)
    only = defaultdict(list)
    for mark, regions in by_mark.items():
        if len(regions) == 1:
            only[next(iter(regions))].append(mark)
    print(f"summary piece={piece} marks={len(by_mark)} in_two_regions={len(both)}")
    if both:
        print("  two_regions " + ", ".join(both))
    for region in ("plan", "end", "viewA", "viewB"):
        names = sorted(only.get(region, []))
        print(f"  only_{region} ({len(names)}) " + ", ".join(names))
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
