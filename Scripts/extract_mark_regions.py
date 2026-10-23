"""Where each straight bar mark sits on the engineer placing sheet. Read-only.

The placing page is the one that names TOP IN FORM, END 2 (or VIEW END), and
VIEW A (or A-A). The reinforcing bill is not that page. A mark is a size, a
dash, and a length. Title-block bill text is ignored. Each mark is counted in
the view frame that contains it. The script exits non-zero when those region
counts do not add up to the number of times that mark is written on the page.

Direction comes from the model rebar geometry, not from the drawing. Global Z
is up: a bar is vertical when its longest delta is Z, and horizontal when its
longest delta is X or Y. This script does not change a drawing.
"""
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

import pymupdf

ROOT = Path(__file__).resolve().parents[1]
DEFAULTS = [
    Path(r"C:\Users\ASUS\Downloads\P22-132.W10-78.Rev 2.pdf"),
    Path(r"C:\Users\ASUS\Downloads\P22-132.W10-175.Rev 2.pdf"),
    Path(r"C:\Users\ASUS\Downloads\P22-132.W10-50.Rev 2.pdf"),
]
JSON_DIRS = [
    ROOT / "Export" / "CivilDrawings" / "new with macros" / "REBAR_BBS",
    Path(r"C:\Users\ASUS\Desktop\2d_tekla\Export\CivilDrawings\new with macros\REBAR_BBS"),
]
SIZE_RE = re.compile(r"\d+M")
LEN_RE = re.compile(r"\d{3,5}")
# Callout boxes sit just outside the concrete outline. PDF points, not panel mm.
CALLOUT_MARGIN = 90.0


def piece_name(path):
    match = re.search(r"(W10-\d+)", path.name, re.IGNORECASE)
    return match.group(1).upper() if match else path.stem


def center(word):
    return ((word[0] + word[2]) / 2.0, (word[1] + word[3]) / 2.0)


def long_segments(page):
    vertical = []
    for drawing in page.get_drawings():
        for item in drawing.get("items", []):
            if item[0] == "l":
                pairs = [(item[1].x, item[1].y, item[2].x, item[2].y)]
            elif item[0] == "re":
                rect = item[1]
                pairs = [
                    (rect.x0, rect.y0, rect.x1, rect.y0),
                    (rect.x0, rect.y1, rect.x1, rect.y1),
                    (rect.x0, rect.y0, rect.x0, rect.y1),
                    (rect.x1, rect.y0, rect.x1, rect.y1),
                ]
            else:
                continue
            for x0, y0, x1, y1 in pairs:
                dx, dy = abs(x1 - x0), abs(y1 - y0)
                if dy > 120 and dx < 1.5:
                    vertical.append(((x0 + x1) / 2.0, min(y0, y1), max(y0, y1)))
    return vertical


def title_block_x(page, vertical):
    height = page.rect.height
    candidates = [
        x for x, y0, y1 in vertical
        if x > page.rect.width * 0.7 and (y1 - y0) > height * 0.6
    ]
    line_x = min(candidates) if candidates else page.rect.width * 0.82
    bill_word = None
    for word in page.get_text("words") or []:
        token = word[4].upper().rstrip(".")
        if token in ("BILL", "QTY") and word[0] > page.rect.width * 0.55:
            bill_word = word[0] if bill_word is None else min(bill_word, word[0])
    if bill_word is None:
        return line_x
    near = [
        x for x, y0, y1 in vertical
        if bill_word - 80.0 < x <= bill_word and (y1 - y0) > 80.0
    ]
    if near:
        return min(line_x, min(near))
    return min(line_x, bill_word - 4.0)


