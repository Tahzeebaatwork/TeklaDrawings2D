"""Bind shop sheets into a 4-page engineer fabrication booklet PDF."""
import argparse
import os
import shutil
import sys
from pathlib import Path

import fitz

ROOT = Path(__file__).resolve().parents[1]
PDF_DIR = ROOT / "Export" / "CivilDrawings" / "new with macros" / "PDF"


def sheet_paths(mark):
    # Standard engineer booklet sequence:
    # Page 1: Hardware Elevation (_1.pdf)
    # Page 2: 3D Isometric View & Sections (_2_Sections_3D.pdf)
    # Page 3: Rebar Placing Elevation (_2.pdf)
    # Page 4: BBS Reinforcing Schedule (_3.pdf)

    p1 = PDF_DIR / f"{mark}_-_1.pdf"
    p2 = PDF_DIR / f"{mark}_-_2_Sections_3D.pdf"
    p3 = PDF_DIR / f"{mark}_-_2.pdf"
    p4 = PDF_DIR / f"{mark}_-_3.pdf"

    p_alt4 = PDF_DIR / f"{mark}_-_4.pdf"
    if p_alt4.exists():
        return [p1, p2 if p2.exists() else p3, p3 if p2.exists() else p4, p_alt4]

    if p2.exists():
        return [p1, p2, p3, p4]
    return [p1, p3, p4]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mark", default="W10-175")
    args = parser.parse_args()
    pages = sheet_paths(args.mark)
    present = [p for p in pages if p.exists()]
    missing = [p.name for p in pages if not p.exists()]
    if not present:
        print(f"No sheet PDFs found in {PDF_DIR}")
        return 1
    if missing:
        print(f"Missing pages: {', '.join(missing)}")

    out = PDF_DIR / f"P22-132.{args.mark}.Rev 2.pdf"
    book = fitz.open()
    for idx, path in enumerate(present):
        src = fitz.open(path)
        # Redact and update title block page number to be sequential (PAGE - 1, PAGE - 2, ...)
        page_num = idx + 1
        for p in src:
            rect = fitz.Rect(1140, 780, 1185, 793)
            p.add_redact_annot(rect, fill=(1, 1, 1))
            p.apply_redactions()
            p.insert_text(fitz.Point(1141.6, 789.8), f"PAGE - {page_num}", fontsize=8.8, fontname="helv")
        book.insert_pdf(src)
        src.close()
        print(f"Added {path.name} as PAGE - {page_num}")
    book.save(out)
    book.close()
    print(f"Wrote {out} ({len(present)} pages)")
    return 0 if len(present) >= 3 else 1


if __name__ == "__main__":
    sys.exit(main())
