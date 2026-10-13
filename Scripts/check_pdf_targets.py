"""Score exported shop-sheet PDFs against tests/regression fixtures (read-only).

The Open API cannot read rendered template fields or native combined-dimension text,
so these targets are checked from the PDFs:

  page1     rotated cumulative (absolute) numbers for each fixture row; one PDF row =
            rotated numbers sharing a baseline. Also every <stem>__dimtype-<t>.pdf trial.
  heights   each row's vertical height column (fixture heights[], either direction): horizontal
            numbers outside the panel sharing an aligned edge; reports whether the 0 printed
  sections  fixture sections[] numbers on the sections sheet (page 1 when there is none)
  rebar     fixture rebar spacing strings (n@s=total) and callouts on placing/BBS sheets
  title     fixture title{} strings inside the title-block column

Numbers are compared with digit-grouping spaces removed ("1 800" == "1800").
Writes PASS / PASS-within-1 / KNOWN-DEVIATION / FAIL / MISSING per target and appends one
line per check to regression_scores.csv with set=pdf:<check>.

  python Scripts/check_pdf_targets.py --piece W10-78
  python Scripts/check_pdf_targets.py --pdf-dir "Export/CivilDrawings/PDF" --run-tag run2
  python Scripts/check_pdf_targets.py --piece W10-78 --expect tests/regression/tuning/W10-78.json
"""
from __future__ import annotations

import argparse
import csv
import datetime as dt
import json
import re
import sys
from collections import Counter
from pathlib import Path

try:
    import pymupdf as fitz
except ImportError:
    print("PyMuPDF required: pip install pymupdf", file=sys.stderr)
    sys.exit(2)

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PDF_DIR = ROOT / "Export" / "CivilDrawings" / "new with macros" / "PDF"
DEFAULT_FIXTURES = ROOT / "tests" / "regression"

# Title block and BOM column on the 11x17 Paisley sheets: right ~22 % of the page.
TITLE_COLUMN_FRACTION = 0.78
BASELINE_TOL_PT = 2.0
NUMBER_RE = re.compile(r"\d+(?:\.\d+)?")


# ---------------------------------------------------------------- fixtures

def find_fixture(root: Path, piece: str) -> Path | None:
    for sub in ("tuning", "holdout", ""):
        p = root / sub / f"{piece}.json" if sub else root / f"{piece}.json"
        if p.exists():
            return p
    return None


def all_fixtures(root: Path) -> list[Path]:
    out = []
    for sub in ("tuning", "holdout"):
        out.extend(sorted((root / sub).glob("*.json")))
    return out


def cumulative(segments: list[float]) -> list[float]:
    out, s = [0.0], 0.0
    for seg in segments:
        s += float(seg)
        out.append(s)
    return out


# ---------------------------------------------------------------- PDF text

def page_lines(page):
    """(dir, bbox, text) for every non-empty text line."""
    out = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = "".join(span["text"] for span in line["spans"]).strip()
            if text:
                out.append((tuple(line["dir"]), tuple(line["bbox"]), text))
    return out


def numbers_in(text: str) -> list[float]:
    joined = re.sub(r"(?<=\d) (?=\d{3}\b)", "", text)
    return [float(n) for n in NUMBER_RE.findall(joined)]


def squash(text: str) -> str:
    return re.sub(r"\s+", "", text).upper()


def words(text: str) -> list[str]:
    out = []
    for w in re.split(r"\s+", text.upper()):
        w = w.strip("()[],;:")
        if w and re.search(r"[A-Z0-9]", w):
            out.append(w)
    return out


def drawing_area(page, lines):
    """Canvas only: left of the BOM/title column and above the bottom title strip."""
    limit = page.rect.width * TITLE_COLUMN_FRACTION
    bottom = page.rect.height * 0.92
    return [ln for ln in lines if ln[1][0] < limit and ln[1][1] < bottom]


def whole_word(hay: str, needle: str) -> bool:
    if not needle:
        return False
    return re.search(
        r"(?<![A-Z0-9])" + re.escape(needle.upper()) + r"(?![A-Z0-9])",
        (hay or "").upper(),
    ) is not None