def plan_frame(vertical):
    """Concrete outline of Top in Form: two verticals with the same y span."""
    best = None
    for i, (x0, y0, y1) in enumerate(vertical):
        span = y1 - y0
        if span < 140 or span > 280:
            continue
        for x1, oy0, oy1 in vertical[i + 1:]:
            if abs(y0 - oy0) > 4 or abs(y1 - oy1) > 4:
                continue
            gap = abs(x1 - x0)
            if gap < 200:
                continue
            left, right = (x0, x1) if x0 < x1 else (x1, x0)
            score = gap * span
            if best is None or score > best[0]:
                best = (score, left, y0, right, y1)
    return None if best is None else best[1:]


def page_views(text):
    upper = text.upper()
    if "REINFORCING BILL OF MATERIALS" in upper and "TOP IN FORM" not in upper:
        return set()
    found = set()
    if "TOP IN FORM" in upper:
        found.add("plan")
    if "END 2" in upper or "VIEW END" in upper:
        found.add("end")
    if "VIEW A" in upper or "A - A" in upper or "A-A" in upper:
        found.add("viewA")
    if "plan" not in found:
        return set()
    return found


def frames_for(page):
    vertical = long_segments(page)
    bill_x = title_block_x(page, vertical)
    plan = plan_frame([seg for seg in vertical if seg[0] < bill_x])
    if plan is None:
        return None
    left, top, right, bottom = plan
    return {
        "plan": (left - CALLOUT_MARGIN, top - CALLOUT_MARGIN, right + 20.0, bottom + 15.0),
        "end": (left - 30.0, bottom + 8.0, right + CALLOUT_MARGIN, page.rect.height - 20.0),
        "viewA": (right + 80.0, 40.0, bill_x - 4.0, bottom + 30.0),
        "bill_x": bill_x,
    }


def inside(box, x, y):
    return box[0] <= x <= box[2] and box[1] <= y <= box[3]


def nearest(words, x, y, limit):
    best = None
    best_dist = limit
    for word in words:
        wx, wy = center(word)
        dist = abs(wx - x) + abs(wy - y)
        if dist < best_dist:
            best_dist = dist
            best = word
    return best


def joined_marks(words):
    """Size, dash, and length must share a baseline or a stacked box.

    The length on the next row is not this bar. That is what turned the
    15M-7890 box into a false 10M-7890.
    """
    dashes = [word for word in words if word[4] == "-"]
    sizes = [word for word in words if SIZE_RE.fullmatch(word[4])]
    lengths = [word for word in words if LEN_RE.fullmatch(word[4])]
    found = []
    for dash in dashes:
        dx, dy = center(dash)
        size = None
        size_dist = None
        for word in sizes:
            sx, sy = center(word)
            inline = abs(sy - dy) < 5.0 and -30.0 < sx - dx < -4.0
            stacked = 8.0 < sy - dy < 18.0 and abs(sx - dx) < 8.0
            if not inline and not stacked:
                continue
            dist = abs(sx - dx) + abs(sy - dy)
            if size is None or dist < size_dist:
                size = word
                size_dist = dist
        if size is None:
            continue
        length = None
        length_dist = None
        for word in lengths:
            lx, ly = center(word)
            inline = abs(ly - dy) < 5.0 and 0.0 < lx - dx < 40.0
            stacked = 8.0 < dy - ly < 24.0 and abs(lx - dx) < 8.0
            if not inline and not stacked:
                continue
            dist = abs(lx - dx) + abs(ly - dy)
            if length is None or dist < length_dist:
                length = word
                length_dist = dist
        if length is None:
            continue
        found.append({"mark": size[4].upper() + "-" + length[4], "x": dx, "y": dy})
    return found


