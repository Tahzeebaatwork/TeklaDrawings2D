"""Summarise coordcheck_<runtag>.csv from a --coord-check run (diag1).

Prints the header lines, the probe rules, ratio statistics and every flagged row, so the
answers (d) and (f) in docs/investigation/coordcheck_diag1.md can be filled in.

  python Scripts/summarize_coordcheck.py --run-tag diag1
  python Scripts/summarize_coordcheck.py --csv "Export/CivilDrawings/PDF/coordcheck_diag1.csv"
"""
import argparse
import csv
import io
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RATIO_TOL = 0.001


def find_csv(tag):
    hits = sorted((ROOT / "Export").rglob("coordcheck_" + tag + ".csv"), key=lambda p: p.stat().st_mtime, reverse=True)
    return hits[0] if hits else None


def num(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run-tag", default="diag1")
    ap.add_argument("--csv")
    args = ap.parse_args()
    path = Path(args.csv) if args.csv else find_csv(args.run_tag)
    if not path or not path.exists():
        print("[CoordSummary] no coordcheck_%s.csv found under Export/" % args.run_tag)
        return 1
    print("[CoordSummary] " + str(path))

    text = path.read_text(encoding="utf-8-sig")
    headers = [l[2:] for l in text.splitlines() if l.startswith("# ")]
    rows = list(csv.DictReader(io.StringIO("\n".join(l for l in text.splitlines() if not l.startswith("# ")))))
    for h in headers:
        print("  " + h)

    by_kind = defaultdict(list)
    for r in rows:
        by_kind[r["kind"]].append(r)

    print("\n== views / handedness / layout")
    for k in ("view", "hand", "layout"):
        for r in by_kind[k]:
            print("  %-6s %-5s %-11s scale=%-4s ratio=%-20s %s %s" % (
                r["piece"], k, r["where"], r["scale"], r["ratio"], r["flags"], r["detail"]))

    print("\n== probe")
    for r in by_kind["probe"]:
        print("  1:%-3s %-8s %-6s passed=%-5s read=%-8s kidDist=%-8s printed=%s %s" % (
            r["scale"], r["type"], r["label"], r["dist_passed"], r["dist_read"], r["offset_view"], r["value_mm"], r["flags"]))
    for r in by_kind["probe-rule"]:
        print("  RULE 1:%-3s %-8s %-6s %s (%s)" % (r["scale"], r["type"], r["label"], r["flags"], r["detail"]))
    if not by_kind["probe"]:
        print("  (no probe rows: run once with --coord-probe)")

    dims = by_kind["dim"]
    print("\n== dimension sets: %d" % len(dims))
    ratios = [num(r["ratio"]) for r in dims if num(r["ratio"]) is not None]
    if ratios:
        off = [x for x in ratios if abs(x - 1.0) > RATIO_TOL]
        print("  value/|A-B|: n=%d min=%.4f max=%.4f off-by->%.3f: %d" % (len(ratios), min(ratios), max(ratios), RATIO_TOL, len(off)))
    flags = Counter(f for r in dims for f in r["flags"].split() if f)
    for f, n in flags.most_common():
        print("  %-22s %d" % (f, n))
    for r in dims:
        if any(f in r["flags"] for f in ("RATIO!=1", "READBACK-INVALID", "ABS-READS-RELATIVE")):
            print("  %s %-7s '%s' %s len=%s value=%s ratio=%s %s" % (
                r["piece"], r["where"], r["label"], r["type"], r["our_len_mm"], r["value_mm"], r["ratio"], r["flags"]))

    overlap = defaultdict(int)
    for r in dims:
        if r["where"] == "page1" and num(r["overlap_n"]):
            overlap[(r["piece"], r["label"])] += int(num(r["overlap_n"]))
    if overlap:
        print("\n== TEXT-OVERLAP-LIKELY segments per page-1 row")
        for (piece, label), n in sorted(overlap.items(), key=lambda kv: -kv[1]):
            print("  %-8s %-40s %d" % (piece, label, n))

    print("\n== height columns")
    for r in by_kind["hcol"]:
        print("  %-8s %-34s %-9s pts=%-3s %s %s" % (r["piece"], r["label"], r["type"], r["points"], r["detail"], r["flags"]))
    for r in by_kind["hcol-skip"]:
        print("  %-8s %-34s skipped: %s" % (r["piece"], r["label"], r["detail"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