def title_area(page, lines):
    limit = page.rect.width * TITLE_COLUMN_FRACTION
    return [ln for ln in lines if ln[1][0] >= limit]


# ---------------------------------------------------------------- scoring helpers

class Tally:
    def __init__(self):
        self.c = Counter()

    def add(self, result: str):
        key = {"PASS": "pass", "PASS-within-1": "within1", "KNOWN-DEVIATION": "known",
               "MISSING": "missing", "EXTRA": "extra"}.get(result, "fail")
        self.c[key] += 1

    def total(self):
        return sum(v for k, v in self.c.items() if k != "extra")

    def summary(self):
        return " ".join(f"{k}={self.c[k]}" for k in ("pass", "within1", "known", "fail", "missing", "extra")) + f" of {self.total()}"


def report(lines_out: list[str], result: str, name: str, detail: str = ""):
    line = f"[PdfCheck] {result} {name}" + (f" {detail}" if detail else "")
    print(line)
    lines_out.append(line)


def fmt(vals):
    return "|".join(f"{v:.0f}" for v in vals)


# ---------------------------------------------------------------- page 1

def _clusters(items, edge):
    items = sorted(items, key=lambda it: it[edge])
    out, cur = [], []
    for it in items:
        if cur and abs(it[edge] - cur[0][edge]) > BASELINE_TOL_PT:
            out.append(cur)
            cur = []
        cur.append(it)
    if cur:
        out.append(cur)
    return out


def rotated_rows(page):
    """Rows of absolute numbers from rotated text (dir ~ (0,-1)), plus the rotated-number count.

    Rows above the panel share the bbox bottom (y1) and rows below share the top (y0), so
    items are clustered on y1 first and the leftovers on y0. A row needs a 0 and one more value.
    """
    items = []
    for d, bbox, text in drawing_area(page, page_lines(page)):
        if abs(d[1]) < 0.9:
            continue
        for n in numbers_in(text):
            items.append((bbox[1], bbox[3], n))
    rows, left = [], []
    for edge, pool in ((1, items), (0, None)):
        pool = left if pool is None else pool
        left = []
        for c in _clusters(pool, edge):
            nums = sorted(set(it[2] for it in c))
            if len(nums) >= 2 and 0.0 in nums:
                rows.append((c[0][edge], nums))
            else:
                left.extend(c)
    return rows, len(items)


def station_match(want, got):
    """(matched fraction of the longer list, summed diff of matched stations)."""
    used, matched, diff = set(), 0, 0.0
    for w in want:
        best = None
        for k, g in enumerate(got):
            if k not in used and abs(w - g) <= 1.0 and (best is None or abs(w - g) < abs(w - got[best])):
                best = k
        if best is not None:
            used.add(best)
            matched += 1
            diff += abs(w - got[best])
    return matched / max(len(want), len(got)), diff