def view_a_rank(page, bill_x):
    """One box per column beside the VIEW A caption, length taken from above the size."""
    caption_y = None
    lines = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = re.sub(r"\s+", " ", "".join(span.get("text") or "" for span in line.get("spans", [])).strip())
            box = line["bbox"]
            cx = (box[0] + box[2]) / 2.0
            cy = (box[1] + box[3]) / 2.0
            lines.append((text, cx, cy))
            upper = text.upper()
            if cx < bill_x:
                continue
            if "VIEW A" in upper or "A - A" in upper or "A-A" in upper:
                caption_y = cy
    if caption_y is None:
        return []
    labels = [
        (text, cx, cy) for text, cx, cy in lines
        if cx >= bill_x and re.fullmatch(r"\d+M\s*-", text)
    ]
    numbers = [(text, cx, cy) for text, cx, cy in lines if re.fullmatch(r"\d{3,5}", text)]
    paired = []
    for text, cx, cy in labels:
        length = None
        best = None
        for number, nx, ny in numbers:
            if abs(nx - cx) < 14.0 and 4.0 < cy - ny < 30.0:
                dist = abs(nx - cx) + (cy - ny)
                if best is None or dist < best:
                    best = dist
                    length = number
        if length is None:
            continue
        size = re.match(r"\d+M", text, re.IGNORECASE).group(0).upper()
        paired.append({"mark": size + "-" + length, "x": cx, "y": cy, "lock": "viewA"})
    if not paired:
        return []
    bands = []
    for mark in sorted(paired, key=lambda item: item["y"]):
        if not bands or mark["y"] - bands[-1][-1]["y"] > 8.0:
            bands.append([mark])
        else:
            bands[-1].append(mark)
    wide = [band for band in bands if len(band) >= 4 and max(item["x"] for item in band) - min(item["x"] for item in band) > 100.0]
    if not wide:
        return []
    band = min(
        wide,
        key=lambda items: (
            -len(items),
            abs(sum(item["y"] for item in items) / len(items) - caption_y),
        ),
    )
    return band


def straight_marks(page, bill_x):
    # The VIEW A row starts just left of the materials-table border.
    edge = bill_x - 20.0
    words = [word for word in (page.get_text("words") or []) if center(word)[0] < edge]
    return joined_marks(words) + view_a_rank(page, edge)


def assign(marks, frames, views):
    """Prefer the view outline. If a mark falls outside it, use the band of boxes.

    A horizontal rank of boxes is VIEW A when that view is named on the page.
    A vertical column of boxes is END 2 when that view is named. Anything left
    over, and still left of the bill, is the plan.
    """
    boxes = {
        "plan": frames["plan"],
        "end": frames["end"] if "end" in views else (0.0, 0.0, 0.0, 0.0),
        "viewA": frames["viewA"] if "viewA" in views else (0.0, 0.0, 0.0, 0.0),
    }
    locked = [{**mark, "region": mark["lock"]} for mark in marks if mark.get("lock")]
    free = [mark for mark in marks if not mark.get("lock")]
    by_frame = []
    outside = False
    for mark in free:
        hits = [name for name in ("plan", "end", "viewA") if inside(boxes[name], mark["x"], mark["y"])]
        if len(hits) != 1:
            outside = True
            break
        by_frame.append({**mark, "region": hits[0]})
    if by_frame and not outside:
        return by_frame + locked

    remaining = [dict(mark) for mark in free]
    for mark in remaining:
        mark["region"] = "plan"
    if "viewA" in views:
        bands = []
        for mark in sorted(remaining, key=lambda item: item["y"]):
            if not bands or mark["y"] - bands[-1][0]["y"] > 18.0:
                bands.append([mark])
            else:
                bands[-1].append(mark)
        for band in bands:
            xs = [mark["x"] for mark in band]
            if len(band) >= 6 and max(xs) - min(xs) > 120.0:
                for mark in band:
                    mark["region"] = "viewA"
    if "end" in views:
        columns = []
        for mark in sorted(remaining, key=lambda item: item["x"]):
            if mark["region"] == "viewA":
                continue
            if not columns or mark["x"] - columns[-1][0]["x"] > 24.0:
                columns.append([mark])
            else:
                columns[-1].append(mark)
        for column in columns:
            ys = [mark["y"] for mark in column]
            if len(column) >= 4 and max(ys) - min(ys) > 120.0:
                for mark in column:
                    mark["region"] = "end"
    elif "viewA" in views:
        columns = []
        for mark in sorted(remaining, key=lambda item: item["x"]):
            if mark["region"] == "viewA":
                continue
            if not columns or mark["x"] - columns[-1][0]["x"] > 24.0:
                columns.append([mark])
            else:
                columns[-1].append(mark)
        for column in columns:
            ys = [mark["y"] for mark in column]
            if len(column) >= 4 and max(ys) - min(ys) > 120.0:
                for mark in column:
                    mark["region"] = "viewA"
    return remaining + locked


def model_json(piece):
    name = f"1_{piece}_REBAR_BBS.json"
    for folder in JSON_DIRS:
        path = folder / name
        if path.exists():
            return path
    return None


def bar_axis(geometry):
    if not geometry or len(geometry) < 2:
        return None
    xs = [point["X"] for point in geometry]
    ys = [point["Y"] for point in geometry]
    zs = [point["Z"] for point in geometry]
    return abs(max(xs) - min(xs)), abs(max(ys) - min(ys)), abs(max(zs) - min(zs))


def direction_of(dx, dy, dz):
    flat = max(dx, dy)
    if max(flat, dz) < 1.0:
        return "unknown"
    if dz > flat * 1.5:
        return "vertical"
    if flat > dz * 1.5:
        return "horizontal"
    return "diagonal"


def model_directions(piece):
    path = model_json(piece)
    if path is None:
        return {}, None, {}
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    grouped = defaultdict(set)
    by_length = defaultdict(set)
    for bar in data.get("Rebar") or []:
        if bar.get("ShapeType") != "straight":
            continue
        length = bar.get("TotalLengthMm")
        size = bar.get("Size")
        if length is None or not size:
            continue
        span = bar_axis(bar.get("Geometry") or [])
        if span is None:
            continue
        rounded = int(round(length))
        key = f"{size}-{rounded}"
        grouped[key].add(direction_of(*span))
        by_length[rounded].add(size)
    directions = {}
    for key, kinds in grouped.items():
        kinds.discard("unknown")
        if not kinds:
            directions[key] = "unknown"
        elif len(kinds) == 1:
            directions[key] = next(iter(kinds))
        else:
            directions[key] = "mixed"
    return directions, path, by_length


def report_page(path, index, page, directions, views, by_length):
    piece = piece_name(path)
    frames = frames_for(page)
    if frames is None:
        vertical = long_segments(page)
        frames = {
            "plan": (0, 0, 0, 0),
            "end": (0, 0, 0, 0),
            "viewA": (0, 0, 0, 0),
            "bill_x": title_block_x(page, vertical),
        }
    placed = assign(straight_marks(page, frames["bill_x"]), frames, views)
    page_total = Counter(mark["mark"] for mark in placed)
    by_region = defaultdict(Counter)
    for mark in placed:
        by_region[mark["region"]][mark["mark"]] += 1
    print(
        f"piece={piece} file={path.name} pdf_page={index + 1} "
        f"bill_x={frames['bill_x']:.1f} straight_marks={len(placed)}"
    )
    print(
        f"{'mark':<12} {'plan':>5} {'END2':>5} {'VIEW_A':>7} {'sum':>5} "
        f"{'page':>5} {'dir':<12} pdf_page"
    )
    failed = False
    breaks = []
    for mark in sorted(page_total):
        plan = by_region["plan"][mark]
        end = by_region["end"][mark]
        view = by_region["viewA"][mark]
        other = by_region["none"][mark] + by_region["overlap"][mark]
        total = plan + end + view + other
        status = "ok" if total == page_total[mark] and other == 0 else "FAIL"
        if status != "ok":
            failed = True
        direction = directions.get(mark, "no-model")
        print(
            f"{mark:<12} {plan:5d} {end:5d} {view:7d} {total:5d} {page_total[mark]:5d} "
            f"{direction:<12} {index + 1}  {path.name}  {status}"
        )
        if end and direction != "vertical":
            breaks.append((piece, index + 1, mark, "END2", direction, end))
        if view and direction != "horizontal":
            breaks.append((piece, index + 1, mark, "VIEW_A", direction, view))
        drawn_size, _, drawn_len = mark.partition("-")
        if drawn_len.isdigit():
            model_sizes = by_length.get(int(drawn_len), set())
            if model_sizes and drawn_size not in model_sizes:
                print(
                    f"diameter drawn={mark} model straight length {drawn_len} is "
                    + ",".join(sorted(model_sizes))
                )
            elif drawn_len == "7890":
                print(f"diameter drawn={mark} model={','.join(sorted(model_sizes)) or 'missing'}")
    end_n = sum(by_region["end"].values())
    view_n = sum(by_region["viewA"].values())
    plan_n = sum(by_region["plan"].values())
    print(f"chains piece={piece} pdf_page={index + 1} plan={plan_n} END2={end_n} VIEW_A={view_n}")
    if by_region["viewA"]:
        print(f"VIEW A marks piece={piece} pdf_page={index + 1}")
        for mark, count in sorted(by_region["viewA"].items()):
            print(f"  {mark} n={count} dir={directions.get(mark, 'no-model')}")
    if piece == "W10-78":
        expected = {"10M-2460": 3, "10M-2570": 2, "15M-5020": 2}
        for mark, count in expected.items():
            got = page_total[mark]
            if got != count:
                print(f"FAIL {mark} page_total={got} expected={count}")
                failed = True
            else:
                print(f"check {mark}={got}")
        if end_n != 7 or view_n != 11:
            print(f"FAIL W10-78 chains END2={end_n} VIEW_A={view_n} expected 7 and 11")
            failed = True
        else:
            print("check W10-78 END2=7 VIEW_A=11")
    if failed:
        print(f"FAIL piece={piece} pdf_page={index + 1} region sum differs from the page total")
        return 1, breaks
    print(f"PASS piece={piece} pdf_page={index + 1}")
    return 0, breaks