def check_page1(fixture: dict, pdf: Path, out: list[str]) -> Tally:
    tally = Tally()
    doc = fitz.open(pdf)
    rows, rotated = rotated_rows(doc[0])
    doc.close()
    length = float(fixture.get("lengthMm", 0))
    expected = []
    for r in fixture.get("rows", []):
        segs = [float(s) for s in r.get("segments", [])]
        if length and abs(sum(segs) - length) > float(r.get("sumTol", 0)) + 0.5:
            report(out, "FIXTURE-ERROR", r["label"], f"sum={sum(segs):.0f} length={length:.0f} (excluded)")
            continue
        expected.append(r)
    note = "" if rows else " (no rotated absolute rows: relative-only dim type?)"
    report(out, "INFO", f"page1 {pdf.name}",
           f"pdfRows={len(rows)} rotatedNumbers={rotated} fixtureRows={len(expected)}{note}")

    # Pair only rows sharing at least half their stations; anything else is MISSING + EXTRA.
    pairs = []
    for i, r in enumerate(expected):
        want = cumulative(r["segments"])
        for j, (_, got) in enumerate(rows):
            frac, diff = station_match(want, got)
            if frac >= 0.5:
                pairs.append(((1.0 - frac) * 1000.0 + diff, i, j))
    pairs.sort()
    hit, used = {}, set()
    for cost, i, j in pairs:
        if i in hit or j in used:
            continue
        hit[i] = j
        used.add(j)

    seen = Counter()
    counts = Counter(r["label"] for r in expected)
    for i, r in enumerate(expected):
        name = r["label"]
        if counts[name] > 1:
            seen[name] += 1
            name += f"#{seen[name]}"
        want = cumulative(r["segments"])
        known = r.get("knownDeviation")
        if i not in hit:
            result = "KNOWN-DEVIATION" if known else "MISSING"
            report(out, result, name, f"expected absolutes={fmt(want)}")
            tally.add(result)
            continue
        got = rows[hit[i]][1]
        if len(got) == len(want):
            diff = max(abs(a - b) for a, b in zip(want, got))
            result = "PASS" if diff < 0.5 else ("PASS-within-1" if diff <= 1.0 else "FAIL")
        else:
            result = "FAIL"
        closure = abs(got[-1] - length) if got and length else None
        first_diff = abs(got[0]) if got else None
        if result.startswith("PASS") and (
            (first_diff is not None and first_diff > 0.5)
            or (closure is not None and closure > 1.0)
        ):
            result = "FAIL"
        if result == "FAIL" and known:
            result = "KNOWN-DEVIATION"
        ends = ""
        if got:
            ends = f" first={got[0]:.0f} last={got[-1]:.0f}"
            if closure is not None:
                ends += f" closure={closure:.1f}"
        detail = "" if result == "PASS" else f"expected={fmt(want)} got={fmt(got)}"
        detail = (detail + ends).strip()
        report(out, result, name, detail)
        tally.add(result)
    for j, (_, got) in enumerate(rows):
        if j not in used:
            report(out, "EXTRA", "pdf row", fmt(got))
            tally.add("EXTRA")
    return tally


# ---------------------------------------------------------------- height columns

def levels(segments, reverse=False):
    seq = list(reversed(segments)) if reverse else list(segments)
    return cumulative(seq)


def panel_x_range(page):
    """Panel ends from the rotated page-1 rows (their 0 and end values sit on the panel ends)."""
    xs = []
    for d, bbox, text in drawing_area(page, page_lines(page)):
        if abs(d[1]) >= 0.9 and numbers_in(text):
            xs.extend([bbox[0], bbox[2]])
    return (min(xs), max(xs)) if xs else None


COLUMN_MIN_SPREAD_PT = 20.0