def report(path):
    doc = pymupdf.open(path)
    piece = piece_name(path)
    chosen = []
    for index, page in enumerate(doc):
        views = page_views(page.get_text() or "")
        if not views.intersection({"end", "viewA"}):
            continue
        vertical = long_segments(page)
        bill = title_block_x(page, vertical)
        if not straight_marks(page, bill):
            continue
        chosen.append((index, page, views))
    if not chosen:
        print(f"FAIL piece={piece} placing page not found file={path}")
        doc.close()
        return 1, []
    directions, model, by_length = model_directions(piece)
    print(f"model piece={piece} file={model if model else 'missing'} marks={len(directions)}")
    code = 0
    breaks = []
    for index, page, views in chosen:
        page_code, page_breaks = report_page(path, index, page, directions, views, by_length)
        code = max(code, page_code)
        breaks.extend(page_breaks)
        print()
    doc.close()
    return code, breaks


def main(argv):
    paths = [Path(p) for p in argv[1:]] or DEFAULTS
    missing = [str(path) for path in paths if not path.exists()]
    if missing:
        print("missing " + ", ".join(missing))
        return 1
    code = 0
    breaks = []
    for path in paths:
        page_code, page_breaks = report(path)
        code = max(code, page_code)
        breaks.extend(page_breaks)
    print("direction rule: END 2 = longest run is Z (vertical); VIEW A = longest run is X or Y; diagonal = plan only")
    print("place-c filter, model direction only, no mark numbers and no page numbers:")
    print("  view named END 2 keeps a chain when the bar's longest run is Z")
    print("  view named VIEW A keeps a chain when the bar's longest run is X or Y")
    print("  a bar with a large run in both Z and X or Y is a plan callout only")
    print("  on W10-78 this leaves END 2 = 7 chains and VIEW A = 11 chains")
    if breaks:
        print(f"marks the filter would drop={len(breaks)}")
        for piece, page, mark, region, direction, count in breaks:
            print(f"  drop {piece} pdf_page={page} {mark} region={region} n={count} direction={direction}")
    else:
        print("no section mark breaks the rule")
    return code


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