def height_columns(page, height_mm):
    """Absolute vertical columns: horizontal numbers sharing a right edge (left of the panel) or a left
    edge (right of it), spread vertically. The panel's x-range comes from the rotated rows when there are
    any; otherwise the side is judged against the middle of all numbers.
    Returns [(side, edge_x, sorted levels, zero_printed)]."""
    span = panel_x_range(page)
    items = []
    for d, bbox, text in drawing_area(page, page_lines(page)):
        if abs(d[1]) > 0.1 or d[0] < 0.9:
            continue
        nums = numbers_in(text)
        if len(nums) != 1 or nums[0] > height_mm + 1.0:
            continue
        if span and span[0] - 1.0 <= (bbox[0] + bbox[2]) / 2 <= span[1] + 1.0:
            continue
        items.append((bbox, nums[0]))
    if not items:
        return []
    xs = sorted((b[0] + b[2]) / 2 for b, _ in items)
    middle = (span[0] + span[1]) / 2 if span else xs[len(xs) // 2]
    out, seen = [], set()
    for side, edge in (("left", 2), ("right", 0)):
        pool = [it for it in items if ((it[0][0] + it[0][2]) / 2 < middle) == (side == "left")]
        pool.sort(key=lambda it: it[0][edge])
        groups, cur = [], []
        for it in pool:
            if cur and abs(it[0][edge] - cur[0][0][edge]) > 1.5:
                groups.append(cur)
                cur = []
            cur.append(it)
        if cur:
            groups.append(cur)
        for g in groups:
            ys = [b[1] for b, _ in g]
            key = frozenset((round(b[0]), round(b[1])) for b, _ in g)
            vals = sorted(set(v for _, v in g))
            if len(vals) < 2 or max(ys) - min(ys) < COLUMN_MIN_SPREAD_PT or key in seen:
                continue
            # Without the panel span, aligned row numbers look like columns; an absolute column starts at 0.
            if not span and 0.0 not in vals:
                continue
            seen.add(key)
            out.append((side, g[0][0][edge], vals, 0.0 in vals))
    return out


def check_heights(fixture: dict, pdf: Path, out: list[str]) -> Tally:
    tally = Tally()
    height = float(fixture.get("heightMm", 0))
    expected = [r for r in fixture.get("rows", []) if r.get("heights")]
    if not expected or height <= 0:
        return tally
    good = []
    for r in expected:
        s = sum(float(v) for v in r["heights"])
        if abs(s - height) > float(r.get("sumTol", 0)) + 0.5:
            report(out, "FIXTURE-ERROR", f"HCOL {r['label']}", f"sum={s:.0f} height={height:.0f} (excluded)")
            continue
        good.append(r)
    doc = fitz.open(pdf)
    cols = height_columns(doc[0], height)
    doc.close()
    report(out, "INFO", f"heights {pdf.name}", f"pdfColumns={len(cols)} fixtureColumns={len(good)}")

    pairs = []
    for i, r in enumerate(good):
        for rev in (False, True):
            want = levels([float(v) for v in r["heights"]], rev)
            for j, (_, _, got, _) in enumerate(cols):
                frac, diff = station_match(want, got)
                if frac >= 0.5:
                    pairs.append(((1.0 - frac) * 1000.0 + diff, i, j, rev))
    pairs.sort()
    hit, used = {}, set()
    for cost, i, j, rev in pairs:
        if i in hit or j in used:
            continue
        hit[i] = (j, rev)
        used.add(j)
    for i, r in enumerate(good):
        name = f"HCOL {r['label']}"
        if i not in hit:
            result = "KNOWN-DEVIATION" if r.get("knownDeviation") else "MISSING"
            report(out, result, name, f"expected levels={fmt(levels(r['heights']))}")
            tally.add(result)
            continue
        j, rev = hit[i]
        side, _, got, zero = cols[j]
        want = levels([float(v) for v in r["heights"]], rev)
        if len(got) == len(want):
            diff = max(abs(a - b) for a, b in zip(want, got))
            result = "PASS" if diff < 0.5 else ("PASS-within-1" if diff <= 1.0 else "FAIL")
        else:
            result = "FAIL"
        if not zero and result.startswith("PASS"):
            result = "FAIL"
        if result == "FAIL" and r.get("knownDeviation"):
            result = "KNOWN-DEVIATION"
        info = f"side={side}" + (f" (fixture {r['heightSide']})" if r.get("heightSide") and r["heightSide"] != side else "")
        detail = f"{info} 0 printed: {'yes' if zero else 'no'}"
        if result != "PASS":
            detail += f" expected={fmt(want)} got={fmt(got)}"
        report(out, result, name, detail)
        tally.add(result)
    for j, (side, x, got, zero) in enumerate(cols):
        if j not in used:
            report(out, "EXTRA", f"pdf column {side} x={x:.0f}", f"{fmt(got)} 0 printed: {'yes' if zero else 'no'}")
            tally.add("EXTRA")
    return tally


# ---------------------------------------------------------------- sections

def check_sections(fixture: dict, pdfs: list[Path], out: list[str]) -> Tally:
    tally = Tally()
    targets = fixture.get("sections") or []
    if not targets:
        return tally
    pool = Counter()
    text_blob = []
    for pdf in pdfs:
        doc = fitz.open(pdf)
        for page in doc:
            for _, _, text in drawing_area(page, page_lines(page)):
                text_blob.append(text.upper())
                for n in numbers_in(text):
                    pool[n] += 1
        doc.close()
    blob = " ".join(text_blob)
    report(out, "INFO", "sections " + ", ".join(p.name for p in pdfs), f"numbers={sum(pool.values())}")
    for t in targets:
        tag = t.get("tag", "")
        need = Counter()
        for _ in range(int(t.get("count", 1))):
            for s in t.get("segments", []):
                need[float(s)] += 1
        short = [f"{v:.0f}x{n - pool[v]}" for v, n in need.items() if pool[v] < n]
        for v, n in need.items():
            pool[v] -= min(n, pool[v])
        tag_note = ""
        if re.search(r"\d", tag) and not whole_word(blob, tag):
            tag_note = f" (tag '{tag}' not printed)"
        if short:
            result = "KNOWN-DEVIATION" if t.get("knownDeviation") else "FAIL"
            report(out, result, f"A-A {tag}", f"missing {', '.join(short)}{tag_note}")
        else:
            result = "PASS"
            report(out, result, f"A-A {tag}", tag_note.strip())
        tally.add(result)
    return tally


def check_section_geometry(fixture: dict, pdfs: list[Path], out: list[str]) -> Tally:
    """Chain closes on the panel height, no 1 mm piece, each tier sums to the thickness."""
    tally = Tally()
    targets = fixture.get("sections") or []
    if not targets:
        return tally
    pool: set[float] = set()
    for pdf in pdfs:
        doc = fitz.open(pdf)
        for page in doc:
            for _, _, text in drawing_area(page, page_lines(page)):
                pool.update(numbers_in(text))
        doc.close()
    overall = next((t for t in targets if str(t.get("tag", "")).upper() == "OVERALL"), None)
    thickness = float(overall["segments"][0]) if overall and overall.get("segments") else None
    for t in targets:
        tag = str(t.get("tag", ""))
        segs = [float(s) for s in (t.get("segments") or [])]
        if any(0 < s < 2.0 for s in segs):
            report(out, "FAIL", f"A-A {tag} min", "expected segment under 2")
            tally.add("FAIL")
        if tag.upper() in ("OVERALL", "HEIGHT") or thickness is None or len(segs) < 2:
            continue
        total = sum(segs)
        ok = abs(total - thickness) <= 1.0
        report(out, "PASS" if ok else "FAIL", f"A-A {tag} thickness",
               f"sum={total:.0f} thickness={thickness:.0f}")
        tally.add("PASS" if ok else "FAIL")
    height = next((t for t in targets if str(t.get("tag", "")).upper() == "HEIGHT"), None)
    if height and height.get("segments"):
        segs = [float(s) for s in height["segments"]]
        total = sum(segs)
        found = any(abs(n - total) <= 1.0 for n in pool)
        report(out, "PASS" if found else "FAIL", "A-A chain closure", f"height={total:.0f}")
        tally.add("PASS" if found else "FAIL")
        short = sorted(n for n in pool if 0 < n < 2.0)
        if short:
            report(out, "FAIL", "A-A min segment", "pdf has " + ",".join(f"{n:.0f}" for n in short[:8]))
            tally.add("FAIL")
        else:
            report(out, "PASS", "A-A min segment", "")
            tally.add("PASS")
    return tally


# ---------------------------------------------------------------- rebar

def check_rebar(fixture: dict, pdfs: list[Path], out: list[str]) -> Tally:
    tally = Tally()
    rebar = fixture.get("rebar") or {}
    if not rebar:
        return tally
    lines = Counter()
    for pdf in pdfs:
        doc = fitz.open(pdf)
        for page in doc:
            for _, _, text in drawing_area(page, page_lines(page)):
                lines[squash(re.sub(r"(?<=\d) (?=\d{3}\b)", "", text))] += 1
        doc.close()
    report(out, "INFO", "rebar " + ", ".join(p.name for p in pdfs), f"lines={sum(lines.values())}")

    def occurrences(target: str) -> int:
        return sum(n for line, n in lines.items() if whole_word(line, target))

    for t in rebar.get("spacing", []):
        want = squash(t["text"])
        need = int(t.get("count", 1))
        got = occurrences(want)
        starred = occurrences(want.replace("@", "*"))
        if got >= need:
            result, detail = "PASS", ""
        elif got + starred >= need and starred:
            result, detail = "FAIL", f"printed with '*' ({starred}x) - XS_COMBINED_BOLT_DIM_CHARACTER / RewriteCombinedStars"
        else:
            result, detail = "FAIL", f"found {got} of {need}"
        if result == "FAIL" and t.get("knownDeviation"):
            result = "KNOWN-DEVIATION"
        report(out, result, f"spacing '{t['text']}'", detail)
        tally.add(result)
    for t in rebar.get("callouts", []):
        want = squash(t["text"])
        need = int(t.get("count", 1))
        got = occurrences(want)
        result = "PASS" if got >= need else ("KNOWN-DEVIATION" if t.get("knownDeviation") else "FAIL")
        report(out, result, f"callout '{t['text']}'", "" if result == "PASS" else f"found {got} of {need}")
        tally.add(result)
    return tally


# ---------------------------------------------------------------- title

def title_rows(page):
    items = sorted(title_area(page, page_lines(page)), key=lambda ln: ((ln[1][1] + ln[1][3]) / 2, ln[1][0]))
    rows, cur, yc = [], [], None
    for d, bbox, text in items:
        if abs(d[1]) > 0.5:
            continue
        y = (bbox[1] + bbox[3]) / 2
        if yc is None or abs(y - yc) <= 3.0:
            cur.append((bbox[0], text))
            yc = y if yc is None else yc
        else:
            rows.append(" ".join(t for _, t in sorted(cur)))
            cur, yc = [(bbox[0], text)], y
    if cur:
        rows.append(" ".join(t for _, t in sorted(cur)))
    return rows


def check_sheet_text(fixture: dict, pdf: Path, out: list[str]) -> Tally:
    """Whole-word checks. sheetText must appear; absentText must not. Page text, not a QA report."""
    tally = Tally()
    want = fixture.get("sheetText") or []
    banned = fixture.get("absentText") or []
    if not want and not banned:
        return tally
    doc = fitz.open(pdf)
    blob = "\n".join(page.get_text("text") for page in doc)
    doc.close()
    for item in want:
        text = item if isinstance(item, str) else str(item.get("text", ""))
        if whole_word(blob, text):
            report(out, "PASS", f"text '{text}'", "")
            tally.add("PASS")
        else:
            report(out, "FAIL", f"text '{text}'", "not a whole word on the sheet")
            tally.add("FAIL")
    for item in banned:
        text = item if isinstance(item, str) else str(item.get("text", ""))
        if whole_word(blob, text):
            report(out, "FAIL", f"absent '{text}'", "printed")
            tally.add("FAIL")
        else:
            report(out, "PASS", f"absent '{text}'", "")
            tally.add("PASS")
    return tally


def check_title(fixture: dict, pdf: Path, out: list[str]) -> Tally:
    tally = Tally()
    title = fixture.get("title") or {}
    if not title:
        return tally
    known = fixture.get("titleKnownDeviations") or {}
    doc = fitz.open(pdf)
    rows = title_rows(doc[0])
    doc.close()
    region_words = Counter(w for r in rows for w in words(r))
    report(out, "INFO", f"title {pdf.name}", f"rows={len(rows)}")
    for key, value in title.items():
        want = words(value)
        row_hit = next((r for r in rows if squash(value) in squash(r)
                        or all(w in words(r) for w in want)), None)
        if row_hit is not None:
            result, detail = "PASS", ""
        elif want and all(region_words[w] > 0 for w in want):
            result, detail = "PASS", "(words split across title rows)"
        else:
            near = max(rows, key=lambda r: len(set(words(r)) & set(want)), default="")
            if not set(words(near)) & set(want):
                near = ""
            result = "KNOWN-DEVIATION" if key in known else "FAIL"
            detail = (f"printed '{near}'" if near else "not in title column") + (f" - {known[key]}" if key in known else "")
        report(out, result, f"title {key} '{value}'", detail)
        tally.add(result)
    return tally


# ---------------------------------------------------------------- driver

def sheet(pdf_dir: Path, piece: str, suffix: str) -> Path | None:
    p = pdf_dir / f"{piece}{suffix}.pdf"
    return p if p.exists() else None


def append_scores(csv_path: Path, run: str, piece: str, set_name: str, t: Tally):
    if t.total() == 0 and t.c["extra"] == 0:
        return
    new = not csv_path.exists()
    csv_path.parent.mkdir(parents=True, exist_ok=True)
    with csv_path.open("a", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        if new:
            w.writerow(["run", "time", "piece", "set", "pass", "within1", "known", "fail", "missing", "extra", "total"])
        w.writerow([run, dt.datetime.now().replace(microsecond=0).isoformat(), piece, set_name,
                    t.c["pass"], t.c["within1"], t.c["known"], t.c["fail"], t.c["missing"], t.c["extra"], t.total()])


def check_piece(fixture_path: Path, pdf_dir: Path, csv_path: Path | None, run: str) -> list[str]:
    fixture = json.loads(fixture_path.read_text(encoding="utf-8"))
    piece = fixture["piece"]
    out: list[str] = []
    report(out, "INFO", piece, f"fixture={fixture_path} set={fixture.get('set', '?')}")
    p1 = sheet(pdf_dir, piece, "_-_1")
    if p1 is None:
        report(out, "MISSING", f"{piece}_-_1.pdf", f"in {pdf_dir}")
        return out
    p2 = sheet(pdf_dir, piece, "_-_2_Sections_3D") or sheet(pdf_dir, piece, "_-_2")
    rebar_pdfs = [p for p in (sheet(pdf_dir, piece, "_-_3"), sheet(pdf_dir, piece, "_-_4")) if p]
    rebar_pdfs += sorted(pdf_dir.glob(f"{piece}_-_[5-9].pdf"))

    checks = [("page1", check_page1(fixture, p1, out)), ("heights", check_heights(fixture, p1, out))]
    for trial in sorted(pdf_dir.glob(f"{piece}_-_1__dimtype-*.pdf")):
        name = trial.stem.split("__dimtype-", 1)[1]
        checks.append((f"page1:{name}", check_page1(fixture, trial, out)))
        checks.append((f"heights:{name}", check_heights(fixture, trial, out)))
    section_pdfs = [p2] if p2 else [p1]
    checks.append(("sections", check_sections(fixture, section_pdfs, out)))
    checks.append(("sectionGeometry", check_section_geometry(fixture, section_pdfs, out)))
    checks.append(("rebar", check_rebar(fixture, rebar_pdfs or [p1], out)))
    checks.append(("title", check_title(fixture, p1, out)))
    checks.append(("sheetText", check_sheet_text(fixture, p1, out)))

    for name, t in checks:
        if t.total() or t.c["extra"]:
            report(out, "SUMMARY", f"{piece} pdf:{name}", t.summary())
            if csv_path is not None:
                append_scores(csv_path, run, piece, f"pdf:{name}", t)
    return out


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pdf-dir", type=Path, default=DEFAULT_PDF_DIR)
    ap.add_argument("--fixtures", type=Path, default=DEFAULT_FIXTURES)
    ap.add_argument("--piece", action="append", help="piece mark; repeatable. Default: every fixture")
    ap.add_argument("--expect", type=Path, help="score against this fixture file instead of the lookup")
    ap.add_argument("--csv", type=Path, help="scores file (default: <pdf-dir>/../regression_scores.csv)")
    ap.add_argument("--no-csv", action="store_true")
    ap.add_argument("--run-tag", default="untagged")
    ap.add_argument("--log", type=Path, help="also write the report lines here")
    args = ap.parse_args(argv)

    pdf_dir = args.pdf_dir.resolve()
    csv_path = None if args.no_csv else (args.csv or pdf_dir.parent / "regression_scores.csv")

    if args.expect:
        fixtures = [args.expect]
    elif args.piece:
        fixtures = []
        for piece in args.piece:
            f = find_fixture(args.fixtures, piece)
            if f is None:
                print(f"[PdfCheck] no fixture for {piece} under {args.fixtures}", file=sys.stderr)
            else:
                fixtures.append(f)
    else:
        fixtures = all_fixtures(args.fixtures)
    if not fixtures:
        print("[PdfCheck] nothing to check", file=sys.stderr)
        return 1

    lines: list[str] = []
    for f in fixtures:
        lines.extend(check_piece(f, pdf_dir, csv_path, args.run_tag))
    if args.log:
        args.log.parent.mkdir(parents=True, exist_ok=True)
        args.log.write_text("\n".join(lines) + "\n", encoding="utf-8")
    if csv_path is not None:
        print(f"[PdfCheck] scores -> {csv_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
